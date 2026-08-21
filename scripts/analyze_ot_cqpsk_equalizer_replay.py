#!/usr/bin/env python3
"""Analyze offline same-IQ Trunk Recorder CQPSK equalizer replays.

OP25's existing decode of each unchanged input supplies independent per-second
healthy/degraded labels. Candidate rates come from Trunk Recorder's fixed-primary
shadow stream. This avoids choosing evaluation windows from the candidate being
scored.
"""

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
TR_SHADOW_RE = re.compile(r"TR_SHADOW (?P<payload>\{.*\})")
TR_ID_RE = re.compile(
    r"Decoding System ID (?P<system>[0-9A-F]+) WACN: (?P<wacn>[0-9A-F]+) "
    r"NAC: (?P<nac>[0-9A-F]+)",
    re.IGNORECASE,
)
NAME_RE = re.compile(
    r"^(?P<candidate>.+)-(?P<trigger>\d+)-r(?P<repetition>\d+)\.log$"
)


EXPECTED = {
    "whiteoakmt-hamilton": {"nac": "2A0", "system": "2A5", "wacn": "BEE00"},
    "whiteoakmt-nbradley": {"nac": "2AD", "system": "2A5", "wacn": "BEE00"},
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_stamp(value: str) -> datetime:
    return datetime.strptime(value, "%m/%d/%y %H:%M:%S.%f")


def parse_op25_labels(path: Path, duration: int) -> list[int]:
    text = path.read_text(encoding="utf-8", errors="replace")
    start = START_RE.search(text)
    if not start:
        raise ValueError(f"{path}: no playback start")
    origin = parse_stamp(start.group("stamp"))
    bins = [0] * duration
    for match in TSBK_RE.finditer(text):
        elapsed = (parse_stamp(match.group("stamp")) - origin).total_seconds()
        if 0 <= elapsed < duration:
            bins[int(elapsed)] += 1
    return bins


def parse_exit_status(path: Path) -> int:
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("exit_status="):
            return int(line.split("=", 1)[1])
    raise ValueError(f"{path}: missing exit status")


def mean_or_none(values: list[int]) -> float | None:
    return round(statistics.fmean(values), 4) if values else None


def longest_run(values: list[int], maximum: int) -> int:
    best = current = 0
    for value in values:
        current = current + 1 if value <= maximum else 0
        best = max(best, current)
    return best


def parse_run(
    log_path: Path,
    capture_root: Path,
    reference_root: Path,
    healthy_min: int,
    degraded_max: int,
) -> dict[str, Any]:
    name = NAME_RE.match(log_path.name)
    if not name:
        raise ValueError(f"unexpected log name: {log_path.name}")
    trigger = int(name.group("trigger"))
    metadata_path = next(capture_root.glob(f"{trigger}-*-automatic.json"))
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    iq_path = metadata_path.with_suffix(".fc32")
    duration = math.floor(iq_path.stat().st_size / (8 * float(metadata["sampleRate"])))
    reference = parse_op25_labels(reference_root / f"{trigger}.log", duration)

    text = log_path.read_text(encoding="utf-8", errors="replace")
    shadow_rows = [
        json.loads(match.group("payload")) for match in TR_SHADOW_RE.finditer(text)
    ]
    if len(shadow_rows) < duration:
        raise ValueError(
            f"{log_path.name}: {len(shadow_rows)} shadow rows; expected {duration}"
        )
    rates = [int(row["shadowDecodeRate"]) for row in shadow_rows[:duration]]
    healthy = [rate for rate, label in zip(rates, reference) if label >= healthy_min]
    degraded = [rate for rate, label in zip(rates, reference) if label <= degraded_max]
    if not healthy or not degraded:
        raise ValueError(f"{trigger}: independent labels lack healthy or degraded seconds")

    system = str(metadata["systemShortName"])
    expected = EXPECTED[system]
    identities = [
        {key: value.upper() for key, value in match.groupdict().items()}
        for match in TR_ID_RE.finditer(text)
    ]
    return {
        "candidate": name.group("candidate"),
        "trigger": trigger,
        "repetition": int(name.group("repetition")),
        "systemShortName": system,
        "durationSeconds": duration,
        "independentHealthySeconds": len(healthy),
        "independentDegradedSeconds": len(degraded),
        "allMeanRate": mean_or_none(rates),
        "healthyMeanRate": mean_or_none(healthy),
        "degradedMeanRate": mean_or_none(degraded),
        "validMessages": sum(rates),
        "healthyMessages": sum(healthy),
        "degradedMessages": sum(degraded),
        "zeroSeconds": sum(value == 0 for value in rates),
        "lowSecondsAtMost3": sum(value <= 3 for value in rates),
        "longestLowRunAtMost3": longest_run(rates, 3),
        "identitySeen": expected in identities,
        "foreignIdentitySeen": any(identity != expected for identity in identities),
        "exitStatus": parse_exit_status(log_path.with_suffix(".meta")),
        "inputSha256": sha256(iq_path),
        "logSha256": sha256(log_path),
    }


def median(values: list[float | int]) -> float:
    return round(float(statistics.median(values)), 4)


def aggregate_runs(runs: list[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: dict[tuple[str, int], list[dict[str, Any]]] = {}
    for run in runs:
        groups.setdefault((run["candidate"], run["trigger"]), []).append(run)
    output = []
    metrics = (
        "allMeanRate",
        "healthyMeanRate",
        "degradedMeanRate",
        "validMessages",
        "healthyMessages",
        "degradedMessages",
        "zeroSeconds",
        "lowSecondsAtMost3",
        "longestLowRunAtMost3",
    )
    for (candidate, trigger), group in sorted(groups.items()):
        row: dict[str, Any] = {
            "candidate": candidate,
            "trigger": trigger,
            "systemShortName": group[0]["systemShortName"],
            "runs": len(group),
            "allIdentitySeen": all(run["identitySeen"] for run in group),
            "anyForeignIdentity": any(run["foreignIdentitySeen"] for run in group),
            "exitStatuses": sorted({run["exitStatus"] for run in group}),
        }
        for metric in metrics:
            row[metric] = median([run[metric] for run in group])
        output.append(row)
    return output


def compare_candidates(captures: list[dict[str, Any]]) -> list[dict[str, Any]]:
    by_candidate: dict[str, list[dict[str, Any]]] = {}
    for row in captures:
        by_candidate.setdefault(row["candidate"], []).append(row)
    baseline = {row["trigger"]: row for row in by_candidate["baseline"]}
    comparisons = []
    for candidate, rows in sorted(by_candidate.items()):
        if candidate == "baseline":
            continue
        deltas = []
        for row in rows:
            base = baseline[row["trigger"]]
            deltas.append(
                {
                    "trigger": row["trigger"],
                    "systemShortName": row["systemShortName"],
                    "healthyMeanDelta": round(
                        row["healthyMeanRate"] - base["healthyMeanRate"], 4
                    ),
                    "degradedMeanDelta": round(
                        row["degradedMeanRate"] - base["degradedMeanRate"], 4
                    ),
                    "validMessageDelta": row["validMessages"] - base["validMessages"],
                }
            )
        comparisons.append(
            {
                "candidate": candidate,
                "captures": len(rows),
                "aggregateHealthyMeanDelta": round(
                    statistics.fmean(delta["healthyMeanDelta"] for delta in deltas), 4
                ),
                "aggregateDegradedMeanDelta": round(
                    statistics.fmean(delta["degradedMeanDelta"] for delta in deltas), 4
                ),
                "aggregateValidMessageDelta": sum(
                    delta["validMessageDelta"] for delta in deltas
                ),
                "healthyRegressedCaptureCount": sum(
                    delta["healthyMeanDelta"] < -1.0 for delta in deltas
                ),
                "degradedImprovedCaptureCount": sum(
                    delta["degradedMeanDelta"] > 0 for delta in deltas
                ),
                "degradedRegressedCaptureCount": sum(
                    delta["degradedMeanDelta"] < 0 for delta in deltas
                ),
                "allIdentitySeen": all(row["allIdentitySeen"] for row in rows),
                "anyForeignIdentity": any(row["anyForeignIdentity"] for row in rows),
                "perCapture": deltas,
            }
        )
    return comparisons


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("phase_root", type=Path)
    parser.add_argument(
        "--capture-root",
        type=Path,
        default=Path(r"C:\temp\pizzawave-rf\ot-day-night-replay"),
    )
    parser.add_argument(
        "--reference-root",
        type=Path,
        default=Path(r"C:\temp\pizzawave-rf\ot-day-night-replay\op25-results"),
    )
    parser.add_argument("--healthy-min", type=int, default=15)
    parser.add_argument("--degraded-max", type=int, default=3)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    logs = sorted((args.phase_root / "logs").glob("*.log"))
    if not logs:
        parser.error("phase has no logs")
    runs = [
        parse_run(
            path,
            args.capture_root,
            args.reference_root,
            args.healthy_min,
            args.degraded_max,
        )
        for path in logs
    ]
    captures = aggregate_runs(runs)
    comparisons = compare_candidates(captures)
    result = {
        "schemaVersion": 1,
        "independentLabels": {
            "source": "existing OP25 same-IQ replay",
            "healthyMinimumMessagesPerSecond": args.healthy_min,
            "degradedMaximumMessagesPerSecond": args.degraded_max,
        },
        "runs": runs,
        "captures": captures,
        "comparisons": comparisons,
    }
    rendered = json.dumps(result, indent=2) + "\n"
    output = args.output or args.phase_root / "analysis.json"
    output.write_text(rendered, encoding="utf-8")
    print(json.dumps(comparisons, indent=2))
    print("analysisSha256", sha256(output))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
