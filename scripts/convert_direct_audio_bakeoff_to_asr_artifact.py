#!/usr/bin/env python3
"""Convert selected direct-audio results into an ASR artifact for blind review."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--direct-audio-artifact", type=Path, required=True)
    parser.add_argument("--review-package", type=Path, required=True)
    parser.add_argument("--review-audio-root", type=Path, required=True)
    parser.add_argument("--engine", required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.resolve().read_text(encoding="utf-8-sig"))


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.resolve().read_bytes()).hexdigest().upper()


def main() -> int:
    args = parse_args()
    inputs = [args.direct_audio_artifact, args.review_package, args.review_audio_root]
    if any("heldout-sealed" in str(path.resolve()).lower() for path in inputs):
        raise ValueError("the direct-audio converter refuses sealed held-out paths")

    direct = read_json(args.direct_audio_artifact)
    review = read_json(args.review_package)
    observation_by_audio_hash: dict[str, str] = {}
    for item in review["items"]:
        audio_path = args.review_audio_root.resolve() / Path(item["audio_file"]).name
        audio_hash = sha256_file(audio_path)
        if audio_hash in observation_by_audio_hash:
            raise ValueError(f"duplicate review audio hash: {audio_hash}")
        observation_by_audio_hash[audio_hash] = item["observation_id"]

    results = []
    for result in direct["results"]:
        audio_hash = result["audio_sha256"]
        if audio_hash not in observation_by_audio_hash:
            raise ValueError(f"direct-audio result is absent from review package: {audio_hash}")
        results.append(
            {
                "observation_id": observation_by_audio_hash[audio_hash],
                "transcript": result["transcription"],
                "inference_milliseconds": result["transcription_milliseconds"],
                "audio_sha256": audio_hash,
            }
        )

    if len(results) != len(review["items"]):
        raise ValueError("direct-audio results do not cover every review item")

    output = {
        "artifact_version": 1,
        "package_sha256": review["package_sha256"],
        "engine": args.engine,
        "model_id": direct["model"],
        "source_artifact_sha256": sha256_file(args.direct_audio_artifact),
        "results": sorted(results, key=lambda item: item["observation_id"]),
    }
    output_path = args.output.resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(output, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(output_path), "results": len(results)}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
