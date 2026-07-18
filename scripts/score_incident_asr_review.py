#!/usr/bin/env python3
"""Reveal and score blind ASR reviews without treating uncertain audio as truth."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--review-package", type=Path, required=True)
    parser.add_argument("--answer-key", type=Path, required=True)
    parser.add_argument("--review", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.resolve().read_text(encoding="utf-8-sig"))


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.resolve().read_bytes()).hexdigest().upper()


def score_review(
    review: dict[str, Any],
    package: dict[str, Any],
    key_by_item: dict[str, dict[str, str]],
    sources: list[str],
) -> dict[str, Any]:
    package_items = {item["item_id"]: item for item in package["items"]}
    review_items = {item["item_id"]: item for item in review["items"]}
    if set(review_items) != set(package_items):
        raise ValueError(f"review '{review['reviewer_id']}' does not contain the package's exact item set")

    source_counts = {source: Counter() for source in sources}
    selection_counts = {
        "high_disagreement": Counter(),
        "low_disagreement": Counter(),
    }
    rating_counts: Counter[int] = Counter()
    all_candidates_wrong = 0
    details = []

    for item_id, package_item in package_items.items():
        item = review_items[item_id]
        if item.get("reviewer_id") != review["reviewer_id"]:
            raise ValueError(f"item '{item_id}' reviewer id does not match the review")
        rating = item.get("audio_intelligibility_0_to_3")
        if rating not in (0, 1, 2, 3):
            raise ValueError(f"item '{item_id}' has an invalid intelligibility rating")
        mapping = key_by_item[item_id]
        unknown_candidates = set(item.get("best_candidate_ids", [])) - set(mapping)
        if unknown_candidates:
            raise ValueError(f"item '{item_id}' selects unknown candidates: {sorted(unknown_candidates)}")
        accepted_sources = sorted(mapping[candidate] for candidate in item.get("best_candidate_ids", []))
        all_wrong = bool(item.get("all_candidates_materially_wrong"))
        if all_wrong and accepted_sources:
            raise ValueError(f"item '{item_id}' both accepts candidates and marks all candidates wrong")

        rating_counts[rating] += 1
        all_candidates_wrong += all_wrong
        for source in sources:
            counts = source_counts[source]
            counts["total"] += 1
            counts["accepted"] += source in accepted_sources
            if rating >= 2:
                counts["mostly_intelligible_or_clear_total"] += 1
                counts["mostly_intelligible_or_clear_accepted"] += source in accepted_sources
            if rating == 3:
                counts["clear_total"] += 1
                counts["clear_accepted"] += source in accepted_sources

        reason = package_item["selection_reason"]
        group = "high_disagreement" if reason.endswith("high-disagreement") else "low_disagreement"
        group_counts = selection_counts[group]
        group_counts["items"] += 1
        group_counts["all_candidates_wrong"] += all_wrong
        group_counts["mostly_intelligible_or_clear"] += rating >= 2
        for source in sources:
            group_counts[f"accepted:{source}"] += source in accepted_sources

        details.append({
            "item_id": item_id,
            "observation_id": package_item["observation_id"],
            "selection_reason": reason,
            "audio_intelligibility_0_to_3": rating,
            "accepted_sources": accepted_sources,
            "all_candidates_materially_wrong": all_wrong,
            "reviewer_transcript": item.get("reviewer_transcript", ""),
            "uncertain_fragments": item.get("uncertain_fragments", []),
            "notes": item.get("notes", ""),
        })

    return {
        "reviewer_id": review["reviewer_id"],
        "completed_at_utc": review["completed_at_utc"],
        "items": len(review_items),
        "intelligibility_rating_counts": {str(key): rating_counts[key] for key in sorted(rating_counts)},
        "all_candidates_materially_wrong": all_candidates_wrong,
        "source_acceptance": {source: dict(source_counts[source]) for source in sources},
        "selection_group_results": {group: dict(counts) for group, counts in selection_counts.items()},
        "details": details,
    }


def main() -> int:
    args = parse_args()
    package = read_json(args.review_package)
    answer_key = read_json(args.answer_key)
    if package["package_sha256"] != answer_key["package_sha256"]:
        raise ValueError("review package and answer key identify different corpus packages")

    key_by_item = {
        item["item_id"]: {
            mapping["candidate_id"]: mapping["source_id"]
            for mapping in item["candidate_sources"]
        }
        for item in answer_key["items"]
    }
    sources = sorted({source for mapping in key_by_item.values() for source in mapping.values()})
    scored_reviews = []
    review_hashes = {}
    for review_path in args.review:
        review = read_json(review_path)
        if review["review_package_sha256"] != package["package_sha256"]:
            raise ValueError(f"review package hash mismatch in {review_path}")
        if review["reviewer_id"] in review_hashes:
            raise ValueError(f"duplicate reviewer id: {review['reviewer_id']}")
        review_hashes[review["reviewer_id"]] = sha256_file(review_path)
        scored_reviews.append(score_review(review, package, key_by_item, sources))

    artifact = {
        "score_artifact_version": 1,
        "status": "provisional-single-reviewer" if len(scored_reviews) == 1 else "multi-reviewer-unreconciled",
        "review_package_sha256": package["package_sha256"],
        "answer_key_sha256": sha256_file(args.answer_key),
        "review_sha256": review_hashes,
        "review_count": len(scored_reviews),
        "reviews": scored_reviews,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(artifact, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({
        "status": artifact["status"],
        "review_count": artifact["review_count"],
        "output": str(args.output.resolve()),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
