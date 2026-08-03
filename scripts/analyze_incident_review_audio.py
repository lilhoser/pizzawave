#!/usr/bin/env python3
"""Measure reviewed call audio and build a small, local listening comparison.

This tool intentionally uses only the Python standard library. It reads the
preserved review package and reviewer export, writes measurements, and creates
processed WAV copies. It never changes source audio.
"""

from __future__ import annotations

import argparse
import array
import csv
import html
import json
import math
import pathlib
import shutil
import wave
from collections import defaultdict
from dataclasses import asdict, dataclass
from typing import Iterable


PCM_MAX = 32768.0


@dataclass(frozen=True)
class AudioMetrics:
    duration_seconds: float
    rms_dbfs: float
    peak_dbfs: float
    near_silent_fraction: float
    low_level_fraction: float
    active_seconds: float
    clipped_fraction: float


def dbfs(value: float) -> float:
    return -120.0 if value <= 0 else 20.0 * math.log10(value / PCM_MAX)


def read_pcm16_mono(path: pathlib.Path) -> tuple[int, array.array]:
    with wave.open(str(path), "rb") as wav:
        if wav.getnchannels() != 1 or wav.getsampwidth() != 2 or wav.getcomptype() != "NONE":
            raise ValueError(f"Expected mono 16-bit PCM WAV: {path}")
        rate = wav.getframerate()
        samples = array.array("h")
        samples.frombytes(wav.readframes(wav.getnframes()))
    return rate, samples


def write_pcm16_mono(path: pathlib.Path, rate: int, samples: Iterable[int]) -> None:
    payload = array.array("h", (max(-32768, min(32767, int(round(x)))) for x in samples))
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(payload.tobytes())


def measure(rate: int, samples: array.array) -> AudioMetrics:
    if not samples:
        return AudioMetrics(0, -120, -120, 1, 1, 0, 0)
    rms = math.sqrt(sum(x * x for x in samples) / len(samples))
    peak = max(abs(x) for x in samples)
    frame_size = max(1, rate // 50)
    frame_dbfs = []
    for offset in range(0, len(samples), frame_size):
        frame = samples[offset : offset + frame_size]
        frame_rms = math.sqrt(sum(x * x for x in frame) / len(frame))
        frame_dbfs.append(dbfs(frame_rms))
    return AudioMetrics(
        duration_seconds=len(samples) / rate,
        rms_dbfs=dbfs(rms),
        peak_dbfs=dbfs(peak),
        near_silent_fraction=sum(x < -45 for x in frame_dbfs) / len(frame_dbfs),
        low_level_fraction=sum(x < -35 for x in frame_dbfs) / len(frame_dbfs),
        active_seconds=sum(x >= -45 for x in frame_dbfs) / 50.0,
        clipped_fraction=sum(abs(x) >= 32600 for x in samples) / len(samples),
    )


def peak_normalize(samples: Iterable[float], target_dbfs: float = -3, max_gain_db: float = 30) -> array.array:
    values = list(samples)
    peak = max((abs(x) for x in values), default=0)
    if peak <= 0:
        return array.array("h", (0 for _ in values))
    desired_gain = (PCM_MAX * (10 ** (target_dbfs / 20))) / peak
    gain = min(desired_gain, 10 ** (max_gain_db / 20))
    return array.array("h", (max(-32768, min(32767, round(x * gain))) for x in values))


def low_pass(samples: Iterable[float], rate: int, cutoff_hz: float) -> list[float]:
    values = list(samples)
    if not values:
        return []
    dt = 1.0 / rate
    rc = 1.0 / (2 * math.pi * cutoff_hz)
    alpha = dt / (rc + dt)
    output = [float(values[0])]
    for value in values[1:]:
        output.append(output[-1] + alpha * (value - output[-1]))
    return output


def high_pass(samples: Iterable[float], rate: int, cutoff_hz: float) -> list[float]:
    values = list(samples)
    if not values:
        return []
    dt = 1.0 / rate
    rc = 1.0 / (2 * math.pi * cutoff_hz)
    alpha = rc / (rc + dt)
    output = [0.0]
    for index in range(1, len(values)):
        output.append(alpha * (output[-1] + values[index] - values[index - 1]))
    return output


def speech_band_normalize(rate: int, samples: array.array) -> array.array:
    # Two passes make these gentle first-order filters useful without imposing a
    # sharp, artifact-prone cutoff. The passband is appropriate for 8 kHz radio.
    filtered: Iterable[float] = samples
    for _ in range(2):
        filtered = high_pass(filtered, rate, 180)
    for _ in range(2):
        filtered = low_pass(filtered, rate, 3400)
    return peak_normalize(filtered)


def load_rows(package_path: pathlib.Path, review_path: pathlib.Path) -> list[dict]:
    package = json.loads(package_path.read_text(encoding="utf-8"))
    review = json.loads(review_path.read_text(encoding="utf-8"))
    reviews = {item["package_key"]: item for item in review["packages"]}
    rows = []
    for review_package in package["packages"]:
        package_key = review_package["package_key"]
        decision = reviews[package_key]
        included = {
            source_key
            for incident in decision["incidents"]
            for source_key in incident["source_keys"]
        }
        for evidence in review_package["evidence"]:
            source_key = evidence["source_key"]
            audio_path = package_path.parent / evidence["audio_file"]
            rate, samples = read_pcm16_mono(audio_path)
            metrics = measure(rate, samples)
            transcripts = evidence.get("transcripts") or []
            rows.append(
                {
                    "package_key": package_key,
                    "source_key": source_key,
                    "audio_path": str(audio_path.resolve()),
                    "call_id": evidence["source_manifest"]["call_id"],
                    "observed_at_unix_seconds": evidence["observed_at_unix_seconds"],
                    "system": evidence["metadata"].get("systemShortName", ""),
                    "talkgroup": evidence["metadata"].get("talkgroupName", ""),
                    "talkgroup_id": evidence["metadata"].get("talkgroup", ""),
                    "frequency_hz": evidence["metadata"].get("frequency", ""),
                    "transcript": transcripts[0].get("text", "") if transcripts else "",
                    "review_choice": (
                        "include"
                        if source_key in included
                        else decision.get("source_review_choices", {}).get(source_key, "unreviewed")
                    ),
                    "human_marked_unintelligible": source_key
                    in decision.get("unintelligible_audio_source_keys", []),
                    **asdict(metrics),
                }
            )
    return rows


def pick_comparison_rows(rows: list[dict], count: int) -> list[dict]:
    unintelligible = sorted(
        (row for row in rows if row["human_marked_unintelligible"]),
        key=lambda row: row["peak_dbfs"],
    )
    understandable = sorted(
        (row for row in rows if not row["human_marked_unintelligible"]),
        key=lambda row: row["peak_dbfs"],
    )

    def evenly_spaced(values: list[dict], wanted: int) -> list[dict]:
        if wanted >= len(values):
            return values
        if wanted == 1:
            return [values[len(values) // 2]]
        return [values[round(index * (len(values) - 1) / (wanted - 1))] for index in range(wanted)]

    unintelligible_count = min(len(unintelligible), max(1, round(count * 2 / 3)))
    selected = evenly_spaced(unintelligible, unintelligible_count)
    selected.extend(evenly_spaced(understandable, min(len(understandable), count - len(selected))))
    return selected


def write_measurements(rows: list[dict], output_dir: pathlib.Path) -> None:
    fields = list(rows[0])
    with (output_dir / "measurements.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    (output_dir / "measurements.json").write_text(
        json.dumps(rows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )


def build_listening_page(selected: list[dict], output_dir: pathlib.Path) -> None:
    audio_dir = output_dir / "audio"
    audio_dir.mkdir(exist_ok=True)
    cards = []
    for index, row in enumerate(selected, 1):
        source_path = pathlib.Path(row["audio_path"])
        rate, samples = read_pcm16_mono(source_path)
        stem = f"sample-{index:02d}"
        original = audio_dir / f"{stem}-original.wav"
        louder = audio_dir / f"{stem}-louder.wav"
        speech = audio_dir / f"{stem}-speech-band.wav"
        shutil.copyfile(source_path, original)
        write_pcm16_mono(louder, rate, peak_normalize(samples))
        write_pcm16_mono(speech, rate, speech_band_normalize(rate, samples))
        cards.append(
            f"""
            <section class="card" data-sample="{stem}">
              <h2>Recording {index} of {len(selected)}</h2>
              <p class="detail">{html.escape(row['talkgroup'])} · {row['duration_seconds']:.1f} seconds</p>
              <div class="versions">
                <label>Original<audio controls preload="metadata" src="audio/{original.name}"></audio></label>
                <label>Volume adjusted<audio controls preload="metadata" src="audio/{louder.name}"></audio></label>
                <label>Speech frequencies and volume adjusted<audio controls preload="metadata" src="audio/{speech.name}"></audio></label>
              </div>
              <fieldset><legend>Which is easiest to understand?</legend>
                <label><input type="radio" name="{stem}" value="original"> Original</label>
                <label><input type="radio" name="{stem}" value="louder"> Volume adjusted</label>
                <label><input type="radio" name="{stem}" value="speech-band"> Speech frequencies and volume adjusted</label>
                <label><input type="radio" name="{stem}" value="none"> None are understandable</label>
                <label><input type="radio" name="{stem}" value="same"> No meaningful difference</label>
              </fieldset>
            </section>"""
        )
    page = """<!doctype html><html><head><meta charset="utf-8"><title>PizzaWave audio processing check</title>
<style>body{font:16px system-ui;margin:0;background:#f4f5f7;color:#18212f}.wrap{max-width:960px;margin:auto;padding:24px}.intro,.card{background:white;border:1px solid #d8dde6;border-radius:10px;padding:20px;margin-bottom:16px}h1{margin-top:0}.detail{color:#586477}.versions{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}.versions label{font-weight:650}.versions audio{display:block;width:100%;height:32px;margin-top:7px}fieldset{margin-top:18px;border:0;padding:0}fieldset label{display:block;padding:5px 0}.actions{position:sticky;bottom:0;background:#18212f;color:white;padding:14px;border-radius:10px}button{font:inherit;padding:9px 14px}@media(max-width:700px){.versions{grid-template-columns:1fr}}</style></head><body><main class="wrap"><section class="intro"><h1>Can simple processing make these recordings understandable?</h1><p>Listen to all three versions of each recording. Choose the version that is easiest to understand. Choose “None are understandable” when processing does not recover understandable speech. You do not need to identify the exact words. The original recordings have not been changed.</p><p>This is only a test of volume adjustment and a gentle speech-frequency filter. It is not a transcription-model comparison.</p></section>
""" + "\n".join(cards) + """
<div class="actions"><button id="download">Download answers</button> <span id="status"></span></div></main><script>
document.getElementById('download').addEventListener('click',()=>{const answers={created_at_utc:new Date().toISOString(),samples:{}};document.querySelectorAll('.card').forEach(c=>{const checked=c.querySelector('input:checked');answers.samples[c.dataset.sample]=checked?checked.value:null});const missing=Object.values(answers.samples).filter(x=>x===null).length;if(missing&&!confirm(`${missing} recording(s) have no answer. Download anyway?`))return;const blob=new Blob([JSON.stringify(answers,null,2)+'\\n'],{type:'application/json'});const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='pizzawave-audio-processing-review.json';a.click();URL.revokeObjectURL(a.href);document.getElementById('status').textContent='Answers downloaded.'});
</script></body></html>"""
    (output_dir / "index.html").write_text(page, encoding="utf-8")


def write_summary(rows: list[dict], selected: list[dict], output_dir: pathlib.Path) -> None:
    groups = defaultdict(list)
    for row in rows:
        groups["unintelligible" if row["human_marked_unintelligible"] else "not_marked_unintelligible"].append(row)
    summary = {
        "total_recordings": len(rows),
        "systems": sorted({row["system"] for row in rows}),
        "selected_comparison_recordings": len(selected),
        "groups": {
            label: {
                "recordings": len(values),
                "mean_duration_seconds": sum(x["duration_seconds"] for x in values) / len(values),
                "mean_rms_dbfs": sum(x["rms_dbfs"] for x in values) / len(values),
                "mean_near_silent_fraction": sum(x["near_silent_fraction"] for x in values) / len(values),
                "mean_active_seconds": sum(x["active_seconds"] for x in values) / len(values),
            }
            for label, values in groups.items()
        },
    }
    (output_dir / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")


def write_listening_results(
    selected: list[dict], listening_review_path: pathlib.Path, output_dir: pathlib.Path
) -> None:
    review = json.loads(listening_review_path.read_text(encoding="utf-8"))
    answers = review.get("samples", {})
    mapped = []
    for index, row in enumerate(selected, 1):
        sample_key = f"sample-{index:02d}"
        mapped.append(
            {
                "sample_key": sample_key,
                "answer": answers.get(sample_key),
                "package_key": row["package_key"],
                "source_key": row["source_key"],
                "call_id": row["call_id"],
                "talkgroup": row["talkgroup"],
                "duration_seconds": row["duration_seconds"],
                "human_marked_unintelligible_before_comparison": row[
                    "human_marked_unintelligible"
                ],
            }
        )
    allowed = {"original", "louder", "speech-band", "none", "same"}
    missing = [item["sample_key"] for item in mapped if item["answer"] not in allowed]
    if missing:
        raise ValueError(f"Listening review has missing or invalid answers: {', '.join(missing)}")
    counts = {answer: sum(item["answer"] == answer for item in mapped) for answer in sorted(allowed)}
    previously_unintelligible = [
        item for item in mapped if item["human_marked_unintelligible_before_comparison"]
    ]
    result = {
        "created_at_utc": review.get("created_at_utc"),
        "recordings": len(mapped),
        "answer_counts": counts,
        "previously_unintelligible_recordings": len(previously_unintelligible),
        "previously_unintelligible_answer_counts": {
            answer: sum(item["answer"] == answer for item in previously_unintelligible)
            for answer in sorted(allowed)
        },
        "mapped_answers": mapped,
    }
    (output_dir / "listening-results.json").write_text(
        json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=pathlib.Path, required=True)
    parser.add_argument("--review", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--comparison-count", type=int, default=12)
    parser.add_argument("--listening-review", type=pathlib.Path)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if not 1 <= args.comparison_count <= 30:
        raise SystemExit("--comparison-count must be between 1 and 30")
    args.output.mkdir(parents=True, exist_ok=True)
    rows = load_rows(args.package, args.review)
    if not rows:
        raise SystemExit("Review package contained no audio evidence")
    selected = pick_comparison_rows(rows, args.comparison_count)
    write_measurements(rows, args.output)
    write_summary(rows, selected, args.output)
    build_listening_page(selected, args.output)
    if args.listening_review:
        write_listening_results(selected, args.listening_review, args.output)
    print(f"Measured {len(rows)} recordings and created {len(selected)} listening comparisons in {args.output.resolve()}")


if __name__ == "__main__":
    main()
