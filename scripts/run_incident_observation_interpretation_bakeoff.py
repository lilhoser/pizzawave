#!/usr/bin/env python3
"""Run a development-only observation interpretation and critique bakeoff."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROVENANCE_SCHEMA = {
    "type": "object",
    "properties": {
        "observation_id": {"type": "string"},
        "transcript_id": {"type": "string"},
        "exact_quote": {"type": "string"},
    },
    "required": ["observation_id", "transcript_id", "exact_quote"],
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
            "minItems": 1,
            "items": PROVENANCE_SCHEMA,
        },
    },
    "required": ["statement_id", "statement", "uncertainty", "provenance"],
    "additionalProperties": False,
}

INTERPRETATION_SCHEMA = {
    "type": "object",
    "properties": {
        "interpretation_id": {"type": "string"},
        "observation_id": {"type": "string"},
        "possible_readings": {"type": "array", "items": STATEMENT_SCHEMA},
        "shared_content": {"type": "array", "items": STATEMENT_SCHEMA},
        "unresolved_questions": {"type": "array", "items": {"type": "string"}},
    },
    "required": [
        "interpretation_id",
        "observation_id",
        "possible_readings",
        "shared_content",
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
            "minItems": 1,
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
        "interpretation_id": {"type": "string"},
        "summary": {"type": "string"},
        "findings": {"type": "array", "items": FINDING_SCHEMA},
    },
    "required": ["critique_id", "interpretation_id", "summary", "findings"],
    "additionalProperties": False,
}

INTERPRETER_PROMPT = """You interpret one radio-call observation containing competing transcript candidates.
Your task is limited to what this single observation may say. Do not decide whether an incident or event exists. Do not assign an event category, call role, incident membership, severity, or operational meaning.

Preserve ambiguity between transcript candidates. Put content supported by every non-empty candidate in shared_content. Put materially different plausible readings in possible_readings. An empty or unintelligible observation may have no statements. State unresolved questions without guessing answers.

Every statement must cite exact, case-sensitive transcript text copied verbatim from this observation. Do not invent quotations, facts, names, spelling, actions, or identifiers. Uncertainty is from 0 to 1, where 1 is highly uncertain."""

CRITIC_PROMPT = """You independently critique a proposed interpretation of one radio-call observation.
Check whether it preserves materially different transcript readings, overstates shared content, invents meaning, or converts text into an event or incident conclusion. Do not create an event hypothesis, category, call role, incident membership, severity, or operational label.

Each finding must cite exact, case-sensitive transcript text copied verbatim from the supplied observation. Return an empty findings array if there is no source-grounded criticism. Uncertainty is from 0 to 1, where 1 is highly uncertain."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--observation-id", action="append", required=True)
    parser.add_argument("--base-url", default="http://127.0.0.1:1234/v1")
    parser.add_argument("--model", default="incident-qwen36-observation")
    parser.add_argument("--max-output-tokens", type=int, default=4096)
    parser.add_argument("--timeout-seconds", type=int, default=600)
    return parser.parse_args()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


def read_package(path: Path) -> tuple[dict[str, Any], bytes]:
    resolved = path.resolve()
    if "heldout-sealed" in str(resolved).lower():
        raise ValueError("the observation bakeoff refuses sealed held-out corpus paths")
    raw = resolved.read_bytes()
    return json.loads(raw.decode("utf-8-sig")), raw


def source_document(observation: dict[str, Any]) -> dict[str, Any]:
    return {
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
    }


def completion_request(
    base_url: str,
    model: str,
    prompt: str,
    document: dict[str, Any],
    schema_name: str,
    schema: dict[str, Any],
    max_tokens: int,
    timeout_seconds: int,
) -> dict[str, Any]:
    request_document = {
        "model": model,
        "messages": [
            {"role": "system", "content": prompt},
            {"role": "user", "content": json.dumps(document, separators=(",", ":"))},
        ],
        "temperature": 0,
        "max_tokens": max_tokens,
        "response_format": {
            "type": "json_schema",
            "json_schema": {"name": schema_name, "strict": True, "schema": schema},
        },
    }
    request_bytes = json.dumps(request_document, separators=(",", ":")).encode("utf-8")
    try:
        with urllib.request.urlopen(f"{base_url.rstrip('/')}/models", timeout=10) as response:
            advertised_models = json.loads(response.read()).get("data", [])
        advertised_model_ids = {item.get("id") for item in advertised_models}
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        return {
            "duration_milliseconds": 0,
            "request_sha256": sha256_bytes(request_bytes),
            "request_error": f"model preflight failed: {exc}",
            "response": None,
            "response_content": "",
        }
    if model not in advertised_model_ids:
        return {
            "duration_milliseconds": 0,
            "request_sha256": sha256_bytes(request_bytes),
            "request_error": f"requested model is not advertised by the endpoint: {model}",
            "response": None,
            "response_content": "",
        }
    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/chat/completions",
        data=request_bytes,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            response_bytes = response.read()
        response_document = json.loads(response_bytes)
        response_model = response_document.get("model")
        error = "" if response_model == model else (
            f"response model '{response_model}' does not match requested model '{model}'"
        )
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        response_document = None
        error = str(exc)
    elapsed_ms = round((time.perf_counter() - started) * 1000)
    content = ""
    if response_document:
        content = str(response_document["choices"][0]["message"].get("content") or "")
    return {
        "duration_milliseconds": elapsed_ms,
        "request_sha256": sha256_bytes(request_bytes),
        "request_error": error,
        "response": response_document,
        "response_content": content,
    }


def valid_uncertainty(value: Any) -> bool:
    return isinstance(value, (int, float)) and math.isfinite(value) and 0 <= value <= 1


def validate_provenance(
    provenance: Any,
    observation: dict[str, Any],
    owner: str,
    errors: list[str],
) -> None:
    if not isinstance(provenance, list) or not provenance:
        errors.append(f"{owner} must include source provenance")
        return
    transcripts = {
        transcript["transcriptId"]: transcript.get("text", "")
        for transcript in observation.get("transcripts", [])
    }
    for item in provenance:
        if item.get("observation_id") != observation["observationId"]:
            errors.append(f"{owner} cites an observation outside the interpreted observation")
            continue
        transcript_id = item.get("transcript_id")
        quote = item.get("exact_quote")
        if transcript_id not in transcripts:
            errors.append(f"{owner} cites unknown transcript '{transcript_id}'")
        elif not isinstance(quote, str) or not quote or quote not in transcripts[transcript_id]:
            errors.append(f"{owner} quote does not occur exactly in transcript '{transcript_id}'")


def parse_content(call: dict[str, Any], owner: str, errors: list[str]) -> Any:
    if call["request_error"]:
        errors.append(f"{owner} request failed: {call['request_error']}")
        return None
    content = call["response_content"]
    if not content:
        errors.append(f"{owner} response content is empty")
        return None
    try:
        return json.loads(content)
    except json.JSONDecodeError as exc:
        errors.append(f"{owner} response content is not JSON: {exc}")
        return None


def validate_interpretation(
    interpretation: Any,
    observation: dict[str, Any],
    errors: list[str],
) -> None:
    if not isinstance(interpretation, dict):
        return
    if interpretation.get("observation_id") != observation["observationId"]:
        errors.append("interpretation observation id does not match the requested observation")
    ids: set[str] = set()
    for collection_name in ("possible_readings", "shared_content"):
        for statement in interpretation.get(collection_name, []):
            statement_id = statement.get("statement_id")
            if not statement_id or statement_id in ids:
                errors.append(f"duplicate or missing interpretation statement id '{statement_id}'")
            ids.add(statement_id)
            if not statement.get("statement"):
                errors.append(f"interpretation statement '{statement_id}' has no text")
            if not valid_uncertainty(statement.get("uncertainty")):
                errors.append(f"interpretation statement '{statement_id}' has invalid uncertainty")
            validate_provenance(
                statement.get("provenance"), observation, f"statement '{statement_id}'", errors
            )


def validate_critique(
    critique: Any,
    interpretation: dict[str, Any],
    observation: dict[str, Any],
    errors: list[str],
) -> None:
    if not isinstance(critique, dict):
        return
    if critique.get("interpretation_id") != interpretation.get("interpretation_id"):
        errors.append("critique interpretation id does not match the interpretation")
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
            finding.get("provenance"), observation, f"critique finding '{finding_id}'", errors
        )


def main() -> int:
    args = parse_args()
    package, package_bytes = read_package(args.input)
    observations = {
        observation["observationId"]: observation
        for bundle in package["bundles"]
        for observation in bundle["observations"]
    }
    missing = sorted(set(args.observation_id) - observations.keys())
    if missing:
        raise ValueError(f"unknown observation id(s): {', '.join(missing)}")

    output_directory = args.output_directory.resolve()
    output_directory.mkdir(parents=True, exist_ok=True)
    for observation_id in args.observation_id:
        observation = observations[observation_id]
        source = source_document(observation)
        run_id = uuid.uuid4().hex
        artifact_path = output_directory / f"{observation_id.replace(':', '-')}-{run_id}.json"
        artifact: dict[str, Any] = {
            "run_id": run_id,
            "started_at_utc": datetime.now(timezone.utc).isoformat(),
            "input_path": str(args.input.resolve()),
            "input_sha256": sha256_bytes(package_bytes),
            "observation_id": observation_id,
            "model_requested": args.model,
            "interpreter_prompt_identity": "incident-observation-interpreter-v1",
            "interpreter_prompt_sha256": sha256_bytes(INTERPRETER_PROMPT.encode()),
            "critic_prompt_identity": "incident-observation-interpretation-critic-v1",
            "critic_prompt_sha256": sha256_bytes(CRITIC_PROMPT.encode()),
            "source": source,
            "interpreter_call": None,
            "interpretation": None,
            "interpretation_validation_errors": ["validation did not complete"],
            "critic_call": None,
            "critique": None,
            "critique_validation_errors": ["critic did not run"],
        }
        artifact_path.write_text(json.dumps(artifact, indent=2), encoding="utf-8")

        interpreter_call = completion_request(
            args.base_url,
            args.model,
            INTERPRETER_PROMPT,
            source,
            "incident_observation_interpretation",
            INTERPRETATION_SCHEMA,
            args.max_output_tokens,
            args.timeout_seconds,
        )
        interpretation_errors: list[str] = []
        interpretation = parse_content(interpreter_call, "interpreter", interpretation_errors)
        validate_interpretation(interpretation, observation, interpretation_errors)
        artifact.update(
            interpreter_call=interpreter_call,
            interpretation=interpretation,
            interpretation_validation_errors=interpretation_errors,
        )
        artifact_path.write_text(json.dumps(artifact, indent=2), encoding="utf-8")

        if not interpretation_errors and interpretation is not None:
            critic_document = {"observation": source, "proposed_interpretation": interpretation}
            critic_call = completion_request(
                args.base_url,
                args.model,
                CRITIC_PROMPT,
                critic_document,
                "incident_observation_interpretation_critique",
                CRITIQUE_SCHEMA,
                args.max_output_tokens,
                args.timeout_seconds,
            )
            critique_errors: list[str] = []
            critique = parse_content(critic_call, "critic", critique_errors)
            validate_critique(critique, interpretation, observation, critique_errors)
            artifact.update(
                critic_call=critic_call,
                critique=critique,
                critique_validation_errors=critique_errors,
            )
            artifact_path.write_text(json.dumps(artifact, indent=2), encoding="utf-8")

        print(
            json.dumps(
                {
                    "observation_id": observation_id,
                    "artifact": str(artifact_path),
                    "interpretation_errors": len(artifact["interpretation_validation_errors"]),
                    "critique_errors": len(artifact["critique_validation_errors"]),
                }
            )
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
