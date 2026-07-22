#!/usr/bin/env python3
"""Run a single-generation, shadow-only incremental event-state experiment."""

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
from run_incident_observation_relationship_bakeoff import (
    PROVENANCE_SCHEMA,
    STATEMENT_SCHEMA,
)


HYPOTHESIS_SCHEMA = {
    "type": "object",
    "properties": {
        "hypothesis_id": {"type": "string"},
        "supersedes_hypothesis_ids": {"type": "array", "items": {"type": "string"}},
        "observation_ids": {"type": "array", "items": {"type": "string"}},
        "uncertainty": {"type": "number", "minimum": 0, "maximum": 1},
        "claims": {"type": "array", "items": STATEMENT_SCHEMA},
        "relationships": {"type": "array", "items": STATEMENT_SCHEMA},
        "alternatives": {"type": "array", "items": STATEMENT_SCHEMA},
        "unresolved_questions": {"type": "array", "items": {"type": "string"}},
    },
    "required": [
        "hypothesis_id",
        "supersedes_hypothesis_ids",
        "observation_ids",
        "uncertainty",
        "claims",
        "relationships",
        "alternatives",
        "unresolved_questions",
    ],
    "additionalProperties": False,
}

DEFERRED_SCHEMA = {
    "type": "object",
    "properties": {
        "observation_id": {"type": "string"},
        "reason": {"type": "string"},
        "uncertainty": {"type": "number", "minimum": 0, "maximum": 1},
        "provenance": {"type": "array", "items": PROVENANCE_SCHEMA},
        "unresolved_questions": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["observation_id", "reason", "uncertainty", "provenance", "unresolved_questions"],
    "additionalProperties": False,
}

PROPOSAL_SCHEMA = {
    "type": "object",
    "properties": {
        "proposal_id": {"type": "string"},
        "bundle_id": {"type": "string"},
        "new_observation_id": {"type": "string"},
        "revised_hypotheses": {"type": "array", "items": HYPOTHESIS_SCHEMA, "maxItems": 1},
        "new_hypotheses": {"type": "array", "items": HYPOTHESIS_SCHEMA, "maxItems": 1},
        "deferred_observations": {"type": "array", "items": DEFERRED_SCHEMA, "maxItems": 1},
    },
    "required": [
        "proposal_id",
        "bundle_id",
        "new_observation_id",
        "revised_hypotheses",
        "new_hypotheses",
        "deferred_observations",
    ],
    "additionalProperties": False,
}

PROMPT = """Perform one incremental update to revisable shadow event state.
You receive exactly one prior single-observation hypothesis and one new radio-call observation. Use every competing transcript as source evidence. Produce exactly one outcome:
- revise the prior hypothesis only when transcript evidence from both observations supports a possible shared unfolding event;
- create a separate new single-observation hypothesis when the new observation supports a distinct possible event;
- defer the new observation when the evidence is insufficient or materially ambiguous.

Do not choose an event category, responder role, operational significance, or production action. Shared system, talkgroup, frequency, source, timing, retrieval, or model agreement is context only and never proves membership. Every claim, relationship, and alternative must use exact transcript quotations or exact supplied metadata field names. A revised hypothesis must include a relationship statement citing both observations. Do not turn a critic, metadata match, or absence of contradiction into relationship evidence. Use uncertainty from 0 to 1, where 1 is highly uncertain. Preserve contradictions and unresolved questions. This is a shadow proposal only."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    parser.add_argument("--pair", action="append", required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    parser.add_argument("--max-output-tokens", type=int, default=8192)
    return parser.parse_args()


def parse_pair(value: str) -> tuple[str, str]:
    parts = [part.strip() for part in value.split(",")]
    if len(parts) != 2 or not all(parts) or parts[0] == parts[1]:
        raise ValueError(f"invalid pair '{value}'")
    return parts[0], parts[1]


def observation_source(observation: dict[str, Any]) -> dict[str, Any]:
    return {
        "observation_id": observation["observationId"],
        "observed_at_unix_seconds": observation["observedAtUnixSeconds"],
        "audio_reference": observation.get("audioReference", ""),
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
            if field in {"source", "systemShortName", "talkgroup", "frequency", "stopTimeUnixSeconds"}
        },
    }


def find_pair(package: dict[str, Any], pair: tuple[str, str]) -> tuple[str, list[dict[str, Any]]]:
    for bundle in package.get("bundles", []):
        by_id = {observation["observationId"]: observation for observation in bundle.get("observations", [])}
        if pair[0] in by_id and pair[1] in by_id:
            return bundle["bundleId"], [by_id[pair[0]], by_id[pair[1]]]
    raise KeyError(f"pair does not occur in one bundle: {pair}")


def validate_statement(
    statement: dict[str, Any],
    observations: dict[str, dict[str, Any]],
    owner: str,
    errors: list[str],
) -> None:
    if not statement.get("statement_id"):
        errors.append(f"{owner} statement id is required")
    if not statement.get("statement"):
        errors.append(f"{owner} statement is required")
    if not valid_uncertainty(statement.get("uncertainty")):
        errors.append(f"{owner} uncertainty is invalid")
    validate_provenance(statement.get("provenance"), observations, owner, errors)


def validate_provenance(
    provenance: Any,
    observations: dict[str, dict[str, Any]],
    owner: str,
    errors: list[str],
) -> None:
    if not isinstance(provenance, list) or not provenance:
        errors.append(f"{owner} must include source provenance")
        return
    for item in provenance:
        observation_id = item.get("observation_id")
        if observation_id not in observations:
            errors.append(f"{owner} cites an unknown observation")
            continue
        observation = observations[observation_id]
        transcripts = {
            transcript["transcript_id"]: transcript.get("text", "")
            for transcript in observation.get("transcripts", [])
        }
        transcript_id = item.get("transcript_id")
        quote = item.get("exact_quote")
        metadata_field = item.get("metadata_field")
        has_transcript = bool(transcript_id)
        has_metadata = bool(metadata_field)
        if has_transcript == has_metadata:
            errors.append(f"{owner} provenance must identify exactly one source kind")
            continue
        if has_transcript:
            if transcript_id not in transcripts:
                errors.append(f"{owner} cites unknown transcript '{transcript_id}'")
            elif not isinstance(quote, str) or not quote or quote not in transcripts[transcript_id]:
                errors.append(f"{owner} quote does not occur exactly in transcript '{transcript_id}'")
        else:
            if transcript_id or quote:
                errors.append(f"{owner} metadata provenance must leave transcript fields empty")
            if metadata_field not in observation.get("metadata", {}):
                errors.append(f"{owner} cites unknown metadata field '{metadata_field}'")


def validate_hypothesis(
    hypothesis: dict[str, Any],
    observations: dict[str, dict[str, Any]],
    owner: str,
    errors: list[str],
) -> None:
    if not hypothesis.get("hypothesis_id"):
        errors.append(f"{owner} hypothesis id is required")
    if not valid_uncertainty(hypothesis.get("uncertainty")):
        errors.append(f"{owner} uncertainty is invalid")
    if len(hypothesis.get("observation_ids", [])) != len(set(hypothesis.get("observation_ids", []))):
        errors.append(f"{owner} observation ids must be unique")
    if not set(hypothesis.get("observation_ids", [])).issubset(observations):
        errors.append(f"{owner} contains an unknown observation")
    statement_ids: list[str] = []
    hypothesis_observation_ids = set(hypothesis.get("observation_ids", []))
    for field in ("claims", "relationships", "alternatives"):
        for index, statement in enumerate(hypothesis.get(field, [])):
            validate_statement(statement, observations, f"{owner} {field}[{index}]", errors)
            statement_ids.append(statement.get("statement_id", ""))
            cited = {item.get("observation_id") for item in statement.get("provenance", [])}
            if not cited.issubset(hypothesis_observation_ids):
                errors.append(f"{owner} {field}[{index}] cites an observation outside the hypothesis")
    if len(statement_ids) != len(set(statement_ids)):
        errors.append(f"{owner} statement ids must be unique")
    for question in hypothesis.get("unresolved_questions", []):
        if not isinstance(question, str) or not question.strip():
            errors.append(f"{owner} has an empty unresolved question")


def validate_proposal(
    proposal: dict[str, Any],
    bundle_id: str,
    prior_id: str,
    prior_observation_id: str,
    new_observation_id: str,
    observations: dict[str, dict[str, Any]],
) -> list[str]:
    errors: list[str] = []
    if proposal.get("bundle_id") != bundle_id:
        errors.append("proposal bundle id does not match")
    if proposal.get("new_observation_id") != new_observation_id:
        errors.append("proposal new observation id does not match")
    if not proposal.get("proposal_id"):
        errors.append("proposal id is required")
    revised = proposal.get("revised_hypotheses", [])
    created = proposal.get("new_hypotheses", [])
    deferred = proposal.get("deferred_observations", [])
    if len(revised) + len(created) + len(deferred) != 1:
        errors.append("proposal must produce exactly one incremental outcome")
    for hypothesis in revised + created:
        validate_hypothesis(hypothesis, observations, "incremental hypothesis", errors)
    if revised:
        hypothesis = revised[0]
        if hypothesis.get("supersedes_hypothesis_ids") != [prior_id]:
            errors.append("revised hypothesis must supersede exactly the supplied prior hypothesis")
        expected = {prior_observation_id, new_observation_id}
        if set(hypothesis.get("observation_ids", [])) != expected:
            errors.append("revised hypothesis must contain exactly the prior and new observations")
        if not hypothesis.get("relationships"):
            errors.append("revised hypothesis must include relationship evidence")
        for relationship in hypothesis.get("relationships", []):
            transcript_cited = {
                item.get("observation_id")
                for item in relationship.get("provenance", [])
                if item.get("transcript_id") and item.get("exact_quote")
            }
            if transcript_cited != expected:
                errors.append("revised relationship must cite transcript evidence from both observations")
    if created:
        hypothesis = created[0]
        if hypothesis.get("supersedes_hypothesis_ids"):
            errors.append("new hypothesis must not supersede prior state")
        if hypothesis.get("observation_ids") != [new_observation_id]:
            errors.append("new hypothesis must contain only the new observation")
        if not hypothesis.get("claims"):
            errors.append("new hypothesis must include at least one grounded claim")
    if deferred:
        item = deferred[0]
        if item.get("observation_id") != new_observation_id:
            errors.append("deferred outcome must identify the new observation")
        if not item.get("reason"):
            errors.append("deferred outcome reason is required")
        if not valid_uncertainty(item.get("uncertainty")):
            errors.append("deferred outcome uncertainty is invalid")
        if not item.get("unresolved_questions"):
            errors.append("deferred outcome must preserve an unresolved question")
        if item.get("provenance"):
            validate_provenance(item.get("provenance"), observations, "deferred outcome", errors)
    return errors


def main() -> int:
    args = parse_args()
    package, _ = read_package(args.input)
    args.output_directory.mkdir(parents=True, exist_ok=True)
    for pair_text in args.pair:
        pair = parse_pair(pair_text)
        bundle_id, raw_observations = find_pair(package, pair)
        observations = [observation_source(item) for item in raw_observations]
        observations_by_id = {item["observation_id"]: item for item in observations}
        prior_id = f"prior:{pair[0]}"
        source = {
            "bundle_id": bundle_id,
            "prior_hypothesis": {
                "hypothesis_id": prior_id,
                "observation_ids": [pair[0]],
                "source_observations": [observations[0]],
            },
            "new_observation": observations[1],
        }
        started = datetime.now(timezone.utc)
        artifact: dict[str, Any] = {
            "run_id": uuid.uuid4().hex,
            "started_at_utc": started.isoformat(),
            "input_path": str(args.input.resolve()),
            "bundle_id": bundle_id,
            "observation_ids": list(pair),
            "model_requested": args.model,
            "prompt_identity": "incident-incremental-update-proposer-v1",
            "source": source,
            "model_call": None,
            "proposal": None,
            "proposal_validation_errors": [],
        }
        model_call = completion_request(
            args.base_url,
            args.model,
            PROMPT,
            source,
            "incident_incremental_update",
            PROPOSAL_SCHEMA,
            args.max_output_tokens,
            args.timeout_seconds,
        )
        artifact["model_call"] = model_call
        proposal_errors: list[str] = []
        proposal = parse_content(model_call, "incremental update proposer", proposal_errors)
        if proposal is not None:
            artifact["proposal"] = proposal
            proposal_errors.extend(
                validate_proposal(
                    proposal,
                    bundle_id,
                    prior_id,
                    pair[0],
                    pair[1],
                    observations_by_id,
                )
            )
        artifact["proposal_validation_errors"] = proposal_errors
        output = args.output_directory / f"{pair[0].replace(':', '-') }--{pair[1].replace(':', '-')}-{artifact['run_id']}.json"
        output.write_text(json.dumps(artifact, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(json.dumps({"observation_ids": list(pair), "artifact": str(output), "errors": len(artifact["proposal_validation_errors"])}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
