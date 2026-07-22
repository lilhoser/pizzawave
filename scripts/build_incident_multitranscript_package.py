#!/usr/bin/env python3
"""Add ASR bakeoff outputs as alternate transcripts in a development package."""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--asr-artifact", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.resolve().read_text(encoding="utf-8-sig"))


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.resolve().read_bytes()).hexdigest().upper()


def main() -> int:
    args = parse_args()
    package_path = args.package.resolve()
    if "heldout-sealed" in str(package_path).lower():
        raise ValueError("the multi-transcript builder refuses sealed held-out corpus paths")

    package_hash = sha256_file(package_path)
    package = copy.deepcopy(read_json(package_path))
    observations = {
        observation["observationId"]: observation
        for bundle in package["bundles"]
        for observation in bundle["observations"]
    }
    source_artifacts = []
    transcript_ids = {
        transcript["transcriptId"]
        for observation in observations.values()
        for transcript in observation.get("transcripts", [])
    }

    for artifact_path in args.asr_artifact:
        artifact = read_json(artifact_path)
        if artifact["package_sha256"] != package_hash:
            raise ValueError(f"package hash mismatch in {artifact_path}")
        engine = artifact["engine"]
        source_artifacts.append({
            "engine": engine,
            "model_id": artifact["model_id"],
            "sha256": sha256_file(artifact_path),
        })
        for result in artifact["results"]:
            observation_id = result["observation_id"]
            if observation_id not in observations:
                raise ValueError(f"ASR result references unknown observation '{observation_id}'")
            transcript_id = f"{observation_id}:{engine}"
            if transcript_id in transcript_ids:
                raise ValueError(f"duplicate transcript id '{transcript_id}'")
            transcript_ids.add(transcript_id)
            observations[observation_id].setdefault("transcripts", []).append({
                "transcriptId": transcript_id,
                "text": result["transcript"] or "",
                "producer": engine,
            })

    package["multiTranscriptDerivation"] = {
        "sourcePackageSha256": package_hash,
        "sourceArtifacts": source_artifacts,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(package, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({
        "output": str(args.output.resolve()),
        "bundles": len(package["bundles"]),
        "observations": len(observations),
        "alternate_sources": len(source_artifacts),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
