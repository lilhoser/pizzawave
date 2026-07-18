#!/usr/bin/env python3
"""Critique saved observation interpretations with a separately loaded model."""

from __future__ import annotations

import argparse
import json
import uuid
from datetime import datetime, timezone
from pathlib import Path

from run_incident_observation_interpretation_bakeoff import (
    CRITIC_PROMPT,
    CRITIQUE_SCHEMA,
    completion_request,
    parse_content,
    read_package,
    source_document,
    validate_critique,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-artifact", type=Path, action="append", required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--base-url", default="http://127.0.0.1:1234/v1")
    parser.add_argument("--model", required=True)
    parser.add_argument("--max-output-tokens", type=int, default=8192)
    parser.add_argument("--timeout-seconds", type=int, default=600)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    output_directory = args.output_directory.resolve()
    output_directory.mkdir(parents=True, exist_ok=True)

    for source_path_arg in args.source_artifact:
        source_path = source_path_arg.resolve()
        if "heldout-sealed" in str(source_path).lower():
            raise ValueError("the cross-critic refuses sealed held-out artifact paths")
        source_artifact = json.loads(source_path.read_text(encoding="utf-8-sig"))
        interpretation = source_artifact.get("interpretation")
        if not interpretation or source_artifact.get("interpretation_validation_errors"):
            raise ValueError(f"source artifact has no validated interpretation: {source_path}")

        package, _ = read_package(Path(source_artifact["input_path"]))
        observations = {
            observation["observationId"]: observation
            for bundle in package["bundles"]
            for observation in bundle["observations"]
        }
        observation_id = source_artifact["observation_id"]
        if observation_id not in observations:
            raise ValueError(f"source observation is absent from its package: {observation_id}")
        observation = observations[observation_id]
        source = source_document(observation)

        call = completion_request(
            args.base_url,
            args.model,
            CRITIC_PROMPT,
            {"observation": source, "proposed_interpretation": interpretation},
            "incident_observation_cross_model_critique",
            CRITIQUE_SCHEMA,
            args.max_output_tokens,
            args.timeout_seconds,
        )
        errors: list[str] = []
        critique = parse_content(call, "cross-model critic", errors)
        validate_critique(critique, interpretation, observation, errors)

        run_id = uuid.uuid4().hex
        output_path = output_directory / f"{observation_id.replace(':', '-')}-{run_id}.json"
        output = {
            "run_id": run_id,
            "started_at_utc": datetime.now(timezone.utc).isoformat(),
            "source_artifact": str(source_path),
            "source_run_id": source_artifact["run_id"],
            "observation_id": observation_id,
            "interpretation_model": source_artifact["model_requested"],
            "critic_model_requested": args.model,
            "critic_prompt_identity": "incident-observation-interpretation-critic-v1",
            "source": source,
            "interpretation": interpretation,
            "critic_call": call,
            "critique": critique,
            "validation_errors": errors,
        }
        output_path.write_text(json.dumps(output, indent=2), encoding="utf-8")
        print(
            json.dumps(
                {
                    "observation_id": observation_id,
                    "artifact": str(output_path),
                    "validation_errors": len(errors),
                }
            )
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
