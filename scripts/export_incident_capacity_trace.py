#!/usr/bin/env python3
"""Export a bounded, transcript-free incident inference capacity trace."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import sqlite3
import uuid


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--cohort-id", required=True)
    parser.add_argument("--system-id", required=True)
    parser.add_argument(
        "--pipeline", required=True, choices=("legacy", "provisional-intake")
    )
    parser.add_argument("--start", required=True, type=int)
    parser.add_argument("--end", required=True, type=int)
    parser.add_argument("--run-id")
    parser.add_argument("--start-after-call-id", type=int, default=0)
    parser.add_argument("--trace-id")
    parser.add_argument("--legacy-trigger", default="automatic insights")
    return parser.parse_args()


def iso_utc(unix_seconds: int) -> str:
    return (
        dt.datetime.fromtimestamp(unix_seconds, tz=dt.timezone.utc)
        .isoformat(timespec="seconds")
        .replace("+00:00", "Z")
    )


def main() -> None:
    args = parse_args()
    if args.start < 0 or args.end <= args.start:
        raise ValueError("the half-open capacity window is invalid")
    if not args.cohort_id.strip() or not args.system_id.strip():
        raise ValueError("cohort-id and system-id are required")
    if args.pipeline == "provisional-intake" and not (args.run_id or "").strip():
        raise ValueError("run-id is required for provisional-intake traces")
    if args.pipeline == "provisional-intake" and args.start_after_call_id <= 0:
        raise ValueError("start-after-call-id is required for provisional-intake traces")
    if args.pipeline == "legacy" and args.start_after_call_id != 0:
        raise ValueError("legacy traces cannot use start-after-call-id")

    output = pathlib.Path(args.output).resolve()
    if output.exists():
        raise FileExistsError(f"refusing to overwrite {output}")
    source_uri = f"file:{pathlib.Path(args.source).resolve().as_posix()}?mode=ro"
    start_utc = iso_utc(args.start)
    end_utc = iso_utc(args.end)

    with sqlite3.connect(source_uri, uri=True) as source:
        source.row_factory = sqlite3.Row
        if args.pipeline == "legacy":
            usable = source.execute(
                "select count(*) from calls where start_time >= ? and start_time < ? "
                "and length(trim(coalesce(transcription, ''))) >= 12",
                (args.start, args.end),
            ).fetchone()[0]
            trigger = args.legacy_trigger
            processed = 0
            request_duration = 0
            candidate_batches = 0
        else:
            usable = source.execute(
                "select count(*) from calls where id > ? and start_time < ? "
                "and length(trim(coalesce(transcription, ''))) >= 12",
                (args.start_after_call_id, args.end),
            ).fetchone()[0]
            trigger = f"incident batch constructor shadow:{args.run_id.strip()}"
            ledger_rows = source.execute(
                "select payload_json from incident_batch_constructor_shadow_ledger "
                "where run_id = ? and recorded_at_utc >= ? and recorded_at_utc < ? "
                "order by sequence",
                (args.run_id.strip(), start_utc, end_utc),
            ).fetchall()
            payloads = [json.loads(row[0]) for row in ledger_rows]
            processed = sum(len(payload.get("newObservationIds", [])) for payload in payloads)
            request_duration = sum(
                int(payload.get("execution", {}).get("proposerDurationMilliseconds") or 0)
                for payload in payloads
            )
            candidate_batches = sum(bool(payload.get("candidates")) for payload in payloads)

        usage = source.execute(
            "select count(*) requests, "
            "sum(case when success = 0 then 1 else 0 end) failed, "
            "coalesce(sum(prompt_tokens), 0) prompt, "
            "coalesce(sum(completion_tokens), 0) completion "
            "from lm_usage where trigger_activity = ? "
            "and timestamp_utc >= ? and timestamp_utc < ?",
            (trigger, start_utc, end_utc),
        ).fetchone()

    pipeline_name = "Legacy" if args.pipeline == "legacy" else "ProvisionalIntake"
    trace_id = (args.trace_id or "").strip() or (
        f"{args.system_id.strip()}-{args.pipeline}-{args.start}-{args.end}"
    )
    trace = {
        "traceId": trace_id,
        "cohortId": args.cohort_id.strip(),
        "systemId": args.system_id.strip(),
        "pipeline": pipeline_name,
        "windowStartUtc": start_utc,
        "windowEndUtc": end_utc,
        "startAfterCallId": args.start_after_call_id,
        "usableObservations": usable,
        "processedObservations": processed,
        "requests": usage["requests"],
        "failedRequests": usage["failed"] or 0,
        "promptTokens": usage["prompt"],
        "completionTokens": usage["completion"],
        "requestDurationMilliseconds": request_duration,
        "candidateBackedBatches": candidate_batches,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f"{output.name}.{uuid.uuid4().hex}.tmp")
    try:
        temporary.write_text(json.dumps(trace, indent=2) + "\n", encoding="utf-8")
        temporary.replace(output)
    except Exception:
        temporary.unlink(missing_ok=True)
        raise
    print(
        f"trace={trace_id} pipeline={pipeline_name} usable={usable} "
        f"processed={processed} requests={usage['requests']} output={output}"
    )


if __name__ == "__main__":
    main()
