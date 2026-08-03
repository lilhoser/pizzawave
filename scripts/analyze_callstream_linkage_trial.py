#!/usr/bin/env python3
"""Read-only analysis for a fixed Callstream v2 transmission-linkage window."""

from __future__ import annotations

import argparse
import collections
import datetime as dt
import json
import math
import os
import re
import sqlite3
import subprocess
import wave


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] * (upper - position) + ordered[upper] * (position - lower)


def text_preview(value: str | None, limit: int = 700) -> str:
    text = " ".join((value or "").split())
    return text if len(text) <= limit else text[: limit - 1] + "…"


def call_time(unix_seconds: int) -> str:
    return dt.datetime.fromtimestamp(unix_seconds, dt.timezone.utc).isoformat()


def read_audio_stats(path: str, start_sample: int, sample_count: int) -> dict:
    with wave.open(path, "rb") as wav:
        wav.setpos(start_sample)
        raw = wav.readframes(sample_count)
    samples = [
        int.from_bytes(raw[offset : offset + 2], "little", signed=True)
        for offset in range(0, len(raw), 2)
    ]
    peak = max((abs(value) for value in samples), default=0)
    rms = math.sqrt(sum(value * value for value in samples) / len(samples)) if samples else 0.0
    return {"peak": peak, "rms": round(rms, 3), "samplesRead": len(samples)}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", required=True)
    parser.add_argument("--audio-root", required=True)
    parser.add_argument("--start", type=int, required=True)
    parser.add_argument("--end", type=int, required=True)
    parser.add_argument("--include-journal", action="store_true")
    args = parser.parse_args()

    connection = sqlite3.connect(f"file:{args.database}?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    calls = connection.execute(
        """
        SELECT id, start_time, stop_time, system_short_name, talkgroup,
               talkgroup_name, transcription, transcription_status,
               quality_reason, audio_path, raw_metadata_json
        FROM calls
        WHERE start_time BETWEEN ? AND ?
        ORDER BY start_time, id
        """,
        (args.start, args.end),
    ).fetchall()
    call_by_id = {row["id"]: row for row in calls}
    call_ids = list(call_by_id)
    incidents_by_call: dict[int, set[int]] = collections.defaultdict(set)
    if call_ids:
        for offset in range(0, len(call_ids), 400):
            batch = call_ids[offset : offset + 400]
            placeholders = ",".join("?" for _ in batch)
            for row in connection.execute(
                f"""
                SELECT call_id, incident_id
                FROM incident_calls
                WHERE call_id IN ({placeholders})
                """,
                batch,
            ):
                incidents_by_call[row["call_id"]].add(row["incident_id"])

    transmissions: list[sqlite3.Row] = []
    if call_ids:
        for offset in range(0, len(call_ids), 400):
            batch = call_ids[offset : offset + 400]
            placeholders = ",".join("?" for _ in batch)
            transmissions.extend(
                connection.execute(
                    f"""
                    SELECT call_id, sequence, source_id, start_time_ms, stop_time_ms,
                           start_sample, sample_count, talkgroup, error_count,
                           spike_count, audio_mapping_status
                    FROM call_transmissions
                    WHERE call_id IN ({placeholders})
                    ORDER BY call_id, sequence
                    """,
                    batch,
                ).fetchall()
            )

    transmissions_by_call: dict[int, list[sqlite3.Row]] = collections.defaultdict(list)
    for row in transmissions:
        transmissions_by_call[row["call_id"]].append(row)

    mapping_counts: collections.Counter[str] = collections.Counter()
    schema_counts: collections.Counter[int] = collections.Counter()
    coverage_failures: list[dict] = []
    wav_failures: list[dict] = []
    retained_unknown: list[dict] = []
    identified_decoder_floor: list[dict] = []
    identified_low_level: list[dict] = []
    source_call_has_audio: dict[tuple[int, int], bool] = {}
    for call in calls:
        try:
            metadata = json.loads(call["raw_metadata_json"] or "{}")
        except json.JSONDecodeError:
            metadata = {}
        schema = int(metadata.get("SchemaVersion") or 1)
        mapping = str(metadata.get("AudioMappingStatus") or "version_1")
        schema_counts[schema] += 1
        mapping_counts[mapping] += 1
        rows = transmissions_by_call.get(call["id"], [])
        if schema < 2:
            continue

        if mapping.startswith("exact"):
            expected_start = 0
            for row in rows:
                if row["start_sample"] != expected_start or row["sample_count"] <= 0:
                    coverage_failures.append(
                        {
                            "callId": call["id"],
                            "sequence": row["sequence"],
                            "expectedStart": expected_start,
                            "actualStart": row["start_sample"],
                            "sampleCount": row["sample_count"],
                        }
                    )
                expected_start += row["sample_count"]
            path = os.path.join(args.audio_root, call["audio_path"] or "")
            try:
                with wave.open(path, "rb") as wav:
                    frames = wav.getnframes()
                    rate = wav.getframerate()
                if frames != expected_start or rate != 8000:
                    wav_failures.append(
                        {
                            "callId": call["id"],
                            "declaredSamples": expected_start,
                            "wavFrames": frames,
                            "sampleRate": rate,
                        }
                    )
            except (OSError, wave.Error) as error:
                wav_failures.append({"callId": call["id"], "error": str(error)})

        for row in rows:
            details = {
                "callId": call["id"],
                "sequence": row["sequence"],
                "sampleCount": row["sample_count"],
            }
            if mapping.startswith("exact") and row["start_sample"] is not None:
                path = os.path.join(args.audio_root, call["audio_path"] or "")
                try:
                    details.update(read_audio_stats(path, row["start_sample"], row["sample_count"]))
                except (OSError, wave.Error) as error:
                    details["error"] = str(error)
            if row["source_id"] is None:
                retained_unknown.append(details)
                continue

            details["sourceId"] = row["source_id"]
            participant_key = (call["id"], row["source_id"])
            if "error" in details or "peak" not in details:
                source_call_has_audio[participant_key] = True
                continue
            has_audio = details["peak"] > 32
            source_call_has_audio[participant_key] = (
                source_call_has_audio.get(participant_key, False) or has_audio
            )
            if details["peak"] <= 32:
                identified_decoder_floor.append(details)
            if details["peak"] <= 64:
                identified_low_level.append(details)

    participant_rows = connection.execute(
        """
        SELECT t.call_id, c.system_short_name, t.source_id,
               MIN(t.start_time_ms) AS first_ms,
               MAX(t.stop_time_ms) AS last_ms,
               COUNT(*) AS transmission_count
        FROM call_transmissions t
        JOIN calls c ON c.id=t.call_id
        WHERE c.start_time BETWEEN ? AND ? AND t.source_id IS NOT NULL
        GROUP BY t.call_id, c.system_short_name, t.source_id
        ORDER BY first_ms, t.call_id
        """,
        (args.start, args.end),
    ).fetchall()

    calls_per_source: dict[tuple[str, int], list[sqlite3.Row]] = collections.defaultdict(list)
    for row in participant_rows:
        key = ((row["system_short_name"] or "").strip().lower(), row["source_id"])
        calls_per_source[key].append(row)

    pair_links: dict[tuple[int, int], dict] = {}
    source_profiles: list[dict] = []
    for (system, source_id), rows in calls_per_source.items():
        ordered = sorted(rows, key=lambda row: (row["first_ms"], row["call_id"]))
        talkgroups = {call_by_id[row["call_id"]]["talkgroup"] for row in ordered}
        source_profiles.append(
            {
                "system": system,
                "sourceId": source_id,
                "callCount": len(ordered),
                "talkgroupCount": len(talkgroups),
                "transmissionCount": sum(row["transmission_count"] for row in ordered),
            }
        )
        for earlier, later in zip(ordered, ordered[1:]):
            gap_ms = max(0, later["first_ms"] - earlier["last_ms"])
            if gap_ms > 60 * 60 * 1000:
                continue
            pair_key = (earlier["call_id"], later["call_id"])
            link = pair_links.setdefault(
                pair_key,
                {
                    "earlierCallId": earlier["call_id"],
                    "laterCallId": later["call_id"],
                    "gapMs": gap_ms,
                    "sourceIds": [],
                    "sourceCallCounts": [],
                },
            )
            link["gapMs"] = min(link["gapMs"], gap_ms)
            link["sourceIds"].append(source_id)
            link["sourceCallCounts"].append(len(ordered))

    thresholds_seconds = [30, 60, 120, 300, 600, 900, 1800, 3600]
    sensitivity: list[dict] = []
    for seconds in thresholds_seconds:
        eligible_links = [link for link in pair_links.values() if link["gapMs"] <= seconds * 1000]
        robust_links = [
            link
            for link in eligible_links
            if any(
                source_call_has_audio.get((link["earlierCallId"], source_id), True)
                and source_call_has_audio.get((link["laterCallId"], source_id), True)
                for source_id in link["sourceIds"]
            )
        ]
        baseline_pairs = 0
        baseline_counts: list[int] = []
        linked_counts: collections.Counter[int] = collections.Counter()
        for link in eligible_links:
            linked_counts[link["laterCallId"]] += 1
        for later_index, later in enumerate(calls):
            count = 0
            for earlier in calls[:later_index]:
                if (earlier["system_short_name"] or "").strip().lower() != (
                    later["system_short_name"] or ""
                ).strip().lower():
                    continue
                gap = max(
                    0,
                    later["start_time"] - max(earlier["start_time"], earlier["stop_time"]),
                )
                if gap <= seconds:
                    count += 1
            baseline_pairs += count
            baseline_counts.append(count)
        same_talkgroup = sum(
            call_by_id[link["earlierCallId"]]["talkgroup"]
            == call_by_id[link["laterCallId"]]["talkgroup"]
            for link in eligible_links
        )
        production_same_incident = 0
        production_different_incident = 0
        production_unassigned = 0
        for link in eligible_links:
            earlier_incidents = incidents_by_call.get(link["earlierCallId"], set())
            later_incidents = incidents_by_call.get(link["laterCallId"], set())
            if not earlier_incidents or not later_incidents:
                production_unassigned += 1
            elif earlier_incidents & later_incidents:
                production_same_incident += 1
            else:
                production_different_incident += 1
        sensitivity.append(
            {
                "maximumGapSeconds": seconds,
                "linkPairs": len(eligible_links),
                "acousticallySupportedLinkPairs": len(robust_links),
                "decoderFloorOnlyLinkPairs": len(eligible_links) - len(robust_links),
                "linkedCalls": len({item for link in eligible_links for item in (link["earlierCallId"], link["laterCallId"])}),
                "sameTalkgroupPairs": same_talkgroup,
                "crossTalkgroupPairs": len(eligible_links) - same_talkgroup,
                "productionSameIncidentPairs": production_same_incident,
                "productionDifferentIncidentPairs": production_different_incident,
                "productionUnassignedPairs": production_unassigned,
                "baselineSameSystemPairs": baseline_pairs,
                "pairReductionPercent": round(
                    100.0 * (1 - len(eligible_links) / baseline_pairs), 3
                )
                if baseline_pairs
                else 0.0,
                "averageBaselineCandidatesPerCall": round(
                    sum(baseline_counts) / len(baseline_counts), 3
                )
                if baseline_counts
                else 0.0,
                "averageLinkedCandidatesAmongLinkedLaterCalls": round(
                    sum(linked_counts.values()) / len(linked_counts), 3
                )
                if linked_counts
                else 0.0,
            }
        )

    def pair_record(link: dict) -> dict:
        earlier = call_by_id[link["earlierCallId"]]
        later = call_by_id[link["laterCallId"]]
        earlier_incidents = sorted(incidents_by_call.get(earlier["id"], set()))
        later_incidents = sorted(incidents_by_call.get(later["id"], set()))
        acoustically_supported_source_ids = [
            source_id
            for source_id in link["sourceIds"]
            if source_call_has_audio.get((earlier["id"], source_id), True)
            and source_call_has_audio.get((later["id"], source_id), True)
        ]
        return {
            **link,
            "sharedRadioCount": len(link["sourceIds"]),
            "acousticallySupportedSourceIds": acoustically_supported_source_ids,
            "decoderFloorOnlyEvidence": not acoustically_supported_source_ids,
            "mostFrequentSharedRadioCallCount": max(link["sourceCallCounts"]),
            "sameTalkgroup": earlier["talkgroup"] == later["talkgroup"],
            "productionIncidentRelation": (
                "unassigned"
                if not earlier_incidents or not later_incidents
                else "same"
                if set(earlier_incidents) & set(later_incidents)
                else "different"
            ),
            "earlierProductionIncidentIds": earlier_incidents,
            "laterProductionIncidentIds": later_incidents,
            "earlier": {
                "id": earlier["id"],
                "timeUtc": call_time(earlier["start_time"]),
                "talkgroup": earlier["talkgroup"],
                "talkgroupName": earlier["talkgroup_name"],
                "qualityReason": earlier["quality_reason"],
                "transcription": text_preview(earlier["transcription"]),
            },
            "later": {
                "id": later["id"],
                "timeUtc": call_time(later["start_time"]),
                "talkgroup": later["talkgroup"],
                "talkgroupName": later["talkgroup_name"],
                "qualityReason": later["quality_reason"],
                "transcription": text_preview(later["transcription"]),
            },
        }

    ordered_links = sorted(
        pair_links.values(),
        key=lambda link: (
            -len(link["sourceIds"]),
            link["gapMs"],
            max(link["sourceCallCounts"]),
        ),
    )
    shortest_links = sorted(pair_links.values(), key=lambda link: link["gapMs"])
    common_radio_links = sorted(
        pair_links.values(),
        key=lambda link: (-max(link["sourceCallCounts"]), link["gapMs"]),
    )
    cross_talkgroup_links = [
        link
        for link in shortest_links
        if call_by_id[link["earlierCallId"]]["talkgroup"]
        != call_by_id[link["laterCallId"]]["talkgroup"]
    ]

    excluded_fragments: list[dict] = []
    omitted_calls: list[int] = []
    if args.include_journal:
        process = subprocess.run(
            [
                "journalctl",
                "--utc",
                "-u",
                "trunk-recorder",
                "--since",
                f"@{args.start}",
                "--until",
                f"@{args.end}",
                "--no-pager",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        for line in process.stdout.splitlines():
            fragment = re.search(
                r"omitting source-less acoustically empty transmission from call (\d+) \(samples=(\d+)\)",
                line,
            )
            if fragment:
                excluded_fragments.append(
                    {"callstreamCallId": int(fragment.group(1)), "sampleCount": int(fragment.group(2))}
                )
            empty_call = re.search(r"omitting call (\d+) because it contains no identified", line)
            if empty_call:
                omitted_calls.append(int(empty_call.group(1)))

    identified_calls = {row["call_id"] for row in participant_rows}
    identified_transmissions = sum(row["transmission_count"] for row in participant_rows)
    report = {
        "window": {
            "startUnix": args.start,
            "endUnix": args.end,
            "startUtc": call_time(args.start),
            "endUtc": call_time(args.end),
        },
        "transport": {
            "calls": len(calls),
            "schemaCounts": dict(schema_counts),
            "mappingCounts": dict(mapping_counts),
            "coverageFailures": coverage_failures,
            "wavFailures": wav_failures,
            "retainedUnknownTransmissions": retained_unknown,
            "identifiedDecoderFloorTransmissions": identified_decoder_floor,
            "identifiedLowLevelTransmissions": identified_low_level,
            "excludedEmptyFragments": excluded_fragments,
            "excludedEmptyFragmentSamples": sum(row["sampleCount"] for row in excluded_fragments),
            "omittedEntireEmptyCalls": omitted_calls,
        },
        "sourceAvailability": {
            "callsWithIdentifiedRadio": len(identified_calls),
            "callsWithoutIdentifiedRadio": len(calls) - len(identified_calls),
            "callAvailabilityPercent": round(100.0 * len(identified_calls) / len(calls), 3)
            if calls
            else 0.0,
            "identifiedTransmissions": identified_transmissions,
            "retainedUnknownTransmissionCount": len(retained_unknown),
            "uniqueSourceCount": len(calls_per_source),
            "sourcesSeenInMultipleCalls": sum(
                1 for rows in calls_per_source.values() if len(rows) > 1
            ),
        },
        "linkage": {
            "sensitivity": sensitivity,
            "sourceCallCountPercentiles": {
                "median": percentile([row["callCount"] for row in source_profiles], 0.5),
                "p90": percentile([row["callCount"] for row in source_profiles], 0.9),
                "p95": percentile([row["callCount"] for row in source_profiles], 0.95),
                "maximum": max((row["callCount"] for row in source_profiles), default=0),
            },
            "mostCommonSources": sorted(
                source_profiles, key=lambda row: (-row["callCount"], -row["talkgroupCount"])
            )[:20],
            "multiRadioPairs": [pair_record(link) for link in ordered_links if len(link["sourceIds"]) > 1][
                :20
            ],
            "shortestPairs": [pair_record(link) for link in shortest_links[:30]],
            "commonRadioRiskPairs": [pair_record(link) for link in common_radio_links[:30]],
            "crossTalkgroupPairs": [pair_record(link) for link in cross_talkgroup_links[:30]],
        },
    }
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
