#!/usr/bin/env python3
"""Read-only PizzaWave transcription bakeoff harness.

This script intentionally works off copied call audio and JSONL manifests. It
does not write to the PizzaWave database and does not retry production
transcriptions.
"""

from __future__ import annotations

import argparse
import csv
import json
import mimetypes
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path


REMOTE_COLLECT = r'''
import argparse, json, re, sqlite3, time

SEVERITY = [
    ("entrapment", re.compile(r"\b(entrapment|entrapped|trapped|pinned in|extrication)\b", re.I), 38),
    ("road closure", re.compile(r"\b(road closed|road closure|shut down|shutdown|blocked|blockage|all lanes|traffic blocked)\b", re.I), 34),
    ("serious wreck", re.compile(r"\b(wreck|crash|accident|rollover|overturned|vehicle fire|head on)\b", re.I), 28),
    ("injury", re.compile(r"\b(injury|injuries|injured|unconscious|not breathing|bleeding|trauma|fatality)\b", re.I), 28),
    ("fire", re.compile(r"\b(structure fire|working fire|smoke showing|flames?|fire alarm|brush fire)\b", re.I), 26),
    ("hazard", re.compile(r"\b(wires? down|tree down|gas leak|hazmat|spill|debris|flooding?)\b", re.I), 22),
    ("law emergency", re.compile(r"\b(shots? fired|shooting|stabbing|pursuit|robbery|burglary|assault|domestic)\b", re.I), 22),
]
LOCATION = re.compile(r"\b(georgetown|ooltewah|oodle|udall|woodwall|road|rd|street|st|avenue|ave|drive|dr|highway|hwy|pike|lane|ln|boulevard|blvd|intersection|mile marker|mm)\b", re.I)

p = argparse.ArgumentParser()
p.add_argument("--start", type=int, default=0)
p.add_argument("--end", type=int, default=0)
p.add_argument("--hours", type=int, default=24)
p.add_argument("--limit", type=int, default=100)
p.add_argument("--call-id", action="append", type=int, default=[])
args = p.parse_args()

now = int(time.time())
end = args.end or now
start = args.start or (end - args.hours * 3600)
conn = sqlite3.connect("/var/lib/pizzawave/pizzad.db")
conn.row_factory = sqlite3.Row
cur = conn.cursor()

call_ids = sorted(set(args.call_id))
params = [start, end]
extra = ""
if call_ids:
    placeholders = ",".join("?" for _ in call_ids)
    extra = f" OR id IN ({placeholders})"
    params.extend(call_ids)

rows = cur.execute(
    f"""
    SELECT id, start_time, stop_time, system_short_name, callstream_call_id,
           talkgroup, talkgroup_name, frequency, category, audio_path,
           transcription_status, quality_reason, transcription, raw_metadata_json
    FROM calls
    WHERE audio_path <> ''
      AND stop_time > start_time
      AND (
        (start_time >= ? AND start_time <= ?)
        {extra}
      )
    ORDER BY start_time DESC, id DESC
    """,
    params,
).fetchall()

scored = []
for row in rows:
    text = " ".join((row["transcription"] or "").split())
    severity = [(label, weight) for label, rx, weight in SEVERITY if rx.search(text)]
    score = sum(weight for _, weight in severity)
    locations = bool(LOCATION.search(text))
    if locations:
        score += 20
    if row["quality_reason"] != "ok":
        score += 22
    if row["transcription_status"] != "complete":
        score += 20
    if row["id"] in call_ids:
        score += 1000
    if score < 40 and row["id"] not in call_ids:
        continue
    item = dict(row)
    item["duration_seconds"] = int(row["stop_time"] - row["start_time"])
    item["candidate_score"] = score
    item["candidate_reasons"] = [label for label, _ in severity]
    if locations:
        item["candidate_reasons"].append("location-like text")
    if row["quality_reason"] != "ok":
        item["candidate_reasons"].append("quality_reason=" + row["quality_reason"])
    if row["transcription_status"] != "complete":
        item["candidate_reasons"].append("transcription_status=" + row["transcription_status"])
    item["transcription_preview"] = text[:280]
    item.pop("transcription", None)
    scored.append(item)

for item in sorted(scored, key=lambda r: (-r["candidate_score"], -r["start_time"]))[:args.limit]:
    print(json.dumps(item, separators=(",", ":")))
'''


def main() -> int:
    parser = argparse.ArgumentParser(description="PizzaWave transcription bakeoff harness")
    sub = parser.add_subparsers(dest="command", required=True)

    collect = sub.add_parser("collect-ssh", help="Collect candidate metadata and copy audio from a PizzaWave host over SSH/SCP")
    collect.add_argument("--host", required=True)
    collect.add_argument("--key")
    collect.add_argument("--hours", type=int, default=24)
    collect.add_argument("--start", type=int, default=0)
    collect.add_argument("--end", type=int, default=0)
    collect.add_argument("--limit", type=int, default=100)
    collect.add_argument("--call-id", action="append", default=[])
    collect.add_argument("--audio-root", default="/var/lib/pizzawave/audio")
    collect.add_argument("--output", required=True)
    collect.add_argument("--skip-audio", action="store_true")

    labels = sub.add_parser("make-label-template", help="Create a human-label JSONL template from a manifest")
    labels.add_argument("--manifest", required=True)
    labels.add_argument("--output", required=True)

    run = sub.add_parser("run-openai-compatible", help="Run an OpenAI-compatible /audio/transcriptions endpoint")
    run.add_argument("--manifest", required=True)
    run.add_argument("--engine-id", required=True)
    run.add_argument("--base-url", required=True)
    run.add_argument("--model", default="")
    run.add_argument("--api-key", default="")
    run.add_argument("--output", required=True)
    run.add_argument("--limit", type=int, default=0)

    score = sub.add_parser("score", help="Score engine outputs against operational fact labels")
    score.add_argument("--labels", required=True)
    score.add_argument("--results", required=True)
    score.add_argument("--output", required=True)

    args = parser.parse_args()
    if args.command == "collect-ssh":
        collect_ssh(args)
    elif args.command == "make-label-template":
        make_label_template(args)
    elif args.command == "run-openai-compatible":
        run_openai_compatible(args)
    elif args.command == "score":
        score_results(args)
    return 0


def ssh_base(key: str | None) -> list[str]:
    cmd = ["ssh", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes"]
    if key:
        cmd += ["-i", key]
    return cmd


def scp_base(key: str | None) -> list[str]:
    cmd = ["scp", "-q", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes"]
    if key:
        cmd += ["-i", key]
    return cmd


def collect_ssh(args: argparse.Namespace) -> None:
    out_dir = Path(args.output)
    audio_dir = out_dir / "audio"
    out_dir.mkdir(parents=True, exist_ok=True)
    audio_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = out_dir / "manifest.jsonl"

    remote_args = ["--hours", str(args.hours), "--limit", str(args.limit)]
    if args.start:
        remote_args += ["--start", str(args.start)]
    if args.end:
        remote_args += ["--end", str(args.end)]
    for call_id in args.call_id:
        remote_args += ["--call-id", str(call_id)]

    cmd = ssh_base(args.key) + [args.host, "python3", "-", *remote_args]
    proc = subprocess.run(cmd, input=REMOTE_COLLECT, text=True, capture_output=True, check=True)
    rows = [json.loads(line) for line in proc.stdout.splitlines() if line.strip()]

    with manifest_path.open("w", encoding="utf-8") as handle:
        for row in rows:
            local_audio = ""
            if not args.skip_audio:
                local_audio = copy_audio(args, row, audio_dir)
            row["local_audio_path"] = local_audio
            row["audio_url"] = f"/api/v1/calls/{row['id']}/audio"
            handle.write(json.dumps(row, separators=(",", ":")) + "\n")

    print(json.dumps({
        "manifest": str(manifest_path),
        "calls": len(rows),
        "audioCopied": sum(1 for row in rows if row.get("local_audio_path")),
    }, indent=2))


def copy_audio(args: argparse.Namespace, row: dict, audio_dir: Path) -> str:
    rel = str(row.get("audio_path") or "").lstrip("/")
    if not rel:
        return ""
    suffix = Path(rel).suffix or ".wav"
    local = audio_dir / f"{row['id']}{suffix}"
    remote = f"{args.host}:{args.audio_root.rstrip('/')}/{rel}"
    subprocess.run(scp_base(args.key) + [remote, str(local)], check=True)
    return str(local)


def make_label_template(args: argparse.Namespace) -> None:
    rows = read_jsonl(Path(args.manifest))
    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8") as handle:
        for row in rows:
            label = {
                "call_id": row["id"],
                "review_status": "unreviewed",
                "incident_worthy": None,
                "expected_location_terms": [],
                "expected_address_unit_terms": [],
                "expected_event_type_terms": [],
                "expected_closure_blockage_terms": [],
                "expected_injury_hazard_terms": [],
                "expected_continuation_terms": [],
                "notes": "",
                "source_transcription_preview": row.get("transcription_preview", ""),
                "local_audio_path": row.get("local_audio_path", ""),
            }
            handle.write(json.dumps(label, separators=(",", ":")) + "\n")
    print(json.dumps({"labels": str(out), "calls": len(rows)}, indent=2))


def run_openai_compatible(args: argparse.Namespace) -> None:
    rows = read_jsonl(Path(args.manifest))
    if args.limit > 0:
        rows = rows[: args.limit]
    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    base_url = args.base_url.rstrip("/")
    model = args.model or "whisper-1"
    with out.open("w", encoding="utf-8") as handle:
        for row in rows:
            audio_path = Path(row.get("local_audio_path") or "")
            started = time.perf_counter()
            result = {
                "engine_id": args.engine_id,
                "model": model,
                "call_id": row["id"],
                "ok": False,
                "text": "",
                "seconds": 0.0,
                "error": "",
            }
            try:
                if not audio_path.exists():
                    raise FileNotFoundError(f"audio file not found: {audio_path}")
                response = post_audio(base_url, model, audio_path, args.api_key)
                result["text"] = str(response.get("text") or "")
                result["response"] = response
                result["ok"] = True
            except Exception as exc:  # keep batch going
                result["error"] = str(exc)
            result["seconds"] = round(time.perf_counter() - started, 3)
            handle.write(json.dumps(result, separators=(",", ":")) + "\n")
            handle.flush()
    print(json.dumps({"results": str(out), "calls": len(rows), "engine": args.engine_id}, indent=2))


def post_audio(base_url: str, model: str, audio_path: Path, api_key: str) -> dict:
    boundary = "----pizzawave-" + uuid.uuid4().hex
    content_type = mimetypes.guess_type(audio_path.name)[0] or "audio/wav"
    body = build_multipart(boundary, model, audio_path, content_type)
    request = urllib.request.Request(
        base_url + "/audio/transcriptions",
        data=body,
        method="POST",
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    if api_key:
        request.add_header("Authorization", "Bearer " + api_key)
    with urllib.request.urlopen(request, timeout=600) as response:
        raw = response.read().decode("utf-8", errors="replace")
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError:
        parsed = {"text": raw}
    return parsed


def build_multipart(boundary: str, model: str, audio_path: Path, content_type: str) -> bytes:
    chunks: list[bytes] = []
    add_field(chunks, boundary, "model", model)
    chunks.append(f"--{boundary}\r\n".encode())
    chunks.append(
        f'Content-Disposition: form-data; name="file"; filename="{audio_path.name}"\r\n'
        f"Content-Type: {content_type}\r\n\r\n".encode()
    )
    chunks.append(audio_path.read_bytes())
    chunks.append(b"\r\n")
    chunks.append(f"--{boundary}--\r\n".encode())
    return b"".join(chunks)


def add_field(chunks: list[bytes], boundary: str, name: str, value: str) -> None:
    chunks.append(f"--{boundary}\r\n".encode())
    chunks.append(f'Content-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())


def score_results(args: argparse.Namespace) -> None:
    labels = {int(row["call_id"]): row for row in read_jsonl(Path(args.labels))}
    results = read_jsonl(Path(args.results))
    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    field_names = [
        ("location", "expected_location_terms"),
        ("address_unit", "expected_address_unit_terms"),
        ("event_type", "expected_event_type_terms"),
        ("closure_blockage", "expected_closure_blockage_terms"),
        ("injury_hazard", "expected_injury_hazard_terms"),
        ("continuation", "expected_continuation_terms"),
    ]
    with out.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=[
            "engine_id", "call_id", "review_status", "incident_worthy", "ok",
            *[name for name, _ in field_names],
            "matched_fields", "scorable_fields", "seconds", "error"
        ])
        writer.writeheader()
        for result in results:
            label = labels.get(int(result["call_id"]), {})
            text = normalize(result.get("text") or "")
            row = {
                "engine_id": result.get("engine_id", ""),
                "call_id": result.get("call_id", ""),
                "review_status": label.get("review_status", "missing_label"),
                "incident_worthy": label.get("incident_worthy"),
                "ok": result.get("ok", False),
                "seconds": result.get("seconds", 0),
                "error": result.get("error", ""),
            }
            matched = 0
            scorable = 0
            for name, key in field_names:
                terms = label.get(key) or []
                if not terms:
                    row[name] = ""
                    continue
                scorable += 1
                hit = any(normalize(term) in text for term in terms)
                row[name] = "1" if hit else "0"
                if hit:
                    matched += 1
            row["matched_fields"] = matched
            row["scorable_fields"] = scorable
            writer.writerow(row)
    print(json.dumps({"score": str(out), "rows": len(results)}, indent=2))


def normalize(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", str(value).lower()).strip()


def read_jsonl(path: Path) -> list[dict]:
    with path.open("r", encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


if __name__ == "__main__":
    raise SystemExit(main())
