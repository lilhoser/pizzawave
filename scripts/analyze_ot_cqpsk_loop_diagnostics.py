#!/usr/bin/env python3
"""Analyze passive CQPSK timing/carrier diagnostics against OP25 same-IQ labels."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import statistics
from datetime import datetime
from pathlib import Path
from typing import Any


STAMP = r"(?P<stamp>\d{2}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d+)"
START_RE = re.compile(STAMP + r"(?: \[\d+\])? PLAYBACK_START sample_index=0")
TSBK_RE = re.compile(STAMP + r" \[\d+\] NAC 0x(?P<nac>[0-9a-fA-F]{3}) TSBK: op=")
LOOP_RE = re.compile(r"^TR_LOOP_(?P<loop>GARDNER|COSTAS) (?P<fields>.*)$")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_stamp(value: str) -> datetime:
    return datetime.strptime(value, "%m/%d/%y %H:%M:%S.%f")


def parse_op25_rates(path: Path, duration: int) -> list[int]:
    text = path.read_text(encoding="utf-8", errors="replace")
    start = START_RE.search(text)
    if not start:
        raise ValueError(f"{path}: missing PLAYBACK_START")
    origin = parse_stamp(start.group("stamp"))
    rates = [0] * duration
    for match in TSBK_RE.finditer(text):
        elapsed = (parse_stamp(match.group("stamp")) - origin).total_seconds()
        if 0 <= elapsed < duration:
            rates[int(elapsed)] += 1
    return rates


def numeric(value: str) -> int | float:
    return float(value) if any(char in value for char in ".eE") else int(value)


def parse_loop_rows(path: Path) -> dict[int, dict[str, int | float]]:
    rows: dict[int, dict[str, int | float]] = {}
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        match = LOOP_RE.match(line)
        if not match:
            continue
        fields = dict(token.split("=", 1) for token in match.group("fields").split())
        interval = int(fields.pop("interval"))
        prefix = match.group("loop").lower()
        row = rows.setdefault(interval, {"interval": interval})
        row.update({f"{prefix}_{key}": numeric(value) for key, value in fields.items()})
    complete = {
        interval: row
        for interval, row in rows.items()
        if any(key.startswith("gardner_") for key in row)
        and any(key.startswith("costas_") for key in row)
    }
    if not complete:
        raise ValueError(f"{path}: no complete loop intervals")
    return complete


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def summarize(values: list[float]) -> dict[str, float | int | None]:
    if not values:
        return {"count": 0, "mean": None, "median": None, "p10": None, "p90": None}
    return {
        "count": len(values),
        "mean": round(statistics.fmean(values), 9),
        "median": round(statistics.median(values), 9),
        "p10": round(percentile(values, 0.1), 9),
        "p90": round(percentile(values, 0.9), 9),
    }


def auc(healthy: list[float], degraded: list[float]) -> float | None:
    if not healthy or not degraded:
        return None
    wins = 0.0
    for good in healthy:
        for bad in degraded:
            wins += 1.0 if good > bad else 0.5 if good == bad else 0.0
    return wins / (len(healthy) * len(degraded))


def pearson(xs: list[float], ys: list[float]) -> float | None:
    if len(xs) < 2 or len(xs) != len(ys):
        return None
    x_mean = statistics.fmean(xs)
    y_mean = statistics.fmean(ys)
    numerator = sum((x - x_mean) * (y - y_mean) for x, y in zip(xs, ys))
    x_energy = sum((x - x_mean) ** 2 for x in xs)
    y_energy = sum((y - y_mean) ** 2 for y in ys)
    denominator = math.sqrt(x_energy * y_energy)
    return numerator / denominator if denominator else None


def balanced_accuracy(
    healthy: list[float], degraded: list[float], threshold: float, healthy_above: bool
) -> float | None:
    if not healthy or not degraded:
        return None
    classify = (lambda value: value >= threshold) if healthy_above else (lambda value: value <= threshold)
    sensitivity = sum(classify(value) for value in healthy) / len(healthy)
    specificity = sum(not classify(value) for value in degraded) / len(degraded)
    return (sensitivity + specificity) / 2


def metric_report(
    metric: str,
    rows: list[dict[str, Any]],
    healthy_min: int,
    degraded_max: int,
) -> dict[str, Any]:
    healthy = [float(row[metric]) for row in rows if row["op25Rate"] >= healthy_min]
    degraded = [float(row[metric]) for row in rows if row["op25Rate"] <= degraded_max]
    raw_auc = auc(healthy, degraded)
    by_capture: dict[str, Any] = {}
    directions: list[bool] = []
    for trigger in sorted({row["trigger"] for row in rows}):
        capture_rows = [row for row in rows if row["trigger"] == trigger]
        good = [float(row[metric]) for row in capture_rows if row["op25Rate"] >= healthy_min]
        bad = [float(row[metric]) for row in capture_rows if row["op25Rate"] <= degraded_max]
        capture_auc = auc(good, bad)
        if capture_auc is not None:
            directions.append(capture_auc >= 0.5)
        by_capture[trigger] = {
            "systemShortName": capture_rows[0]["systemShortName"],
            "healthy": summarize(good),
            "degraded": summarize(bad),
            "aucHealthyAbove": round(capture_auc, 6) if capture_auc is not None else None,
        }

    holdout_scores: dict[str, float | None] = {}
    for trigger in sorted({row["trigger"] for row in rows}):
        train = [row for row in rows if row["trigger"] != trigger]
        test = [row for row in rows if row["trigger"] == trigger]
        train_good = [float(row[metric]) for row in train if row["op25Rate"] >= healthy_min]
        train_bad = [float(row[metric]) for row in train if row["op25Rate"] <= degraded_max]
        test_good = [float(row[metric]) for row in test if row["op25Rate"] >= healthy_min]
        test_bad = [float(row[metric]) for row in test if row["op25Rate"] <= degraded_max]
        if not train_good or not train_bad:
            holdout_scores[trigger] = None
            continue
        healthy_above = statistics.median(train_good) >= statistics.median(train_bad)
        threshold = (statistics.median(train_good) + statistics.median(train_bad)) / 2
        score = balanced_accuracy(test_good, test_bad, threshold, healthy_above)
        holdout_scores[trigger] = round(score, 6) if score is not None else None

    correlation = pearson(
        [float(row[metric]) for row in rows],
        [float(row["op25Rate"]) for row in rows],
    )
    valid_holdouts = [score for score in holdout_scores.values() if score is not None]
    return {
        "metric": metric,
        "healthy": summarize(healthy),
        "degraded": summarize(degraded),
        "aucHealthyAbove": round(raw_auc, 6) if raw_auc is not None else None,
        "separationAuc": round(max(raw_auc, 1 - raw_auc), 6) if raw_auc is not None else None,
        "healthyDirectionConsistentAcrossCaptures": bool(directions)
        and (all(directions) or not any(directions)),
        "pearsonVsOp25Rate": round(correlation, 6) if correlation is not None else None,
        "leaveOneCaptureOutBalancedAccuracy": holdout_scores,
        "meanLeaveOneCaptureOutBalancedAccuracy": round(statistics.fmean(valid_holdouts), 6)
        if valid_holdouts
        else None,
        "byCapture": by_capture,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run", action="append", required=True, metavar="TRIGGER,SYSTEM,LOG")
    parser.add_argument("--op25-root", type=Path, required=True)
    parser.add_argument("--healthy-min", type=int, default=15)
    parser.add_argument("--degraded-max", type=int, default=3)
    parser.add_argument("--exclude-start-seconds", type=int, default=5)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    rows: list[dict[str, Any]] = []
    inputs = []
    for specification in args.run:
        trigger, system, raw_path = specification.split(",", 2)
        path = Path(raw_path)
        loop_rows = parse_loop_rows(path)
        duration = max(loop_rows) + 1
        op25_path = args.op25_root / f"{trigger}.log"
        op25_rates = parse_op25_rates(op25_path, duration)
        for interval, loop_row in sorted(loop_rows.items()):
            if interval < args.exclude_start_seconds:
                continue
            rows.append(
                {
                    "trigger": trigger,
                    "systemShortName": system,
                    "op25Rate": op25_rates[interval],
                    **loop_row,
                }
            )
        inputs.append(
            {
                "trigger": trigger,
                "systemShortName": system,
                "diagnosticLog": str(path),
                "diagnosticLogSha256": sha256(path),
                "op25Log": str(op25_path),
                "op25LogSha256": sha256(op25_path),
                "completeIntervals": len(loop_rows),
            }
        )

    metrics = sorted(
        key
        for key in rows[0]
        if key.startswith("gardner_") or key.startswith("costas_")
    )
    reports = [metric_report(metric, rows, args.healthy_min, args.degraded_max) for metric in metrics]
    reports.sort(
        key=lambda report: (
            report["meanLeaveOneCaptureOutBalancedAccuracy"] or 0,
            report["separationAuc"] or 0,
        ),
        reverse=True,
    )
    result = {
        "schemaVersion": 1,
        "labels": {
            "source": "existing OP25 same-IQ replay",
            "healthyMinimumMessagesPerSecond": args.healthy_min,
            "degradedMaximumMessagesPerSecond": args.degraded_max,
            "excludedStartupSeconds": args.exclude_start_seconds,
        },
        "inputs": inputs,
        "rowCount": len(rows),
        "healthyRowCount": sum(row["op25Rate"] >= args.healthy_min for row in rows),
        "degradedRowCount": sum(row["op25Rate"] <= args.degraded_max for row in rows),
        "metrics": reports,
        "rows": rows,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(reports[:8], indent=2))
    print("analysisSha256", sha256(args.output))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
