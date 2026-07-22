#!/usr/bin/env python3
"""Score one-sided incident links from existing typed-evidence artifacts."""

from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--review-package", action="append", required=True, type=Path)
    parser.add_argument("--review", action="append", required=True, type=Path)
    parser.add_argument("--artifact-directory", action="append", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    return parser.parse_args()


def read_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} does not contain a JSON object")
    return value


def read_package(path: Path) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8-sig").strip()
    if path.suffix.lower() == ".js":
        prefix = "window.INCIDENT_RELATIONSHIP_REVIEW_PACKAGE="
        if not text.startswith(prefix) or not text.endswith(";"):
            raise ValueError(f"{path} is not a supported review-package script")
        text = text[len(prefix) : -1]
    value = json.loads(text)
    if not isinstance(value, dict):
        raise ValueError(f"{path} does not contain a review package")
    return value


def pair_key(observation_ids: list[str]) -> tuple[str, str]:
    if len(observation_ids) != 2 or not all(isinstance(value, str) and value for value in observation_ids):
        raise ValueError(f"invalid observation pair: {observation_ids}")
    return tuple(sorted(observation_ids))  # type: ignore[return-value]


def load_labels(
    package_paths: list[Path],
    review_paths: list[Path],
) -> dict[tuple[str, str], dict[str, str]]:
    if len(package_paths) != len(review_paths):
        raise ValueError("--review-package and --review must be supplied in matching pairs")

    labels: dict[tuple[str, str], dict[str, str]] = {}
    for package_path, review_path in zip(package_paths, review_paths, strict=True):
        package = read_package(package_path)
        review = read_json(review_path)
        package_hash = package.get("content_sha256")
        if package_hash and review.get("package_sha256") != package_hash:
            raise ValueError(f"{review_path} does not match {package_path}")
        review_by_case = {
            item["case_id"]: item
            for item in review.get("cases", [])
            if isinstance(item, dict) and item.get("case_id")
        }
        for case in package.get("cases", []):
            if not isinstance(case, dict):
                continue
            case_id = case.get("case_id")
            reviewed = review_by_case.get(case_id)
            if reviewed is None:
                raise ValueError(f"review is missing case '{case_id}' from {package_path}")
            observations = case.get("observations", [])
            key = pair_key([item.get("observation_id", "") for item in observations])
            assessment = reviewed.get("relationship_assessment", "")
            if assessment not in {"same_event", "not_same_event", "unresolved"}:
                raise ValueError(f"case '{case_id}' has invalid assessment '{assessment}'")
            if key in labels:
                raise ValueError(f"observation pair {key} occurs in more than one review package")
            labels[key] = {
                "case_id": case_id,
                "assessment": assessment,
            }
    return labels


def load_artifacts(directories: list[Path]) -> dict[tuple[str, str], dict[str, Any]]:
    artifacts: dict[tuple[str, str], dict[str, Any]] = {}
    for directory in directories:
        for path in sorted(directory.glob("*.json")):
            artifact = read_json(path)
            key = pair_key(artifact.get("observation_ids", []))
            if key in artifacts:
                raise ValueError(f"duplicate artifact for observation pair {key}")
            artifact["_path"] = str(path.resolve())
            artifacts[key] = artifact
    return artifacts


def main() -> int:
    args = parse_args()
    labels = load_labels(args.review_package, args.review)
    artifacts = load_artifacts(args.artifact_directory)
    missing = sorted(set(labels) - set(artifacts))
    extra = sorted(set(artifacts) - set(labels))
    if missing or extra:
        raise ValueError(f"artifact/review mismatch: missing={missing}; extra={extra}")

    rows: list[dict[str, Any]] = []
    for key, label in labels.items():
        artifact = artifacts[key]
        evidence = artifact.get("evidence_record") or {}
        link_proposal = artifact.get("link_proposal") or {}
        validation_errors = (
            artifact.get("proposal_validation_errors")
            if "proposal_validation_errors" in artifact
            else artifact.get("evidence_validation_errors")
        ) or []
        proposed_link = not validation_errors and (
            link_proposal.get("decision") == "propose_link"
            or evidence.get("verdict") == "supports_shared_event"
        )
        assessment = label["assessment"]
        rows.append(
            {
                "case_id": label["case_id"],
                "observation_ids": list(key),
                "assessment": assessment,
                "link_only_outcome": "linked" if proposed_link else "unresolved_singleton",
                "correct": proposed_link == (assessment == "same_event"),
                "contract_valid": not validation_errors,
                "original_verdict": link_proposal.get("decision") or evidence.get("verdict", ""),
                "artifact": artifact["_path"],
            }
        )

    same_count = sum(row["assessment"] == "same_event" for row in rows)
    link_rows = [row for row in rows if row["link_only_outcome"] == "linked"]
    correct_links = sum(row["assessment"] == "same_event" for row in link_rows)
    false_links = len(link_rows) - correct_links
    report = {
        "report_version": "incident-event-link-only-shadow-score-v1",
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "source_policy": {
            "positive_link": "contract-valid supports_shared_event",
            "all_other_output": "unresolved_singleton",
            "distinct_event_authority": False,
        },
        "metrics": {
            "reviewed_pairs": len(rows),
            "reviewed_same_event": same_count,
            "admitted_links": len(link_rows),
            "correct_links": correct_links,
            "false_links": false_links,
            "positive_link_precision": correct_links / len(link_rows) if link_rows else None,
            "same_event_recall": correct_links / same_count if same_count else None,
            "unresolved_singletons": len(rows) - len(link_rows),
            "contract_valid_records": sum(row["contract_valid"] for row in rows),
        },
        "rows": sorted(rows, key=lambda row: row["observation_ids"]),
    }
    rendered = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
