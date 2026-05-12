#!/usr/bin/env python3
import argparse
import csv
import datetime as dt
import os
import re
import sqlite3
import subprocess
import sys
from collections import defaultdict


CSV_HEADER = (
    "ts_start,ts_end,scope,decode_lines,decode_zero,decode_nonzero,avg_decode_rate,"
    "grant_updates,retunes,calls_started,calls_concluded,update_not_grant,no_tx_recorded,"
    "recorder_exhausted,sample_stops,unable_source,tuning_err_samples,tuning_err_avg_abs_hz,tuning_err_max_abs_hz"
)

TR_TS_RE = re.compile(r"\[(?P<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]")
TR_SCOPE_RE = re.compile(r"\]\s+\((?:info|error|warning|debug)\)\s+\[(?P<scope>[^\]]+)\]", re.I)
DECODE_RE = re.compile(r"(?:decode|decoded)[^0-9-]*(?P<rate>-?\d+(?:\.\d+)?)\s*(?:/sec|per sec|hz)?", re.I)
TUNING_RE = re.compile(r"(?:tuning|tune)[^0-9-]*(?:error|err)[^0-9-]*(?P<hz>-?\d+(?:\.\d+)?)\s*hz", re.I)


def parse_args():
    parser = argparse.ArgumentParser(description="Prime pizzad TR health history from existing collector CSV and/or journald.")
    parser.add_argument("--database", default="/var/lib/pizzawave/pizzad.db")
    parser.add_argument("--service", default="trunk-recorder")
    parser.add_argument("--days", type=int, default=30)
    parser.add_argument("--csv", default="/var/lib/pizzapi/tr-health/summary_5m.csv")
    parser.add_argument("--skip-csv", action="store_true")
    parser.add_argument("--skip-journal", action="store_true")
    parser.add_argument("--chunk-hours", type=int, default=6)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def ensure_schema(conn):
    columns = {
        "decode_rate_total": "REAL NOT NULL DEFAULT 0",
        "update_not_grant": "INTEGER NOT NULL DEFAULT 0",
        "no_tx_recorded": "INTEGER NOT NULL DEFAULT 0",
        "recorder_exhausted": "INTEGER NOT NULL DEFAULT 0",
        "tuning_err_samples": "INTEGER NOT NULL DEFAULT 0",
        "tuning_err_total_abs_hz": "REAL NOT NULL DEFAULT 0",
        "tuning_err_max_abs_hz": "REAL NOT NULL DEFAULT 0",
    }
    existing = {row[1] for row in conn.execute("PRAGMA table_info(tr_health_samples)")}
    for name, definition in columns.items():
        if name not in existing:
            conn.execute(f"ALTER TABLE tr_health_samples ADD COLUMN {name} {definition}")


def parse_datetime(value):
    value = (value or "").strip()
    if not value:
        return None
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        try:
            parsed = dt.datetime.strptime(value.split(".")[0], "%Y-%m-%d %H:%M:%S")
        except ValueError:
            return None
    if parsed.tzinfo is None:
        parsed = parsed.astimezone()
    return parsed.astimezone(dt.timezone.utc)


def floor_five_minutes(value):
    value = value.astimezone(dt.timezone.utc)
    minute = value.minute - value.minute % 5
    return value.replace(minute=minute, second=0, microsecond=0)


def is_system_scope(scope):
    scope = (scope or "").strip()
    return bool(scope and scope.lower() != "global" and not scope.lower().startswith("source") and not scope.isdigit() and any(c.isalpha() for c in scope))


def bucket_from_lines(scope, start, lines):
    decode_lines = decode_zero = 0
    decode_total = 0.0
    tuning_samples = 0
    tuning_total = 0.0
    tuning_max = 0.0
    retunes = calls_started = calls_concluded = update_not_grant = no_tx = recorder_exhausted = sample_stops = unable_source = 0

    for line in lines:
        lower = line.lower()
        m = DECODE_RE.search(line)
        if m:
            rate = float(m.group("rate"))
            decode_lines += 1
            decode_total += rate
            if abs(rate) < 0.0001:
                decode_zero += 1
        tm = TUNING_RE.search(line)
        if tm:
            hz = abs(float(tm.group("hz")))
            tuning_samples += 1
            tuning_total += hz
            tuning_max = max(tuning_max, hz)
        if "retuning to control channel" in lower:
            retunes += 1
        if "starting p25 recorder" in lower:
            calls_started += 1
        if "concluding recorded call" in lower or "call concluded" in lower or "calls concluded" in lower:
            calls_concluded += 1
        if "update not grant" in lower or "this was an update" in lower:
            update_not_grant += 1
        if "only 0 recorders are available" in lower:
            recorder_exhausted += 1
        if ("no transmissions were recorded" in lower or "no transmission" in lower or "no tx" in lower
                or "not recording transmission" in lower):
            no_tx += 1
        if "has stopped receiving samples" in lower or "sample stop" in lower or "stopped samples" in lower:
            sample_stops += 1
        if "no source covering" in lower or ("unable" in lower and "source" in lower):
            unable_source += 1

    return {
        "window_start_utc": start,
        "window_end_utc": start + dt.timedelta(minutes=5),
        "scope": scope,
        "decode_lines": decode_lines,
        "decode_zero": decode_zero,
        "decode_zero_pct": 0 if decode_lines == 0 else decode_zero * 100.0 / decode_lines,
        "decode_rate_total": decode_total,
        "retunes": retunes,
        "calls_started": calls_started,
        "calls_concluded": calls_concluded,
        "update_not_grant": update_not_grant,
        "no_tx_recorded": no_tx,
        "recorder_exhausted": recorder_exhausted,
        "sample_stops": sample_stops,
        "unable_source": unable_source,
        "tuning_err_samples": tuning_samples,
        "tuning_err_total_abs_hz": tuning_total,
        "tuning_err_max_abs_hz": tuning_max,
    }


def rows_from_journal(service, start, end):
    args = [
        "journalctl",
        "-u", service,
        "--utc",
        "--since", start.strftime("%Y-%m-%d %H:%M:%S UTC"),
        "--until", end.strftime("%Y-%m-%d %H:%M:%S UTC"),
        "--no-pager",
        "-o", "cat",
    ]
    proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, errors="ignore")
    buckets = defaultdict(list)
    last_scope = None
    last_scope_ts = None
    assert proc.stdout is not None
    for line in proc.stdout:
        ts_match = TR_TS_RE.search(line)
        if not ts_match:
            continue
        ts = parse_datetime(ts_match.group("ts"))
        if ts is None:
            continue
        bucket = floor_five_minutes(ts)
        buckets[(bucket, "global")].append(line)
        scope_match = TR_SCOPE_RE.search(line)
        if scope_match and is_system_scope(scope_match.group("scope")):
            last_scope = scope_match.group("scope").strip()
            last_scope_ts = ts
            buckets[(bucket, last_scope)].append(line)
        elif last_scope and last_scope_ts and (ts - last_scope_ts).total_seconds() <= 600:
            lower = line.lower()
            if "has stopped receiving samples" in lower or "sample stop" in lower or "stopped samples" in lower:
                buckets[(bucket, last_scope)].append(line)
    _, err = proc.communicate()
    if proc.returncode != 0:
        raise RuntimeError(err.strip() or f"journalctl exited {proc.returncode}")
    return [bucket_from_lines(scope, start, lines) for (start, scope), lines in buckets.items()]


def rows_from_csv(path):
    if not path or not os.path.exists(path):
        return []
    rows = []
    with open(path, newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        for r in reader:
            start = parse_datetime(r.get("ts_start"))
            end = parse_datetime(r.get("ts_end"))
            if not start or not end:
                continue
            decode_lines = int(float(r.get("decode_lines") or 0))
            decode_zero = int(float(r.get("decode_zero") or 0))
            avg_decode = float(r.get("avg_decode_rate") or 0)
            tuning_samples = int(float(r.get("tuning_err_samples") or 0))
            tuning_avg = float(r.get("tuning_err_avg_abs_hz") or 0)
            rows.append({
                "window_start_utc": start,
                "window_end_utc": end,
                "scope": r.get("scope") or "global",
                "decode_lines": decode_lines,
                "decode_zero": decode_zero,
                "decode_zero_pct": 0 if decode_lines == 0 else decode_zero * 100.0 / decode_lines,
                "decode_rate_total": avg_decode * decode_lines,
                "retunes": int(float(r.get("retunes") or 0)),
                "calls_started": int(float(r.get("calls_started") or 0)),
                "calls_concluded": int(float(r.get("calls_concluded") or 0)),
                "update_not_grant": int(float(r.get("update_not_grant") or 0)),
                "no_tx_recorded": int(float(r.get("no_tx_recorded") or 0)),
                "recorder_exhausted": int(float(r.get("recorder_exhausted") or 0)),
                "sample_stops": int(float(r.get("sample_stops") or 0)),
                "unable_source": int(float(r.get("unable_source") or 0)),
                "tuning_err_samples": tuning_samples,
                "tuning_err_total_abs_hz": tuning_avg * tuning_samples,
                "tuning_err_max_abs_hz": float(r.get("tuning_err_max_abs_hz") or 0),
            })
    return rows


def write_rows(conn, rows):
    sql_delete = "DELETE FROM tr_health_samples WHERE window_start_utc=? AND window_end_utc=? AND scope=?"
    sql_insert = """
        INSERT INTO tr_health_samples (
            window_start_utc, window_end_utc, scope, decode_lines, decode_zero, decode_zero_pct,
            decode_rate_total, retunes, calls_started, calls_concluded, update_not_grant, no_tx_recorded, recorder_exhausted,
            sample_stops, unable_source, tuning_err_samples, tuning_err_total_abs_hz, tuning_err_max_abs_hz)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """
    with conn:
        for r in rows:
            start = r["window_start_utc"].isoformat()
            end = r["window_end_utc"].isoformat()
            scope = r["scope"]
            conn.execute(sql_delete, (start, end, scope))
            conn.execute(sql_insert, (
                start, end, scope, r["decode_lines"], r["decode_zero"], r["decode_zero_pct"],
                r["decode_rate_total"], r["retunes"], r["calls_started"], r["calls_concluded"],
                r["update_not_grant"], r["no_tx_recorded"], r["recorder_exhausted"], r["sample_stops"], r["unable_source"],
                r["tuning_err_samples"], r["tuning_err_total_abs_hz"], r["tuning_err_max_abs_hz"],
            ))


def main():
    args = parse_args()
    all_rows = []
    if not args.skip_csv:
        csv_rows = rows_from_csv(args.csv)
        all_rows.extend(csv_rows)
        print(f"loaded {len(csv_rows)} row(s) from collector CSV: {args.csv}")
    if not args.skip_journal:
        end = dt.datetime.now(dt.timezone.utc)
        start = end - dt.timedelta(days=max(1, args.days))
        cursor = start
        while cursor < end:
            chunk_end = min(end, cursor + dt.timedelta(hours=max(1, args.chunk_hours)))
            chunk_rows = rows_from_journal(args.service, cursor, chunk_end)
            all_rows.extend(chunk_rows)
            print(f"parsed {len(chunk_rows)} row(s) from journald {cursor.isoformat()} to {chunk_end.isoformat()}")
            cursor = chunk_end
    unique = {}
    for row in all_rows:
        key = (row["window_start_utc"], row["window_end_utc"], row["scope"])
        unique[key] = row
    rows = list(unique.values())
    print(f"prepared {len(rows)} unique health row(s)")
    if args.dry_run:
        return 0
    conn = sqlite3.connect(args.database)
    ensure_schema(conn)
    write_rows(conn, rows)
    conn.close()
    print(f"wrote {len(rows)} row(s) to {args.database}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
