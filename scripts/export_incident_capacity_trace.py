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
    parser.add_argument(
        "--includes-verification",
        action="store_true",
        help="Assert that the trace window includes all verification requests for this pipeline.",
    )
    parser.add_argument(
        "--request-timings",
        help="Optional reconciled request-timing JSON for a host without native duration telemetry.",
    )
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
    if not args.includes_verification:
        raise ValueError("--includes-verification is required for production capacity traces")

    output = pathlib.Path(args.output).resolve()
    if output.exists():
        raise FileExistsError(f"refusing to overwrite {output}")
    source_uri = f"file:{pathlib.Path(args.source).resolve().as_posix()}?mode=ro"
    start_utc = iso_utc(args.start)
    end_utc = iso_utc(args.end)

    with sqlite3.connect(source_uri, uri=True) as source:
        source.row_factory = sqlite3.Row
        usage_columns = {
            str(row[1]) for row in source.execute("pragma table_info(lm_usage)")
        }
        duration_expression = (
            "duration_milliseconds"
            if "duration_milliseconds" in usage_columns
            else "0"
        )
        if args.pipeline == "legacy":
            usable = source.execute(
                "select count(*) from calls where start_time >= ? and start_time < ? "
                "and length(trim(coalesce(transcription, ''))) >= 12",
                (args.start, args.end),
            ).fetchone()[0]
            trigger = args.legacy_trigger
            processed = 0
            candidate_batches = 0
            triggers = [trigger]
        else:
            usable = source.execute(
                "select count(*) from calls where id > ? and start_time < ? "
                "and length(trim(coalesce(transcription, ''))) >= 12",
                (args.start_after_call_id, args.end),
            ).fetchone()[0]
            run_id = args.run_id.strip()
            trigger = f"incident batch constructor shadow:{run_id}"
            triggers = [
                trigger,
                f"incident batch relationship shadow:{run_id}",
                f"incident batch confirmation shadow:{run_id}",
                f"incident batch standalone verification:{run_id}",
            ]
            ledger_rows = source.execute(
                "select payload_json from incident_batch_constructor_shadow_ledger "
                "where run_id = ? and recorded_at_utc >= ? and recorded_at_utc < ? "
                "order by sequence",
                (args.run_id.strip(), start_utc, end_utc),
            ).fetchall()
            payloads = [json.loads(row[0]) for row in ledger_rows]
            processed = sum(len(payload.get("newObservationIds", [])) for payload in payloads)
            candidate_batches = sum(bool(payload.get("candidates")) for payload in payloads)

        placeholders = ",".join("?" for _ in triggers)
        usage = source.execute(
            "select count(*) requests, "
            "sum(case when success = 0 then 1 else 0 end) failed, "
            "coalesce(sum(prompt_tokens), 0) prompt, "
            "coalesce(sum(completion_tokens), 0) completion, "
            f"coalesce(sum({duration_expression}), 0) duration "
            f"from lm_usage where trigger_activity in ({placeholders}) "
            "and timestamp_utc >= ? and timestamp_utc < ?",
            (*triggers, start_utc, end_utc),
        ).fetchone()

    request_duration = usage["duration"]
    if args.request_timings:
        timings = json.loads(
            pathlib.Path(args.request_timings).resolve().read_text(encoding="utf-8")
        )
        if timings.get("systemId") != args.system_id.strip():
            raise ValueError("request timings system does not match the trace")
        if timings.get("cohortId") != args.cohort_id.strip():
            raise ValueError("request timings cohort does not match the trace")
        if timings.get("windowStartUtc") != start_utc or timings.get("windowEndUtc") != end_utc:
            raise ValueError("request timings window does not match the trace")
        if int(timings.get("requestCount") or 0) != int(usage["requests"]):
            raise ValueError("request timings count does not match recorded LM usage")
        request_duration = int(timings.get("requestDurationMilliseconds") or 0)
    if usage["requests"] and request_duration <= 0:
        raise ValueError("measured requests require positive duration telemetry")

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
        "includesVerification": True,
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
