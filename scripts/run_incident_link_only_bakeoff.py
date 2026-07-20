#!/usr/bin/env python3
"""Run the one-sided, shadow-only incident link proposal contract."""

from __future__ import annotations

import argparse
import json
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from run_incident_incremental_update_bakeoff import find_pair, observation_source
from run_incident_observation_interpretation_bakeoff import (
    completion_request,
    parse_content,
    read_package,
    valid_uncertainty,
)


CITATION_SCHEMA = {
    "type": "object",
    "properties": {
        "transcript_id": {"type": "string"},
        "exact_quote": {"type": "string"},
    },
    "required": ["transcript_id", "exact_quote"],
    "additionalProperties": False,
}

LINK_SCHEMA = {
    "type": "object",
    "properties": {
        "decision": {"type": "string", "enum": ["propose_link", "abstain"]},
        "candidate_token": {"type": "string", "enum": ["candidate-1", ""]},
        "relationship_statement": {"type": "string"},
        "uncertainty": {"type": "number", "minimum": 0, "maximum": 1},
        "new_observation_evidence": {"type": "array", "items": CITATION_SCHEMA},
        "candidate_evidence": {"type": "array", "items": CITATION_SCHEMA},
        "unresolved_questions": {"type": "array", "items": {"type": "string"}},
    },
    "required": [
        "decision",
        "candidate_token",
        "relationship_statement",
        "uncertainty",
        "new_observation_evidence",
        "candidate_evidence",
        "unresolved_questions",
    ],
    "additionalProperties": False,
}

PROMPT = """You propose one source-grounded connection between a new radio observation and an existing event, or abstain. Your output is shadow evidence only. Application code owns identifiers, state projection, validation, and persistence.

Determine whether positive transcript evidence supports linking the new observation to exactly one supplied candidate event. Use propose_link only when the new observation and the selected candidate both contain positive evidence of one unfolding real-world event. Otherwise use abstain. Abstention means unresolved; it does not mean the observations describe different events.

You may not declare a different event, create an event, split an event, assign a category or role, or change event state. Timing, system, talkgroup, frequency, retrieval, and metadata may provide context, but none proves a link by itself.

For propose_link, use candidate_token candidate-1 and cite literal transcript substrings from both the new observation and the candidate. Do not alter a quote or combine separate fragments. For abstain, return an empty candidate_token, no candidate evidence, and at least one concrete unresolved question."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    parser.add_argument("--pair", action="append", required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--timeout-seconds", type=int, default=120)
    parser.add_argument("--max-output-tokens", type=int, default=2048)
    return parser.parse_args()


def parse_pair(value: str) -> tuple[str, str]:
    parts = [part.strip() for part in value.split(",")]
    if len(parts) != 2 or not all(parts) or parts[0] == parts[1]:
        raise ValueError(f"invalid pair '{value}'")
    return parts[0], parts[1]


def validate_citations(
    citations: Any,
    observation: dict[str, Any],
    owner: str,
    required: bool,
    errors: list[str],
) -> None:
    if not isinstance(citations, list):
        errors.append(f"{owner} must be an array")
        return
    if required and not citations:
        errors.append(f"{owner} must include an exact transcript citation")
    transcripts = {
        item["transcript_id"]: item.get("text", "")
        for item in observation.get("transcripts", [])
    }
    for index, citation in enumerate(citations):
        if not isinstance(citation, dict):
            errors.append(f"{owner}[{index}] must be an object")
            continue
        transcript_id = citation.get("transcript_id")
        quote = citation.get("exact_quote")
        if transcript_id not in transcripts:
            errors.append(f"{owner}[{index}] cites an unknown transcript")
        elif not isinstance(quote, str) or not quote or quote not in transcripts[transcript_id]:
            errors.append(f"{owner}[{index}] quote does not occur exactly in the transcript")


def validate_link_output(
    output: dict[str, Any],
    candidate: dict[str, Any],
    new_observation: dict[str, Any],
) -> list[str]:
    errors: list[str] = []
    decision = output.get("decision")
    if decision not in {"propose_link", "abstain"}:
        errors.append("link decision is invalid")
    if not valid_uncertainty(output.get("uncertainty")):
        errors.append("link uncertainty is invalid")
    statement = output.get("relationship_statement")
    if not isinstance(statement, str):
        errors.append("relationship statement must be a string")
    questions = output.get("unresolved_questions")
    if not isinstance(questions, list) or any(
        not isinstance(question, str) or not question.strip() for question in questions
    ):
        errors.append("unresolved questions must be non-empty strings")
        questions = []

    if decision == "propose_link":
        if output.get("candidate_token") != "candidate-1":
            errors.append("positive link must select candidate-1")
        if not isinstance(statement, str) or not statement.strip():
            errors.append("positive link requires a relationship statement")
        validate_citations(
            output.get("new_observation_evidence"),
            new_observation,
            "new observation evidence",
            True,
            errors,
        )
        validate_citations(
            output.get("candidate_evidence"),
            candidate,
            "candidate evidence",
            True,
            errors,
        )
    elif decision == "abstain":
        if output.get("candidate_token") != "":
            errors.append("abstention must use an empty candidate token")
        candidate_evidence = output.get("candidate_evidence")
        if candidate_evidence != []:
            errors.append("abstention cannot cite candidate evidence")
        if not questions:
            errors.append("abstention must preserve an unresolved question")
        validate_citations(
            output.get("new_observation_evidence"),
            new_observation,
            "new observation evidence",
            False,
            errors,
        )
    return errors


def main() -> int:
    args = parse_args()
    package, _ = read_package(args.input)
    args.output_directory.mkdir(parents=True, exist_ok=True)
    for pair_text in args.pair:
        pair = parse_pair(pair_text)
        bundle_id, raw_observations = find_pair(package, pair)
        observations = [observation_source(item) for item in raw_observations]
        source = {
            "new_observation": observations[1],
            "candidate_events": [
                {
                    "candidate_token": "candidate-1",
                    "source_observations": [observations[0]],
                }
            ],
        }
        artifact: dict[str, Any] = {
            "run_id": uuid.uuid4().hex,
            "started_at_utc": datetime.now(timezone.utc).isoformat(),
            "input_path": str(args.input.resolve()),
            "bundle_id": bundle_id,
            "observation_ids": list(pair),
            "model_requested": args.model,
            "prompt_identity": "incident-event-link-only-v1",
            "source": source,
            "model_call": None,
            "link_proposal": None,
            "proposal_validation_errors": [],
            "application_transition": None,
        }
        model_call = completion_request(
            args.base_url,
            args.model,
            PROMPT,
            source,
            "incident_event_link_only_v1",
            LINK_SCHEMA,
            args.max_output_tokens,
            args.timeout_seconds,
        )
        artifact["model_call"] = model_call
        errors: list[str] = []
        output = parse_content(model_call, "link proposer", errors)
        if isinstance(output, dict):
            artifact["link_proposal"] = output
            errors.extend(validate_link_output(output, observations[0], observations[1]))
        artifact["proposal_validation_errors"] = errors
        linked = not errors and output and output.get("decision") == "propose_link"
        artifact["application_transition"] = {
            "operation": "link_to_candidate" if linked else "create_unresolved_singleton",
            "reason": (
                "valid source-cited positive link"
                if linked
                else "abstention or invalid output cannot establish event membership"
            ),
        }
        output_path = args.output_directory / (
            f"{pair[0].replace(':', '-')}--{pair[1].replace(':', '-')}-{artifact['run_id']}.json"
        )
        output_path.write_text(
            json.dumps(artifact, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8",
        )
        print(
            json.dumps(
                {
                    "observation_ids": list(pair),
                    "artifact": str(output_path),
                    "errors": len(errors),
                    "transition": artifact["application_transition"]["operation"],
                    "duration_milliseconds": model_call["duration_milliseconds"],
                }
            ),
            flush=True,
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
