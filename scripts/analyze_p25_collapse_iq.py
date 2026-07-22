#!/usr/bin/env python3
"""Analyze a paired wide P25 collapse capture without changing live services.

The recorder's wide capture is centered on the primary control channel and is
stored as interleaved complex64 (fc32).  This script aligns one-second spectral
measurements with the decoder timeline embedded in the JSON sidecar.  It is an
evidence extractor, not a P25 demodulator: it can distinguish broadband,
channel-local, neighboring-signal, and sample-integrity signatures, but cannot
by itself prove multipath instead of an exactly co-channel interferer.
"""

from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
from typing import Any, Callable

import numpy as np


FC32_BYTES_PER_SAMPLE = 8


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Analyze a Trunk Recorder paired-wide P25 collapse capture."
    )
    parser.add_argument("iq", type=Path, help="Wide fc32 IQ file")
    parser.add_argument("metadata", type=Path, help="Matching wide JSON sidecar")
    parser.add_argument(
        "--narrow-metadata",
        type=Path,
        help="Optional narrow JSON sidecar used to validate the capture group",
    )
    parser.add_argument("--output", type=Path, help="Optional JSON output file")
    parser.add_argument("--fft-size", type=int, default=8192)
    parser.add_argument("--low-max-rate", type=float, default=3.0)
    parser.add_argument("--baseline-min-rate", type=float, default=10.0)
    parser.add_argument("--signal-half-width-hz", type=float, default=6250.0)
    parser.add_argument("--near-min-hz", type=float, default=12500.0)
    parser.add_argument("--near-max-hz", type=float, default=100000.0)
    parser.add_argument("--outer-min-hz", type=float, default=150000.0)
    parser.add_argument("--outer-max-hz", type=float, default=300000.0)
    parser.add_argument("--localized-band-width-hz", type=float, default=12500.0)
    parser.add_argument("--localized-delta-threshold-db", type=float, default=1.0)
    parser.add_argument("--clip-amplitude", type=float, default=0.999)
    return parser.parse_args()


def load_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        value = json.load(handle)
    if not isinstance(value, dict):
        raise ValueError(f"{path} does not contain a JSON object")
    return value


def require_number(metadata: dict[str, Any], key: str) -> float:
    value = metadata.get(key)
    if not isinstance(value, (int, float)):
        raise ValueError(f"metadata field {key!r} is missing or is not numeric")
    return float(value)


def db10(value: float) -> float:
    return 10.0 * math.log10(max(value, 1e-30))


def band_mean(psd: np.ndarray, mask: np.ndarray, name: str) -> float:
    if not np.any(mask):
        raise ValueError(f"FFT/sample rate provides no bins for {name}")
    return float(np.mean(psd[mask]))


def nearest_timeline_sample(
    timeline: list[dict[str, Any]], target_unix_ms: float
) -> dict[str, Any]:
    return min(
        timeline,
        key=lambda sample: abs(float(sample["timestampUnixMs"]) - target_unix_ms),
    )


def mean_metrics(
    rows: list[dict[str, Any]], predicate: Callable[[dict[str, Any]], bool]
) -> dict[str, float] | None:
    selected = [row for row in rows if predicate(row)]
    if not selected:
        return None
    keys = ("rawPowerDb", "signalToOuterDb", "nearToOuterDb", "outerPowerDb")
    return {key: float(np.mean([row[key] for row in selected])) for key in keys}


def metric_delta(
    low: dict[str, float] | None, baseline: dict[str, float] | None
) -> dict[str, float] | None:
    if low is None or baseline is None:
        return None
    return {key: low[key] - baseline[key] for key in low}


def classify(delta: dict[str, float] | None, integrity: dict[str, Any]) -> dict[str, Any]:
    if integrity["nonFiniteSamples"] > 0:
        return {
            "failureClass": "sample_integrity_failure",
            "supports": ["invalid samples exist in the retained source stream"],
            "caveats": [],
        }
    if delta is None:
        return {
            "failureClass": "insufficient_rate_contrast",
            "supports": [],
            "caveats": ["capture lacks both low-rate and baseline-rate pre-trigger intervals"],
        }

    raw = delta["rawPowerDb"]
    outer = delta["outerPowerDb"]
    signal = delta["signalToOuterDb"]
    if raw <= -2.0 and outer <= -1.0:
        failure_class = "broadband_rf_or_front_end_drop"
        supports = ["low decode coincides with a broadband power and noise-floor drop"]
    elif abs(raw) < 1.0 and abs(outer) < 1.0:
        failure_class = "channel_local_modulation_impairment"
        supports = [
            "low decode occurs while broadband power and the outer-band floor remain stable"
        ]
        if signal <= -1.0:
            supports.append("control-channel energy selectively decreases")
        elif signal >= 1.0:
            supports.append("control-channel energy increases despite worse decode")
        else:
            supports.append("control-channel energy remains nearly unchanged")
    else:
        failure_class = "mixed_or_inconclusive_rf_change"
        supports = ["power changes do not match one bounded signature"]

    return {
        "failureClass": failure_class,
        "supports": supports,
        "caveats": [
            "spectral evidence cannot distinguish simulcast multipath from an exactly co-channel interferer"
        ],
    }


def analyze(args: argparse.Namespace) -> dict[str, Any]:
    metadata = load_json(args.metadata)
    if metadata.get("captureVariant") != "wide":
        raise ValueError("metadata is not a wide capture sidecar")

    sample_rate = require_number(metadata, "sampleRate")
    pre_samples = int(require_number(metadata, "preTriggerSamples"))
    post_samples = int(require_number(metadata, "postTriggerSamplesRequested"))
    trigger_unix_ms = require_number(metadata, "triggerUnixMs")
    completed_unix_ms = require_number(metadata, "completedUnixMs")
    timeline = metadata.get("decoderTimeline")
    if not isinstance(timeline, list) or not timeline:
        raise ValueError("metadata has no decoderTimeline")

    if args.fft_size <= 0 or args.fft_size % 2:
        raise ValueError("--fft-size must be a positive even number")
    if args.outer_max_hz >= sample_rate / 2.0:
        raise ValueError("outer band extends beyond the capture Nyquist frequency")

    file_size = args.iq.stat().st_size
    if file_size % FC32_BYTES_PER_SAMPLE:
        raise ValueError("IQ file size is not a whole number of complex64 samples")
    actual_samples = file_size // FC32_BYTES_PER_SAMPLE
    expected_samples = pre_samples + post_samples
    if actual_samples != expected_samples:
        raise ValueError(
            f"IQ sample count {actual_samples} does not match metadata {expected_samples}"
        )

    pair_validation: dict[str, Any] | None = None
    if args.narrow_metadata:
        narrow = load_json(args.narrow_metadata)
        pair_validation = {
            "captureGroupMatches": narrow.get("captureGroupUnixMs")
            == metadata.get("captureGroupUnixMs"),
            "systemMatches": narrow.get("systemShortName")
            == metadata.get("systemShortName"),
            "narrowCompleted": isinstance(narrow.get("completedUnixMs"), (int, float)),
        }
        if not all(pair_validation.values()):
            raise ValueError("narrow and wide sidecars do not form a complete capture pair")

    iq = np.memmap(args.iq, dtype=np.complex64, mode="r")
    window = np.hanning(args.fft_size).astype(np.float32)
    frequencies = np.fft.fftshift(
        np.fft.fftfreq(args.fft_size, d=1.0 / sample_rate)
    )
    abs_frequency = np.abs(frequencies)
    signal_mask = abs_frequency <= args.signal_half_width_hz
    near_mask = (abs_frequency >= args.near_min_hz) & (
        abs_frequency <= args.near_max_hz
    )
    outer_mask = (abs_frequency >= args.outer_min_hz) & (
        abs_frequency <= args.outer_max_hz
    )

    first_second = math.ceil(-pre_samples / sample_rate)
    last_second_exclusive = math.floor(post_samples / sample_rate)
    rows: list[dict[str, Any]] = []
    psds: dict[int, np.ndarray] = {}
    non_finite = 0
    zero_samples = 0
    repeated_adjacent = 0
    clipped_samples = 0
    previous_last: np.complex64 | None = None

    for second in range(first_second, last_second_exclusive):
        start = max(0, round(pre_samples + second * sample_rate))
        end = min(actual_samples, round(pre_samples + (second + 1) * sample_rate))
        samples = np.asarray(iq[start:end])
        usable = (len(samples) // args.fft_size) * args.fft_size
        if usable == 0:
            continue

        finite_mask = np.isfinite(samples)
        non_finite += int(samples.size - np.count_nonzero(finite_mask))
        zero_samples += int(np.count_nonzero(samples == 0))
        repeated_adjacent += int(np.count_nonzero(samples[1:] == samples[:-1]))
        if previous_last is not None and samples[0] == previous_last:
            repeated_adjacent += 1
        previous_last = samples[-1]
        clipped_samples += int(np.count_nonzero(np.abs(samples) >= args.clip_amplitude))

        blocks = samples[:usable].reshape(-1, args.fft_size) * window
        psd = np.mean(
            np.abs(np.fft.fftshift(np.fft.fft(blocks, axis=1), axes=1)) ** 2,
            axis=0,
        ) + 1e-30
        psds[second] = psd
        outer_power = band_mean(psd, outer_mask, "outer band")
        signal_power = band_mean(psd, signal_mask, "control-channel band")
        near_power = band_mean(psd, near_mask, "near band")
        timeline_sample = nearest_timeline_sample(
            timeline, trigger_unix_ms + second * 1000.0
        )
        rows.append(
            {
                "secondFromTrigger": second,
                "liveDecodeRate": float(timeline_sample["liveDecodeRate"]),
                "shadowDecodeRate": float(timeline_sample["shadowDecodeRate"]),
                "liveControlChannelHz": float(
                    timeline_sample["liveControlChannelHz"]
                ),
                "rawPowerDb": db10(float(np.mean(np.abs(samples) ** 2))),
                "signalToOuterDb": db10(signal_power / outer_power),
                "nearToOuterDb": db10(near_power / outer_power),
                "outerPowerDb": db10(outer_power),
                "peakAmplitude": float(np.max(np.abs(samples))),
            }
        )

    pre_rows = [row for row in rows if row["secondFromTrigger"] <= 0]
    low_predicate = lambda row: row["liveDecodeRate"] <= args.low_max_rate
    baseline_predicate = lambda row: row["liveDecodeRate"] >= args.baseline_min_rate
    low_metrics = mean_metrics(pre_rows, low_predicate)
    baseline_metrics = mean_metrics(pre_rows, baseline_predicate)
    delta = metric_delta(low_metrics, baseline_metrics)

    neighboring_changes: list[dict[str, float]] = []
    low_seconds = [row["secondFromTrigger"] for row in pre_rows if low_predicate(row)]
    baseline_seconds = [
        row["secondFromTrigger"] for row in pre_rows if baseline_predicate(row)
    ]
    if low_seconds and baseline_seconds:
        low_psd = np.mean([psds[second] for second in low_seconds], axis=0)
        baseline_psd = np.mean([psds[second] for second in baseline_seconds], axis=0)
        band_start = -args.near_max_hz
        while band_start < args.near_max_hz:
            band_end = band_start + args.localized_band_width_hz
            mask = (frequencies >= band_start) & (frequencies < band_end)
            overlaps_control_channel = (
                band_start < args.signal_half_width_hz
                and band_end > -args.signal_half_width_hz
            )
            if np.any(mask) and not overlaps_control_channel:
                low_db = db10(float(np.mean(low_psd[mask])))
                baseline_db = db10(float(np.mean(baseline_psd[mask])))
                band_delta = low_db - baseline_db
                if abs(band_delta) >= args.localized_delta_threshold_db:
                    neighboring_changes.append(
                        {
                            "centerOffsetHz": (band_start + band_end) / 2.0,
                            "lowMinusBaselineDb": band_delta,
                        }
                    )
            band_start = band_end

    integrity = {
        "finite": non_finite == 0,
        "nonFiniteSamples": non_finite,
        "zeroSamples": zero_samples,
        "repeatedAdjacentSamples": repeated_adjacent,
        "clippedSamples": clipped_samples,
    }
    classification = classify(delta, integrity)
    classification["neighboringSpectralChanges"] = neighboring_changes

    return {
        "schemaVersion": 1,
        "capture": {
            "iqPath": str(args.iq),
            "metadataPath": str(args.metadata),
            "systemShortName": metadata.get("systemShortName"),
            "captureGroupUnixMs": metadata.get("captureGroupUnixMs"),
            "triggerUnixMs": trigger_unix_ms,
            "completedUnixMs": completed_unix_ms,
            "sampleRate": sample_rate,
            "actualSamples": actual_samples,
            "expectedSamples": expected_samples,
            "pairValidation": pair_validation,
        },
        "thresholds": {
            "lowMaxRate": args.low_max_rate,
            "baselineMinRate": args.baseline_min_rate,
        },
        "integrity": integrity,
        "preTriggerLow": low_metrics,
        "preTriggerBaseline": baseline_metrics,
        "preTriggerLowMinusBaseline": delta,
        "classification": classification,
        "timeline": rows,
    }


def main() -> int:
    args = parse_args()
    result = analyze(args)
    rendered = json.dumps(result, indent=2, sort_keys=True, allow_nan=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered + os.linesep, encoding="utf-8")
    print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
