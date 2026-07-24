#!/usr/bin/env python3
"""Reconcile transcript-free incident request starts with recorded LM completions."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import uuid


START_MARKERS = (
    "Calling incident extraction endpoint ",
    "Calling evidence verifier endpoint ",
    "Calling LM Studio insights endpoint ",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--journal", required=True)
    parser.add_argument("--usage", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--system-id", required=True)
    parser.add_argument("--cohort-id", required=True)
    parser.add_argument("--start", required=True, type=int)
    parser.add_argument("--end", required=True, type=int)
    return parser.parse_args()


def iso_utc(value: dt.datetime) -> str:
    return value.astimezone(dt.timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def parse_usage_timestamp(value: str) -> dt.datetime:
    normalized = value.strip().replace("Z", "+00:00")
    parsed = dt.datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed.astimezone(dt.timezone.utc)


def canonical_hash(value: object) -> str:
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def main() -> None:
    args = parse_args()
    if args.start < 0 or args.end <= args.start:
        raise ValueError("the half-open timing window is invalid")
    if not args.system_id.strip() or not args.cohort_id.strip():
        raise ValueError("system-id and cohort-id are required")

    start = dt.datetime.fromtimestamp(args.start, tz=dt.timezone.utc)
    end = dt.datetime.fromtimestamp(args.end, tz=dt.timezone.utc)
    starts: list[dict[str, object]] = []
    journal_path = pathlib.Path(args.journal).resolve()
    for line_number, line in enumerate(
        journal_path.read_text(encoding="utf-8").splitlines(), start=1
    ):
        if not line.strip():
            continue
        row = json.loads(line)
        message = str(row.get("MESSAGE") or "").strip()
        if not message.startswith(START_MARKERS):
            continue
        raw_timestamp = row.get("__REALTIME_TIMESTAMP")
        if raw_timestamp is None:
            raise ValueError(f"journal line {line_number} has no realtime timestamp")
        started = dt.datetime.fromtimestamp(
            int(raw_timestamp) / 1_000_000, tz=dt.timezone.utc
        )
        if start <= started < end:
            starts.append(
                {
                    "started": started,
                    "kind": next(
                        marker.removeprefix("Calling ").removesuffix(" endpoint ")
                        for marker in START_MARKERS
                        if message.startswith(marker)
                    ),
                }
            )

    usage_document = json.loads(pathlib.Path(args.usage).read_text(encoding="utf-8"))
    usage_rows = usage_document if isinstance(usage_document, list) else []
    completions: list[dict[str, object]] = []
    for row in usage_rows:
        completed = parse_usage_timestamp(str(row["timestamp_utc"]))
        if (
            start <= completed < end
            and str(row.get("trigger_activity") or "") == "automatic insights"
        ):
            completions.append(
                {
                    "id": int(row["id"]),
                    "completed": completed,
                    "success": bool(row["success"]),
                }
            )

    starts.sort(key=lambda item: item["started"])
    completions.sort(key=lambda item: item["completed"])
    if len(starts) != len(completions):
        raise ValueError(
            f"request boundary mismatch: {len(starts)} starts and "
            f"{len(completions)} recorded completions"
        )

    timings: list[dict[str, object]] = []
    for index, (request, completion) in enumerate(zip(starts, completions, strict=True)):
        started = request["started"]
        completed = completion["completed"]
        assert isinstance(started, dt.datetime)
        assert isinstance(completed, dt.datetime)
        if completed < started:
            raise ValueError(
                f"completion {completion['id']} precedes request start at index {index}"
            )
        if index + 1 < len(starts):
            next_started = starts[index + 1]["started"]
            assert isinstance(next_started, dt.datetime)
            if completed > next_started:
                raise ValueError(
                    f"request {completion['id']} overlaps the next serialized start"
                )
        timings.append(
            {
                "usageId": completion["id"],
                "kind": request["kind"],
                "startedAtUtc": iso_utc(started),
                "completedAtUtc": iso_utc(completed),
                "durationMilliseconds": round(
                    (completed - started).total_seconds() * 1000
                ),
                "success": completion["success"],
            }
        )

    report_without_hash = {
        "protocolIdentity": "incident-request-timing-reconciliation-v1",
        "systemId": args.system_id.strip(),
        "cohortId": args.cohort_id.strip(),
        "windowStartUtc": iso_utc(start),
        "windowEndUtc": iso_utc(end),
        "requestCount": len(timings),
        "requestDurationMilliseconds": sum(
            int(item["durationMilliseconds"]) for item in timings
        ),
        "timings": timings,
    }
    report = report_without_hash | {"contentHash": canonical_hash(report_without_hash)}
    output = pathlib.Path(args.output).resolve()
    if output.exists():
        raise FileExistsError(f"refusing to overwrite {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f"{output.name}.{uuid.uuid4().hex}.tmp")
    try:
        temporary.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        temporary.replace(output)
    except Exception:
        temporary.unlink(missing_ok=True)
        raise
    print(
        f"system={report['systemId']} cohort={report['cohortId']} "
        f"requests={report['requestCount']} "
        f"duration_ms={report['requestDurationMilliseconds']} output={output}"
    )


if __name__ == "__main__":
    main()
