#!/usr/bin/env python3
"""Build a local-only, blind relationship-review package from development data."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import tarfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--audio-archive", required=True, type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    parser.add_argument("--pair", action="append", required=True)
    parser.add_argument("--exclude-review-package", type=Path)
    parser.add_argument(
        "--template",
        type=Path,
        default=Path(__file__).with_name("incident_relationship_reviewer.html"),
    )
    return parser.parse_args()


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def parse_pair(value: str) -> tuple[str, str]:
    parts = [part.strip() for part in value.split(",")]
    if len(parts) != 2 or not all(parts) or parts[0] == parts[1]:
        raise ValueError(f"invalid pair '{value}'; expected two distinct comma-separated observation ids")
    return parts[0], parts[1]


def excluded_observation_ids(path: Path | None) -> set[str]:
    if path is None:
        return set()
    package = read_json(path)
    return {
        item.get("observation_id", "")
        for item in package.get("items", [])
        if item.get("observation_id")
    }


def metadata_value(observation: dict[str, Any], field: str) -> str:
    value = observation.get("metadata", {}).get(field, "")
    if isinstance(value, dict):
        return str(value.get("value", ""))
    return str(value)


def observation_for_review(
    observation: dict[str, Any],
    case_number: int,
    side: str,
) -> dict[str, Any]:
    observation_id = observation["observationId"]
    safe_id = observation_id.replace(":", "-")
    audio_suffix = Path(observation["audioReference"]).suffix or ".wav"
    return {
        "observation_id": observation_id,
        "observed_at_unix_seconds": observation["observedAtUnixSeconds"],
        "audio_source_reference": observation["audioReference"],
        "audio_file": f"audio/case-{case_number:02d}-{side}-{safe_id}{audio_suffix}",
        "audio_duration_milliseconds": observation.get("audioDurationMilliseconds"),
        "metadata": {
            field: metadata_value(observation, field)
            for field in ("systemShortName", "talkgroup", "frequency", "stopTimeUnixSeconds")
        },
        "transcripts": [
            {
                "transcript_id": transcript["transcriptId"],
                "producer": transcript.get("producer", ""),
                "text": transcript.get("text", ""),
            }
            for transcript in observation.get("transcripts", [])
        ],
    }


def copy_audio(
    archive: tarfile.TarFile,
    source_reference: str,
    destination: Path,
) -> None:
    normalized = source_reference.replace("\\", "/").lstrip("/")
    candidates = [normalized, f"audio/{normalized}", f"development/audio/{normalized}"]
    member = next((archive.getmember(name) for name in candidates if name in archive.getnames()), None)
    if member is None or not member.isfile():
        raise FileNotFoundError(f"audio archive does not contain '{source_reference}'")
    source = archive.extractfile(member)
    if source is None:
        raise FileNotFoundError(f"could not read '{source_reference}' from audio archive")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with source, destination.open("wb") as target:
        shutil.copyfileobj(source, target)


def main() -> int:
    args = parse_args()
    for path in (args.input, args.audio_archive, args.template):
        if "heldout-sealed" in {part.lower() for part in path.parts}:
            raise ValueError(f"refusing to open sealed held-out path: {path}")
    if args.output_directory.exists():
        raise FileExistsError(f"output directory already exists: {args.output_directory}")

    corpus = read_json(args.input)
    excluded = excluded_observation_ids(args.exclude_review_package)
    bundle_by_observation: dict[str, dict[str, Any]] = {}
    observation_by_id: dict[str, dict[str, Any]] = {}
    for bundle in corpus.get("bundles", []):
        for observation in bundle.get("observations", []):
            observation_id = observation["observationId"]
            bundle_by_observation[observation_id] = bundle
            observation_by_id[observation_id] = observation

    pairs = [parse_pair(value) for value in args.pair]
    cases: list[dict[str, Any]] = []
    seen_observations: set[str] = set()
    for index, pair in enumerate(pairs, start=1):
        if any(observation_id in excluded for observation_id in pair):
            raise ValueError(f"pair {pair} repeats an observation from the excluded review package")
        if any(observation_id in seen_observations for observation_id in pair):
            raise ValueError(f"observation is repeated across review cases: {pair}")
        missing = [observation_id for observation_id in pair if observation_id not in observation_by_id]
        if missing:
            raise KeyError(f"unknown observations: {missing}")
        bundle = bundle_by_observation[pair[0]]
        if bundle is not bundle_by_observation[pair[1]]:
            raise ValueError(f"pair must come from one development bundle: {pair}")
        seen_observations.update(pair)
        cases.append(
            {
                "case_id": f"relationship-case-{index:02d}",
                "bundle_id": bundle["bundleId"],
                "observations": [
                    observation_for_review(observation_by_id[pair[0]], index, "a"),
                    observation_for_review(observation_by_id[pair[1]], index, "b"),
                ],
            }
        )

    package = {
        "review_package_version": "incident-relationship-review-v1",
        "created_at_utc": datetime.now(timezone.utc).isoformat(),
        "source_corpus_id": corpus.get("corpusId", ""),
        "source_corpus_version": corpus.get("corpusVersion", ""),
        "blind": True,
        "local_only": True,
        "instructions": (
            "Review only the supplied audio, transcript candidates, timestamps, and neutral radio metadata. "
            "Describe any supported relationship without using existing incident records or model output."
        ),
        "cases": cases,
    }
    package["content_sha256"] = hashlib.sha256(canonical_bytes(package)).hexdigest().upper()

    args.output_directory.mkdir(parents=True)
    with tarfile.open(args.audio_archive, "r:gz") as archive:
        for case in cases:
            for observation in case["observations"]:
                copy_audio(
                    archive,
                    observation["audio_source_reference"],
                    args.output_directory / observation["audio_file"],
                )

    shutil.copyfile(args.template, args.output_directory / "reviewer.html")
    package_json = json.dumps(package, ensure_ascii=False, separators=(",", ":"))
    (args.output_directory / "review-package.js").write_text(
        f"window.INCIDENT_RELATIONSHIP_REVIEW_PACKAGE={package_json};\n",
        encoding="utf-8",
    )
    review_template = {
        "review_version": "incident-relationship-review-v1",
        "package_sha256": package["content_sha256"],
        "reviewer": "",
        "started_at_utc": "",
        "completed_at_utc": "",
        "cases": [],
    }
    (args.output_directory / "review-template.json").write_text(
        json.dumps(review_template, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    print(json.dumps({"output_directory": str(args.output_directory), "cases": len(cases), "package_sha256": package["content_sha256"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
