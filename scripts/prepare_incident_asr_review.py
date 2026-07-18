#!/usr/bin/env python3
"""Prepare a blind human-review subset from completed development ASR runs."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--audio-root", type=Path, required=True)
    parser.add_argument("--asr-artifact", type=Path, action="append", required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--high-disagreement-per-tertile", type=int, default=4)
    parser.add_argument("--low-disagreement-per-tertile", type=int, default=2)
    return parser.parse_args()


def words(value: str) -> list[str]:
    normalized = "".join(character.lower() if character.isalnum() else " " for character in value)
    return normalized.split()


def normalized_edit_distance(left: str, right: str) -> float:
    left_words = words(left)
    right_words = words(right)
    previous = list(range(len(right_words) + 1))
    for left_index, left_word in enumerate(left_words, 1):
        current = [left_index]
        for right_index, right_word in enumerate(right_words, 1):
            current.append(min(
                current[-1] + 1,
                previous[right_index] + 1,
                previous[right_index - 1] + (left_word != right_word),
            ))
        previous = current
    return previous[-1] / max(len(left_words), len(right_words), 1)


def main() -> int:
    args = parse_args()
    package_path = args.package.resolve()
    if "heldout-sealed" in str(package_path).lower():
        raise ValueError("the review preparer refuses sealed held-out corpus paths")
    if len(args.asr_artifact) < 2:
        raise ValueError("at least two ASR artifacts are required")

    package_bytes = package_path.read_bytes()
    package_hash = hashlib.sha256(package_bytes).hexdigest().upper()
    package = json.loads(package_bytes.decode("utf-8-sig"))
    observations: dict[str, dict[str, Any]] = {}
    for bundle in package["bundles"]:
        for observation in bundle["observations"]:
            transcripts = observation.get("transcripts") or []
            observations[observation["observationId"]] = {
                "bundle_id": bundle["bundleId"],
                "audio_reference": observation["audioReference"],
                "audio_duration_milliseconds": observation["audioDurationMilliseconds"],
                "stored_transcript": transcripts[0]["text"] if transcripts else "",
            }

    sources: dict[str, dict[str, str]] = {
        "stored": {
            observation_id: observation["stored_transcript"]
            for observation_id, observation in observations.items()
        }
    }
    artifact_hashes: dict[str, str] = {}
    for artifact_path in args.asr_artifact:
        artifact_bytes = artifact_path.resolve().read_bytes()
        artifact = json.loads(artifact_bytes.decode("utf-8-sig"))
        if artifact["package_sha256"] != package_hash:
            raise ValueError(f"package hash mismatch in {artifact_path}")
        source_id = artifact["engine"]
        if source_id in sources:
            raise ValueError(f"duplicate review source: {source_id}")
        sources[source_id] = {
            item["observation_id"]: item["transcript"] or ""
            for item in artifact["results"]
        }
        artifact_hashes[source_id] = hashlib.sha256(artifact_bytes).hexdigest().upper()

    eligible = set(observations)
    for source in sources.values():
        eligible.intersection_update(source)
    if not eligible:
        raise ValueError("ASR artifacts and package share no observations")

    scored: list[dict[str, Any]] = []
    source_ids = sorted(sources)
    for observation_id in eligible:
        pair_distances = []
        for left_index, left_source in enumerate(source_ids):
            for right_source in source_ids[left_index + 1:]:
                pair_distances.append(normalized_edit_distance(
                    sources[left_source][observation_id],
                    sources[right_source][observation_id],
                ))
        scored.append({
            "observation_id": observation_id,
            "duration": observations[observation_id]["audio_duration_milliseconds"],
            "disagreement": sum(pair_distances) / len(pair_distances),
        })

    # Duration tertiles cover short, medium, and long calls without selecting on
    # talkgroup, transcript content, incident history, or application labels.
    by_duration = sorted(scored, key=lambda item: (item["duration"], item["observation_id"]))
    tertiles = [
        by_duration[len(by_duration) * index // 3: len(by_duration) * (index + 1) // 3]
        for index in range(3)
    ]
    selected: dict[str, dict[str, Any]] = {}
    selection_reasons: dict[str, str] = {}
    for tertile_index, tertile in enumerate(tertiles, 1):
        ranked = sorted(tertile, key=lambda item: (-item["disagreement"], item["observation_id"]))
        for item in ranked[: args.high_disagreement_per_tertile]:
            selected[item["observation_id"]] = item
            selection_reasons[item["observation_id"]] = f"duration-tertile-{tertile_index}-high-disagreement"
        for item in reversed(ranked[-args.low_disagreement_per_tertile:]):
            selected[item["observation_id"]] = item
            selection_reasons[item["observation_id"]] = f"duration-tertile-{tertile_index}-low-disagreement"

    output = args.output_directory.resolve()
    audio_output = output / "audio"
    audio_output.mkdir(parents=True, exist_ok=True)
    review_items = []
    answer_key = []
    review_template = []
    for item_number, observation_id in enumerate(sorted(selected), 1):
        observation = observations[observation_id]
        source_audio = (args.audio_root / observation["audio_reference"]).resolve()
        if not source_audio.is_file():
            raise FileNotFoundError(source_audio)
        audio_name = f"item-{item_number:02d}-{observation_id.replace(':', '-')}.wav"
        shutil.copy2(source_audio, audio_output / audio_name)

        # Stable per-observation permutation hides model identity while keeping
        # independently completed review packages directly comparable.
        ordered_sources = sorted(source_ids, key=lambda source_id: hashlib.sha256(
            f"{package_hash}:{observation_id}:{source_id}".encode("utf-8")
        ).hexdigest())
        candidates = []
        mappings = []
        for candidate_index, source_id in enumerate(ordered_sources):
            candidate_id = chr(ord("A") + candidate_index)
            candidates.append({"candidate_id": candidate_id, "text": sources[source_id][observation_id]})
            mappings.append({"candidate_id": candidate_id, "source_id": source_id})
        review_items.append({
            "item_id": f"item-{item_number:02d}",
            "observation_id": observation_id,
            "bundle_id": observation["bundle_id"],
            "audio_file": f"audio/{audio_name}",
            "audio_duration_milliseconds": observation["audio_duration_milliseconds"],
            "selection_reason": selection_reasons[observation_id],
            "candidates": candidates,
        })
        answer_key.append({"item_id": f"item-{item_number:02d}", "candidate_sources": mappings})
        review_template.append({
            "item_id": f"item-{item_number:02d}",
            "reviewer_id": "",
            "audio_intelligibility_0_to_3": None,
            "best_candidate_ids": [],
            "all_candidates_materially_wrong": None,
            "reviewer_transcript": "",
            "uncertain_fragments": [],
            "notes": "",
        })

    manifest = {
        "review_package_version": 1,
        "package_sha256": package_hash,
        "selection_algorithm": "duration-tertiles-with-high-and-low-three-way-normalized-word-edit-disagreement-v1",
        "reviewer_instructions": [
            "Complete the review independently without consulting another reviewer or the answer key.",
            "Listen to the audio before reading candidate transcripts.",
            "Score intelligibility: 0 unintelligible, 1 fragments, 2 mostly intelligible, 3 clear.",
            "Select every candidate that is materially acceptable; selecting none is valid.",
            "Write what you hear and preserve uncertainty instead of guessing.",
        ],
        "items": review_items,
    }
    output.mkdir(parents=True, exist_ok=True)
    (output / "review-package.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    (output / "review-template.json").write_text(json.dumps(review_template, indent=2), encoding="utf-8")
    (output / "answer-key.json").write_text(json.dumps({
        "package_sha256": package_hash,
        "asr_artifact_sha256": artifact_hashes,
        "items": answer_key,
    }, indent=2), encoding="utf-8")
    reviewer_page = Path(__file__).with_name("incident_asr_reviewer.html")
    if not reviewer_page.is_file():
        raise FileNotFoundError(reviewer_page)
    shutil.copy2(reviewer_page, output / "reviewer.html")
    print(json.dumps({
        "output_directory": str(output),
        "eligible_observations": len(eligible),
        "review_items": len(review_items),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
