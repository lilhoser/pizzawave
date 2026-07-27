#!/usr/bin/env python3
"""Summarize fixed-primary Trunk Recorder P25 file-source replay logs.

Log names must use ``<capture>-<demod>-r<repetition>.log``. The analyzer
compares the first configured pre-trigger interval with the following
post-trigger interval by ordinal decode-rate windows. Any trailing EOF status
sample is excluded from the evidence window.
"""

from __future__ import annotations

import argparse
import json
import re
import statistics
from dataclasses import dataclass
from pathlib import Path


NAME_RE = re.compile(
    r"^(?P<capture>\d+)-(?P<demod>[A-Za-z0-9_-]+)-r(?P<repetition>\d+)\.log$"
)
RATE_RE = re.compile(
    r"freq:\s*(?P<frequency_mhz>[0-9.]+)\s+MHz\s+"
    r"Control Channel Message Decode Rate:\s*(?P<rate>\d+)/sec,\s*"
    r"count:\s*(?P<count>\d+)"
)
SYSTEM_RE = re.compile(
    r"Decoding System ID\s+(?P<system>[0-9A-F]+)\s+"
    r"WACN:\s*(?P<wacn>[0-9A-F]+)\s+NAC:\s*(?P<nac>[0-9A-F]+)",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class RateWindow:
    frequency_mhz: float
    rate: int
    count: int


def summarize_windows(rows: list[RateWindow]) -> dict[str, float | int]:
    if not rows:
        return {
            "windows": 0,
            "validMessages": 0,
            "meanRate": 0.0,
            "minRate": 0,
            "maxRate": 0,
            "zeroWindows": 0,
            "lowWindowsAtMostOne": 0,
        }
    return {
        "windows": len(rows),
        "validMessages": sum(row.count for row in rows),
        "meanRate": round(statistics.fmean(row.rate for row in rows), 4),
        "minRate": min(row.rate for row in rows),
        "maxRate": max(row.rate for row in rows),
        "zeroWindows": sum(row.rate == 0 for row in rows),
        "lowWindowsAtMostOne": sum(row.rate <= 1 for row in rows),
    }


def parse_log(
    path: Path,
    pre_windows: int,
    post_windows: int,
    expected_system: str,
    expected_wacn: str,
    expected_nac: str,
) -> dict[str, object]:
    match = NAME_RE.match(path.name)
    if not match:
        raise ValueError(f"Unexpected replay log name: {path.name}")

    text = path.read_text(encoding="utf-8", errors="replace")
    rates = [
        RateWindow(
            frequency_mhz=float(rate_match.group("frequency_mhz")),
            rate=int(rate_match.group("rate")),
            count=int(rate_match.group("count")),
        )
        for rate_match in RATE_RE.finditer(text)
    ]
    evidence_windows = pre_windows + post_windows
    if len(rates) < evidence_windows:
        raise ValueError(
            f"{path.name} has {len(rates)} rate windows; expected at least "
            f"{evidence_windows}"
        )

    evidence = rates[:evidence_windows]
    pre = evidence[:pre_windows]
    post = evidence[pre_windows:]
    decoded_systems = [system_match.groupdict() for system_match in SYSTEM_RE.finditer(text)]
    expected_identity = {
        "system": expected_system.upper(),
        "wacn": expected_wacn.upper(),
        "nac": expected_nac.upper(),
    }
    correct_identity = any(
        {key: value.upper() for key, value in identity.items()} == expected_identity
        for identity in decoded_systems
    )
    frequencies = sorted({row.frequency_mhz for row in evidence})

    status_path = path.with_suffix(".status")
    exit_status = (
        int(status_path.read_text(encoding="utf-8").strip())
        if status_path.exists()
        else None
    )

    return {
        "capture": match.group("capture"),
        "demod": match.group("demod"),
        "repetition": int(match.group("repetition")),
        "exitStatus": exit_status,
        "correctIdentity": correct_identity,
        "decodedIdentities": decoded_systems,
        "frequenciesMHz": frequencies,
        "fixedPrimary": len(frequencies) == 1,
        "all": summarize_windows(evidence),
        "preTrigger": summarize_windows(pre),
        "postTrigger": summarize_windows(post),
    }


def aggregate(runs: list[dict[str, object]]) -> list[dict[str, object]]:
    grouped: dict[tuple[str, str], list[dict[str, object]]] = {}
    for run in runs:
        key = (str(run["capture"]), str(run["demod"]))
        grouped.setdefault(key, []).append(run)

    output: list[dict[str, object]] = []
    for (capture, demod), group in sorted(grouped.items()):
        def values(section: str, metric: str) -> list[float]:
            return [float(run[section][metric]) for run in group]  # type: ignore[index]

        output.append(
            {
                "capture": capture,
                "demod": demod,
                "runs": len(group),
                "allCorrectIdentity": all(bool(run["correctIdentity"]) for run in group),
                "allFixedPrimary": all(bool(run["fixedPrimary"]) for run in group),
                "exitStatuses": sorted({run["exitStatus"] for run in group}),
                "validMessagesMedian": statistics.median(values("all", "validMessages")),
                "validMessagesRange": [
                    min(values("all", "validMessages")),
                    max(values("all", "validMessages")),
                ],
                "preTriggerMessagesMedian": statistics.median(
                    values("preTrigger", "validMessages")
                ),
                "postTriggerMessagesMedian": statistics.median(
                    values("postTrigger", "validMessages")
                ),
                "postTriggerZeroWindowsMedian": statistics.median(
                    values("postTrigger", "zeroWindows")
                ),
                "postTriggerLowWindowsMedian": statistics.median(
                    values("postTrigger", "lowWindowsAtMostOne")
                ),
            }
        )
    return output


def build_comparisons(aggregates: list[dict[str, object]]) -> list[dict[str, object]]:
    by_capture: dict[str, dict[str, dict[str, object]]] = {}
    for row in aggregates:
        by_capture.setdefault(str(row["capture"]), {})[str(row["demod"])] = row

    comparisons: list[dict[str, object]] = []
    for capture, demods in sorted(by_capture.items()):
        if "qpsk" not in demods or "fsk4" not in demods:
            continue
        qpsk = demods["qpsk"]
        fsk4 = demods["fsk4"]
        comparisons.append(
            {
                "capture": capture,
                "fsk4MinusQpskValidMessages": (
                    fsk4["validMessagesMedian"] - qpsk["validMessagesMedian"]  # type: ignore[operator]
                ),
                "fsk4MinusQpskPreTriggerMessages": (
                    fsk4["preTriggerMessagesMedian"]
                    - qpsk["preTriggerMessagesMedian"]  # type: ignore[operator]
                ),
                "fsk4MinusQpskPostTriggerMessages": (
                    fsk4["postTriggerMessagesMedian"]
                    - qpsk["postTriggerMessagesMedian"]  # type: ignore[operator]
                ),
                "fsk4MinusQpskPostZeroWindows": (
                    fsk4["postTriggerZeroWindowsMedian"]
                    - qpsk["postTriggerZeroWindowsMedian"]  # type: ignore[operator]
                ),
            }
        )
    return comparisons


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("log_dir", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--window-seconds", type=int, default=3)
    parser.add_argument("--pre-trigger-seconds", type=int, default=30)
    parser.add_argument("--post-trigger-seconds", type=int, default=60)
    parser.add_argument("--system", default="2AD")
    parser.add_argument("--wacn", default="BEE00")
    parser.add_argument("--nac", default="2A4")
    args = parser.parse_args()

    if args.pre_trigger_seconds % args.window_seconds != 0:
        parser.error("pre-trigger seconds must be divisible by window seconds")
    if args.post_trigger_seconds % args.window_seconds != 0:
        parser.error("post-trigger seconds must be divisible by window seconds")

    pre_windows = args.pre_trigger_seconds // args.window_seconds
    post_windows = args.post_trigger_seconds // args.window_seconds
    logs = sorted(args.log_dir.glob("*.log"))
    if not logs:
        parser.error(f"no replay logs found in {args.log_dir}")

    runs = [
        parse_log(
            path,
            pre_windows,
            post_windows,
            args.system,
            args.wacn,
            args.nac,
        )
        for path in logs
    ]
    aggregates = aggregate(runs)
    result = {
        "schemaVersion": 1,
        "windowSeconds": args.window_seconds,
        "preTriggerSeconds": args.pre_trigger_seconds,
        "postTriggerSeconds": args.post_trigger_seconds,
        "runs": runs,
        "aggregates": aggregates,
        "comparisons": build_comparisons(aggregates),
    }
    rendered = json.dumps(result, indent=2) + "\n"
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    else:
        print(rendered, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
