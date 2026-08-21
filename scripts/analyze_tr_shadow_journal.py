#!/usr/bin/env python3
"""Summarize Trunk Recorder TR_SHADOW journal telemetry without database state."""

from __future__ import annotations

import argparse
import json
import statistics
import subprocess
from collections import defaultdict
from datetime import datetime
from typing import Any
from zoneinfo import ZoneInfo


def percentile(values: list[int], fraction: float) -> float:
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def longest_run(values: list[int], maximum: int) -> int:
    best = current = 0
    for value in values:
        current = current + 1 if value <= maximum else 0
        best = max(best, current)
    return best


def summarize(values: list[int]) -> dict[str, Any]:
    return {
        "samples": len(values),
        "mean": round(statistics.fmean(values), 4),
        "median": round(statistics.median(values), 4),
        "p10": round(percentile(values, 0.1), 4),
        "p90": round(percentile(values, 0.9), 4),
        "minimum": min(values),
        "maximum": max(values),
        "zeroPercent": round(100 * sum(value == 0 for value in values) / len(values), 4),
        "atMost3Percent": round(100 * sum(value <= 3 for value in values) / len(values), 4),
        "atLeast25Percent": round(100 * sum(value >= 25 for value in values) / len(values), 4),
        "longestAtMost3Run": longest_run(values, 3),
    }


def episodes(values: list[int]) -> list[int]:
    active_start: int | None = None
    low_run = 0
    recovery_run = 0
    completed: list[int] = []
    for index, value in enumerate(values):
        if active_start is None:
            low_run = low_run + 1 if value <= 3 else 0
            if low_run == 3:
                active_start = index - 2
                recovery_run = 0
        else:
            recovery_run = recovery_run + 1 if value >= 10 else 0
            if recovery_run == 3:
                completed.append(index - active_start + 1)
                active_start = None
                low_run = 0
                recovery_run = 0
    return completed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--since", required=True)
    parser.add_argument("--timezone", default="America/New_York")
    args = parser.parse_args()

    command = [
        "journalctl", "-u", "trunk-recorder", "--since", args.since,
        "--grep", "TR_SHADOW", "-o", "cat", "--no-pager",
    ]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, text=True, errors="replace")
    if process.stdout is None:
        raise RuntimeError("journalctl stdout unavailable")

    rows: dict[str, list[dict[str, Any]]] = defaultdict(list)
    malformed = 0
    for line in process.stdout:
        marker = "TR_SHADOW "
        if marker not in line:
            continue
        try:
            payload = json.loads(line.split(marker, 1)[1])
            rows[str(payload["systemShortName"])].append(payload)
        except (json.JSONDecodeError, KeyError, TypeError, ValueError):
            malformed += 1
    status = process.wait()
    if status != 0:
        raise RuntimeError(f"journalctl exited {status}")

    timezone = ZoneInfo(args.timezone)
    systems = []
    for system, samples in sorted(rows.items()):
        samples.sort(key=lambda row: int(row["timestampUnixMs"]))
        live = [int(row["liveDecodeRate"]) for row in samples]
        shadow = [int(row["shadowDecodeRate"]) for row in samples]
        deltas = [left - right for left, right in zip(live, shadow)]
        by_hour: dict[int, list[tuple[int, int]]] = defaultdict(list)
        for row, live_rate, shadow_rate in zip(samples, live, shadow):
            timestamp = datetime.fromtimestamp(int(row["timestampUnixMs"]) / 1000, timezone)
            by_hour[timestamp.hour].append((live_rate, shadow_rate))
        hourly = [
            {
                "localHour": hour,
                "samples": len(values),
                "liveMean": round(statistics.fmean(value[0] for value in values), 4),
                "shadowMean": round(statistics.fmean(value[1] for value in values), 4),
                "liveAtMost3Percent": round(100 * sum(value[0] <= 3 for value in values) / len(values), 4),
                "shadowAtMost3Percent": round(100 * sum(value[1] <= 3 for value in values) / len(values), 4),
            }
            for hour, values in sorted(by_hour.items())
        ]
        live_episodes = episodes(live)
        shadow_episodes = episodes(shadow)
        systems.append(
            {
                "systemShortName": system,
                "startUnixMs": int(samples[0]["timestampUnixMs"]),
                "endUnixMs": int(samples[-1]["timestampUnixMs"]),
                "live": summarize(live),
                "shadow": summarize(shadow),
                "paired": {
                    "meanLiveMinusShadow": round(statistics.fmean(deltas), 4),
                    "liveWinsPercent": round(100 * sum(delta > 0 for delta in deltas) / len(deltas), 4),
                    "tiesPercent": round(100 * sum(delta == 0 for delta in deltas) / len(deltas), 4),
                    "shadowWinsPercent": round(100 * sum(delta < 0 for delta in deltas) / len(deltas), 4),
                },
                "liveCompletedLowEpisodes": {
                    "count": len(live_episodes),
                    "medianDurationSeconds": round(statistics.median(live_episodes), 4) if live_episodes else None,
                    "maximumDurationSeconds": max(live_episodes) if live_episodes else None,
                },
                "shadowCompletedLowEpisodes": {
                    "count": len(shadow_episodes),
                    "medianDurationSeconds": round(statistics.median(shadow_episodes), 4) if shadow_episodes else None,
                    "maximumDurationSeconds": max(shadow_episodes) if shadow_episodes else None,
                },
                "hourly": hourly,
            }
        )

    print(json.dumps({"schemaVersion": 1, "since": args.since, "malformed": malformed, "systems": systems}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
