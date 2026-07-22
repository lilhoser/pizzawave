#!/usr/bin/env python3
"""Run development-only pairwise observation relationship proposals and critiques."""

from __future__ import annotations

import argparse
import json
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from run_incident_observation_interpretation_bakeoff import (
    completion_request,
    parse_content,
    read_package,
    valid_uncertainty,
)


PROVENANCE_SCHEMA = {
    "type": "object",
    "properties": {
        "observation_id": {"type": "string"},
        "transcript_id": {
            "type": "string",
            "description": "Exact supplied transcript id, or empty for metadata evidence.",
        },
        "exact_quote": {
            "type": "string",
            "description": "Exact text copied from that transcript, or empty for metadata evidence.",
        },
        "metadata_field": {
            "type": "string",
            "description": "Exact supplied metadata key only, or empty for transcript evidence.",
        },
    },
    "required": ["observation_id", "transcript_id", "exact_quote", "metadata_field"],
    "additionalProperties": False,
}


STATEMENT_SCHEMA = {
    "type": "object",
    "properties": {
        "statement_id": {"type": "string"},
        "statement": {"type": "string"},
        "uncertainty": {"type": "number", "minimum": 0, "maximum": 1},
        "provenance": {
            "type": "array",
            "minItems": 2,
            "items": PROVENANCE_SCHEMA,
        },
    },
    "required": ["statement_id", "statement", "uncertainty", "provenance"],
    "additionalProperties": False,
}

PROPOSAL_SCHEMA = {
    "type": "object",
    "properties": {
        "proposal_id": {"type": "string"},
        "bundle_id": {"type": "string"},
        "observation_ids": {
            "type": "array",
            "minItems": 2,
            "maxItems": 2,
            "items": {"type": "string"},
        },
        "possible_relationships": {"type": "array", "items": STATEMENT_SCHEMA},
        "evidence_against_relationship": {"type": "array", "items": STATEMENT_SCHEMA},
        "unresolved_questions": {"type": "array", "items": {"type": "string"}},
    },
    "required": [
        "proposal_id",
        "bundle_id",
        "observation_ids",
        "possible_relationships",
        "evidence_against_relationship",
        "unresolved_questions",
    ],
    "additionalProperties": False,
}

FINDING_SCHEMA = {
    "type": "object",
    "properties": {
        "finding_id": {"type": "string"},
        "statement": {"type": "string"},
        "uncertainty": {"type": "number", "minimum": 0, "maximum": 1},
        "provenance": {
            "type": "array",
            "minItems": 2,
            "items": PROVENANCE_SCHEMA,
        },
    },
    "required": ["finding_id", "statement", "uncertainty", "provenance"],
    "additionalProperties": False,
}

CRITIQUE_SCHEMA = {
    "type": "object",
    "properties": {
        "critique_id": {"type": "string"},
        "proposal_id": {"type": "string"},
        "summary": {"type": "string"},
        "findings": {"type": "array", "items": FINDING_SCHEMA},
    },
    "required": ["critique_id", "proposal_id", "summary", "findings"],
    "additionalProperties": False,
}

PROPOSER_PROMPT = """Compare exactly two radio-call observations using their competing transcript candidates.
Your task is limited to possible relationships between these two observations. Do not create an event or incident, assign membership, name or categorize an event, assign a call role, or infer operational significance.

A possible relationship statement must be supported by specific quoted content from both observations. Source metadata may contextualize that text evidence but is never itself a relationship: do not report shared system, talkgroup, frequency, source, or temporal proximity as a possible relationship. Quote existence alone does not establish that your statement is entailed. Do not add entities, actions, causes, responses, destinations, or continuity that the combined evidence does not support.

Return no possible relationship when the evidence is insufficient. Preserve counterevidence and unresolved questions. Every relationship or counterevidence statement must cite evidence from both observations. Statement IDs must be nonempty and unique across both possible_relationships and evidence_against_relationship. Each provenance item identifies exactly one evidence kind: transcript evidence uses an exact transcript_id and exact, case-sensitive copied text while metadata_field is empty; metadata evidence uses the exact supplied metadata key while transcript_id and exact_quote are empty. Uncertainty is from 0 to 1, where 1 is highly uncertain; do not use zero when competing transcripts materially disagree or when continuity remains unproven."""

CRITIC_PROMPT = """Independently critique a proposed relationship analysis for exactly two radio-call observations.
Check whether every proposed relationship is supported by its cited transcript evidence from both observations. Identify added entities, actions, causes, responses, destinations, continuity, false certainty, omitted counterevidence, omitted plausible alternatives, or metadata-only relationships. Source metadata may contextualize text evidence but shared system, talkgroup, frequency, source, or temporal proximity never proves a relationship.

Do not create an event or incident, assign membership, name or categorize an event, or assign a call role. Every finding must cite evidence from both observations. Each provenance item identifies exactly one evidence kind: transcript evidence uses an exact transcript_id and exact copied text while metadata_field is empty; metadata evidence uses the exact supplied metadata key while transcript_id and exact_quote are empty. Return no findings only when the proposal is fully grounded and preserves uncertainty."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument(
        "--pair",
        action="append",
        required=True,
        help="Two observation IDs separated by a comma",
    )
    parser.add_argument("--base-url", default="http://127.0.0.1:1234/v1")
    parser.add_argument("--model", required=True)
    parser.add_argument("--max-output-tokens", type=int, default=8192)
    parser.add_argument("--timeout-seconds", type=int, default=600)
    return parser.parse_args()


def pair_source(bundle_id: str, observations: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "bundle_id": bundle_id,
        "observations": [
            {
                "observation_id": observation["observationId"],
                "observed_at_unix_seconds": observation["observedAtUnixSeconds"],
                "transcripts": [
                    {
                        "transcript_id": transcript["transcriptId"],
                        "producer": transcript.get("producer", ""),
                        "text": transcript.get("text", ""),
                    }
                    for transcript in observation.get("transcripts", [])
                ],
                "metadata": {
                    field: value
                    for field, value in observation.get("metadata", {}).items()
                    if field in {
                        "source",
                        "systemShortName",
                        "talkgroup",
                        "frequency",
                        "stopTimeUnixSeconds",
                    }
                },
            }
            for observation in observations
        ],
    }


def validate_provenance(
    provenance: Any,
    observations: dict[str, dict[str, Any]],
    owner: str,
    errors: list[str],
) -> None:
    if not isinstance(provenance, list) or not provenance:
        errors.append(f"{owner} must include source provenance")
        return
    cited_observations: set[str] = set()
    for item in provenance:
        observation_id = item.get("observation_id")
        if observation_id not in observations:
            errors.append(f"{owner} cites an observation outside the compared pair")
            continue
        cited_observations.add(observation_id)
        transcripts = {
            transcript["transcriptId"]: transcript.get("text", "")
            for transcript in observations[observation_id].get("transcripts", [])
        }
        transcript_id = item.get("transcript_id")
        quote = item.get("exact_quote")
        metadata_field = item.get("metadata_field")
        has_transcript = bool(transcript_id)
        has_metadata = bool(metadata_field)
        if not has_transcript and not has_metadata:
            errors.append(f"{owner} provenance identifies no source material")
        if has_transcript and has_metadata:
            errors.append(f"{owner} provenance mixes transcript and metadata evidence")
        if has_transcript:
            if transcript_id not in transcripts:
                errors.append(f"{owner} cites unknown transcript '{transcript_id}'")
            elif not isinstance(quote, str) or not quote or quote not in transcripts[transcript_id]:
                errors.append(f"{owner} quote does not occur exactly in transcript '{transcript_id}'")
        elif quote:
            errors.append(f"{owner} metadata provenance must not contain an exact quote")
        if has_metadata:
            if metadata_field not in observations[observation_id].get("metadata", {}):
                errors.append(f"{owner} cites unknown metadata field '{metadata_field}'")
            if transcript_id or quote:
                errors.append(f"{owner} metadata provenance must leave transcript fields empty")
    if cited_observations != set(observations):
        errors.append(f"{owner} must cite both compared observations")


def validate_proposal(
    proposal: Any,
    bundle_id: str,
    observations: dict[str, dict[str, Any]],
    errors: list[str],
) -> None:
    if not isinstance(proposal, dict):
        return
    if proposal.get("bundle_id") != bundle_id:
        errors.append("proposal bundle id does not match")
    if set(proposal.get("observation_ids", [])) != set(observations):
        errors.append("proposal observation ids do not match the compared pair")
    ids: set[str] = set()
    for collection_name in ("possible_relationships", "evidence_against_relationship"):
        for statement in proposal.get(collection_name, []):
            statement_id = statement.get("statement_id")
            if not statement_id or statement_id in ids:
                errors.append(f"duplicate or missing relationship statement id '{statement_id}'")
            ids.add(statement_id)
            if not statement.get("statement"):
                errors.append(f"relationship statement '{statement_id}' has no text")
            if not valid_uncertainty(statement.get("uncertainty")):
                errors.append(f"relationship statement '{statement_id}' has invalid uncertainty")
            validate_provenance(
                statement.get("provenance"),
                observations,
                f"relationship statement '{statement_id}'",
                errors,
            )


def validate_critique(
    critique: Any,
    proposal: dict[str, Any],
    observations: dict[str, dict[str, Any]],
    errors: list[str],
) -> None:
    if not isinstance(critique, dict):
        return
    if critique.get("proposal_id") != proposal.get("proposal_id"):
        errors.append("critique proposal id does not match")
    if not critique.get("summary"):
        errors.append("critique summary is required")
    ids: set[str] = set()
    for finding in critique.get("findings", []):
        finding_id = finding.get("finding_id")
        if not finding_id or finding_id in ids:
            errors.append(f"duplicate or missing critique finding id '{finding_id}'")
        ids.add(finding_id)
        if not finding.get("statement"):
            errors.append(f"critique finding '{finding_id}' has no text")
        if not valid_uncertainty(finding.get("uncertainty")):
            errors.append(f"critique finding '{finding_id}' has invalid uncertainty")
        validate_provenance(
            finding.get("provenance"),
            observations,
            f"critique finding '{finding_id}'",
            errors,
        )


def main() -> int:
    args = parse_args()
    package, _ = read_package(args.input)
    indexed = {
        observation["observationId"]: (bundle["bundleId"], observation)
        for bundle in package["bundles"]
        for observation in bundle["observations"]
    }
    pairs = []
    for pair_value in args.pair:
        observation_ids = pair_value.split(",")
        if len(observation_ids) != 2 or len(set(observation_ids)) != 2:
            raise ValueError(f"pair must contain two distinct observation ids: {pair_value}")
        if any(observation_id not in indexed for observation_id in observation_ids):
            raise ValueError(f"pair references an unknown observation: {pair_value}")
        bundle_ids = {indexed[observation_id][0] for observation_id in observation_ids}
        if len(bundle_ids) != 1:
            raise ValueError(f"pair crosses source bundles: {pair_value}")
        pairs.append((observation_ids, bundle_ids.pop()))

    output_directory = args.output_directory.resolve()
    output_directory.mkdir(parents=True, exist_ok=True)
    for observation_ids, bundle_id in pairs:
        pair_observations = [indexed[observation_id][1] for observation_id in observation_ids]
        observations = {observation["observationId"]: observation for observation in pair_observations}
        source = pair_source(bundle_id, pair_observations)
        run_id = uuid.uuid4().hex
        output_path = output_directory / f"{'--'.join(value.replace(':', '-') for value in observation_ids)}-{run_id}.json"
        artifact: dict[str, Any] = {
            "run_id": run_id,
            "started_at_utc": datetime.now(timezone.utc).isoformat(),
            "input_path": str(args.input.resolve()),
            "bundle_id": bundle_id,
            "observation_ids": observation_ids,
            "model_requested": args.model,
            "proposer_prompt_identity": "incident-observation-relationship-proposer-v4",
            "critic_prompt_identity": "incident-observation-relationship-critic-v4",
            "source": source,
            "proposer_call": None,
            "proposal": None,
            "proposal_validation_errors": ["validation did not complete"],
            "critic_call": None,
            "critique": None,
            "critique_validation_errors": ["critic did not run"],
        }
        output_path.write_text(json.dumps(artifact, indent=2), encoding="utf-8")

        proposer_call = completion_request(
            args.base_url,
            args.model,
            PROPOSER_PROMPT,
            source,
            "incident_observation_relationship_proposal",
            PROPOSAL_SCHEMA,
            args.max_output_tokens,
            args.timeout_seconds,
        )
        proposal_errors: list[str] = []
        proposal = parse_content(proposer_call, "relationship proposer", proposal_errors)
        validate_proposal(proposal, bundle_id, observations, proposal_errors)
        artifact.update(
            proposer_call=proposer_call,
            proposal=proposal,
            proposal_validation_errors=proposal_errors,
        )
        output_path.write_text(json.dumps(artifact, indent=2), encoding="utf-8")

        if not proposal_errors and proposal is not None:
            critic_call = completion_request(
                args.base_url,
                args.model,
                CRITIC_PROMPT,
                {"source": source, "proposed_relationship_analysis": proposal},
                "incident_observation_relationship_critique",
                CRITIQUE_SCHEMA,
                args.max_output_tokens,
                args.timeout_seconds,
            )
            critique_errors: list[str] = []
            critique = parse_content(critic_call, "relationship critic", critique_errors)
            validate_critique(critique, proposal, observations, critique_errors)
            artifact.update(
                critic_call=critic_call,
                critique=critique,
                critique_validation_errors=critique_errors,
            )
            output_path.write_text(json.dumps(artifact, indent=2), encoding="utf-8")

        print(
            json.dumps(
                {
                    "observation_ids": observation_ids,
                    "artifact": str(output_path),
                    "proposal_errors": len(artifact["proposal_validation_errors"]),
                    "critique_errors": len(artifact["critique_validation_errors"]),
                }
            )
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
