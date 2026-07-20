#!/usr/bin/env python3
"""Run a shadow-only typed-evidence incident transition experiment."""

from __future__ import annotations

import argparse
import json
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from run_incident_incremental_update_bakeoff import (
    find_pair,
    observation_source,
)
from run_incident_observation_interpretation_bakeoff import (
    completion_request,
    parse_content,
    read_package,
    valid_uncertainty,
)


TRANSCRIPT_CITATION_SCHEMA = {
    "type": "object",
    "properties": {
        "transcript_id": {"type": "string"},
        "exact_quote": {"type": "string"},
    },
    "required": ["transcript_id", "exact_quote"],
    "additionalProperties": False,
}


EVIDENCE_SCHEMA = {
    "type": "object",
    "properties": {
        "verdict": {
            "type": "string",
            "enum": ["supports_shared_event", "supports_distinct_event", "unresolved"],
        },
        "uncertainty": {"type": "number", "minimum": 0, "maximum": 1},
        "relationship_statement": {"type": "string"},
        "prior_evidence": {"type": "array", "items": TRANSCRIPT_CITATION_SCHEMA},
        "new_evidence": {"type": "array", "items": TRANSCRIPT_CITATION_SCHEMA},
        "unresolved_questions": {"type": "array", "items": {"type": "string"}},
    },
    "required": [
        "verdict",
        "uncertainty",
        "relationship_statement",
        "prior_evidence",
        "new_evidence",
        "unresolved_questions",
    ],
    "additionalProperties": False,
}

PROMPT = """Compare one prior radio-call observation with one new observation.
Return a typed evidence record only. Do not create or revise an incident, hypothesis, identifier, category, label, responder role, or operational action.

Choose supports_shared_event only when transcript evidence from both observations positively supports one unfolding real-world event. Choose supports_distinct_event only when transcript evidence from both observations positively supports different real-world events. Otherwise choose unresolved. Similar timing, system, talkgroup, frequency, source, retrieval score, transcript model agreement, absence of contradiction, or absence of shared wording never proves either relationship.

For a decisive verdict, explain the relationship in relationship_statement and provide at least one exact transcript citation in each of prior_evidence and new_evidence. Copy exact_quote as a literal substring of the supplied transcript without adding quotation marks. Do not return observation identifiers or cite metadata; the application owns observation identity. Preserve transcript disagreement and ambiguity. An unresolved verdict must include at least one concrete unresolved question. Use uncertainty from 0 to 1, where 1 is highly uncertain. The application validates this record and alone decides whether any shadow-state transition is admissible."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    parser.add_argument("--pair", action="append", required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    parser.add_argument("--max-output-tokens", type=int, default=4096)
    return parser.parse_args()


def parse_pair(value: str) -> tuple[str, str]:
    parts = [part.strip() for part in value.split(",")]
    if len(parts) != 2 or not all(parts) or parts[0] == parts[1]:
        raise ValueError(f"invalid pair '{value}'")
    return parts[0], parts[1]


def validate_evidence_record(
    record: dict[str, Any],
    observations: dict[str, dict[str, Any]],
) -> list[str]:
    errors: list[str] = []
    verdict = record.get("verdict")
    if verdict not in {"supports_shared_event", "supports_distinct_event", "unresolved"}:
        errors.append("evidence verdict is invalid")
    if not valid_uncertainty(record.get("uncertainty")):
        errors.append("evidence uncertainty is invalid")

    relationship_statement = record.get("relationship_statement")
    if not isinstance(relationship_statement, str) or not relationship_statement.strip():
        errors.append("relationship statement is required")

    observation_list = list(observations.values())
    for field, observation in zip(("prior_evidence", "new_evidence"), observation_list, strict=True):
        citations = record.get(field, [])
        if not isinstance(citations, list):
            errors.append(f"{field} must be an array")
            citations = []
        if verdict in {"supports_shared_event", "supports_distinct_event"} and not citations:
            errors.append(f"decisive evidence verdict must include {field}")
        transcripts = {
            transcript["transcript_id"]: transcript.get("text", "")
            for transcript in observation.get("transcripts", [])
        }
        for index, citation in enumerate(citations):
            transcript_id = citation.get("transcript_id")
            quote = citation.get("exact_quote")
            if transcript_id not in transcripts:
                errors.append(f"{field}[{index}] cites an unknown transcript")
            elif not isinstance(quote, str) or not quote or quote not in transcripts[transcript_id]:
                errors.append(f"{field}[{index}] quote does not occur exactly in the transcript")

    questions = record.get("unresolved_questions", [])
    if not isinstance(questions, list):
        errors.append("unresolved questions must be an array")
        questions = []
    if any(not isinstance(question, str) or not question.strip() for question in questions):
        errors.append("unresolved questions must be non-empty strings")
    if verdict == "unresolved" and not questions:
        errors.append("unresolved verdict must preserve an unresolved question")
    return errors


def application_transition(verdict: str | None, validation_errors: list[str]) -> dict[str, str]:
    if validation_errors:
        return {
            "operation": "defer_observation",
            "reason": "model evidence record was rejected by deterministic validation",
        }
    if verdict == "supports_shared_event":
        return {
            "operation": "append_to_prior_hypothesis",
            "reason": "admitted typed evidence supports a shared event",
        }
    if verdict == "supports_distinct_event":
        return {
            "operation": "create_single_observation_hypothesis",
            "reason": "admitted typed evidence supports distinct events",
        }
    return {
        "operation": "defer_observation",
        "reason": "typed evidence remains unresolved",
    }


def main() -> int:
    args = parse_args()
    package, _ = read_package(args.input)
    args.output_directory.mkdir(parents=True, exist_ok=True)
    for pair_text in args.pair:
        pair = parse_pair(pair_text)
        bundle_id, raw_observations = find_pair(package, pair)
        observations = [observation_source(item) for item in raw_observations]
        observations_by_id = {item["observation_id"]: item for item in observations}
        source = {
            "bundle_id": bundle_id,
            "prior_observation": observations[0],
            "new_observation": observations[1],
        }
        artifact: dict[str, Any] = {
            "run_id": uuid.uuid4().hex,
            "started_at_utc": datetime.now(timezone.utc).isoformat(),
            "input_path": str(args.input.resolve()),
            "bundle_id": bundle_id,
            "observation_ids": list(pair),
            "model_requested": args.model,
            "prompt_identity": "incident-typed-evidence-v2",
            "source": source,
            "model_call": None,
            "evidence_record": None,
            "evidence_validation_errors": [],
            "application_transition": None,
        }
        model_call = completion_request(
            args.base_url,
            args.model,
            PROMPT,
            source,
            "incident_typed_evidence",
            EVIDENCE_SCHEMA,
            args.max_output_tokens,
            args.timeout_seconds,
        )
        artifact["model_call"] = model_call
        errors: list[str] = []
        record = parse_content(model_call, "typed evidence generator", errors)
        if record is not None:
            artifact["evidence_record"] = record
            errors.extend(validate_evidence_record(record, observations_by_id))
        artifact["evidence_validation_errors"] = errors
        artifact["application_transition"] = application_transition(
            record.get("verdict") if record else None,
            errors,
        )
        output = args.output_directory / (
            f"{pair[0].replace(':', '-')}--{pair[1].replace(':', '-')}-{artifact['run_id']}.json"
        )
        output.write_text(json.dumps(artifact, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(
            json.dumps(
                {
                    "observation_ids": list(pair),
                    "artifact": str(output),
                    "errors": len(errors),
                    "transition": artifact["application_transition"]["operation"],
                }
            )
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
