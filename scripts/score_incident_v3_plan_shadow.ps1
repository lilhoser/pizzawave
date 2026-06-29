param(
    [string]$HostName = "lilhoser@192.168.1.173",
    [string]$Since = "2026-06-28 00:00:00",
    [string]$Until = "",
    [string]$ServiceName = "pizzad",
    [string]$DatabasePath = "/var/lib/pizzawave/pizzad.db",
    [string]$OutputDirectory = "artifacts/incident-v3-plan-shadow"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputFullPath = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null

$remoteScript = @'
import argparse
import base64
import collections
import datetime
import json
import re
import sqlite3
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--since-b64", required=True)
parser.add_argument("--until-b64", default="")
parser.add_argument("--service", default="pizzad")
parser.add_argument("--database", default="/var/lib/pizzawave/pizzad.db")
args = parser.parse_args()
args.since = base64.b64decode(args.since_b64).decode("utf-8")
args.until = base64.b64decode(args.until_b64).decode("utf-8") if args.until_b64 else ""

journal_args = ["journalctl", "-u", args.service, "--since", args.since, "--no-pager", "-o", "json"]
if args.until:
    journal_args.extend(["--until", args.until])

proc = subprocess.run(journal_args, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)

records = []
current = None
for line in proc.stdout.splitlines():
    try:
        item = json.loads(line)
    except Exception:
        continue

    message = item.get("MESSAGE", "")
    start = re.search(r"Incident v3 incident plan shadow for ([^:]+): .*?plans=\[$", message.strip())
    if start:
        if current:
            records.append(current)
        timestamp_us = int(item.get("__REALTIME_TIMESTAMP", "0") or 0)
        current = {
            "timestamp": timestamp_us / 1000000 if timestamp_us else 0,
            "system": start.group(1),
            "lines": ["["],
            "depth": 1,
        }
        continue

    if current:
        text = message.strip()
        current["lines"].append(text)
        current["depth"] += text.count("[") - text.count("]")
        if current["depth"] <= 0:
            records.append(current)
            current = None

if current:
    records.append(current)

plan_records = []
parse_failures = 0
for record in records:
    try:
        plans = json.loads("\n".join(record["lines"]))
    except Exception:
        parse_failures += 1
        continue
    when = datetime.datetime.fromtimestamp(record["timestamp"]).isoformat(sep=" ", timespec="seconds") if record["timestamp"] else ""
    plan_records.append({
        "time": when,
        "system": record["system"],
        "plans": plans,
    })

conn = sqlite3.connect(args.database)
conn.row_factory = sqlite3.Row
cur = conn.cursor()

def rows(sql, params=()):
    return [dict(row) for row in cur.execute(sql, params)]

def call_details(call_ids):
    if not call_ids:
        return []
    placeholders = ",".join("?" for _ in call_ids)
    return rows(
        f"""
        SELECT id, system_short_name, datetime(start_time,'unixepoch','localtime') local_time,
               category, talkgroup_name,
               substr(replace(transcription,char(10),' '),1,220) transcription
        FROM calls
        WHERE id IN ({placeholders})
        ORDER BY start_time, id
        """,
        call_ids,
    )

def current_links(call_ids):
    if not call_ids:
        return []
    placeholders = ",".join("?" for _ in call_ids)
    return rows(
        f"""
        SELECT c.id call_id, i.id incident_id, i.status, i.title, i.category
        FROM calls c
        LEFT JOIN incident_calls ic ON ic.call_id = c.id
        LEFT JOIN incidents i ON i.id = ic.incident_id
        WHERE c.id IN ({placeholders})
        ORDER BY c.id
        """,
        call_ids,
    )

def incident_call_ids(incident_id):
    return set(row["call_id"] for row in rows("SELECT call_id FROM incident_calls WHERE incident_id = ?", (incident_id,)))

audits = []
for audit in cur.execute(
    """
    SELECT id, timestamp_utc, system_short_name, operation, accepted, reason, score, call_ids_json
    FROM incident_operation_audit
    WHERE timestamp_utc >= ?
    ORDER BY id
    """,
    (args.since.replace(" ", "T"),),
):
    audit = dict(audit)
    try:
        audit["ids"] = set(int(value) for value in json.loads(audit.get("call_ids_json") or "[]"))
    except Exception:
        audit["ids"] = set()
    audits.append(audit)

action_counts = collections.Counter()
drop_reason_counts = collections.Counter()
blockable_write_counts = collections.Counter()
unique_creates = {}
unique_updates = {}

for record in plan_records:
    for plan in record["plans"]:
        action = (plan.get("action") or "").strip().lower()
        action_counts[action] += 1
        reason = plan.get("reason") or ""
        for drop in re.findall(r"(?:planDroppedBecause|droppedBecause)=([^;]+)", reason):
            drop_reason_counts[drop.strip()] += 1
        if action in {"update_current", "create_new", "detach_create"}:
            blockable_write_counts[action] += 1
        if action == "update_current":
            call_ids = tuple(sorted(int(value) for value in plan.get("callIds") or []))
            target_id = (plan.get("targetIncidentId") or "").strip()
            key = (record["system"], target_id, call_ids, plan.get("title", ""), plan.get("frameTitle", ""))
            row = unique_updates.setdefault(key, {
                "firstSeen": record["time"],
                "lastSeen": record["time"],
                "occurrences": 0,
                "system": record["system"],
                "targetIncidentId": target_id,
                "targetIncidentTitle": plan.get("targetIncidentTitle", ""),
                "title": plan.get("title", ""),
                "frameTitle": plan.get("frameTitle", ""),
                "category": plan.get("category", ""),
                "callIds": list(call_ids),
                "reasons": collections.Counter(),
            })
            row["occurrences"] += 1
            row["lastSeen"] = record["time"]
            row["reasons"][reason] += 1
        if action != "create_new":
            continue

        call_ids = tuple(sorted(int(value) for value in plan.get("callIds") or []))
        key = (record["system"], call_ids, plan.get("title", ""), plan.get("frameTitle", ""))
        row = unique_creates.setdefault(key, {
            "firstSeen": record["time"],
            "lastSeen": record["time"],
            "occurrences": 0,
            "system": record["system"],
            "title": plan.get("title", ""),
            "frameTitle": plan.get("frameTitle", ""),
            "category": plan.get("category", ""),
            "callIds": list(call_ids),
            "reasons": collections.Counter(),
        })
        row["occurrences"] += 1
        row["lastSeen"] = record["time"]
        row["reasons"][reason] += 1

for candidate in unique_creates.values():
    call_id_set = set(candidate["callIds"])
    links = current_links(candidate["callIds"])
    linked_incidents = collections.defaultdict(set)
    for link in links:
        if link.get("incident_id") is not None:
            linked_incidents[int(link["incident_id"])].add(int(link["call_id"]))

    accepted_audits = [audit for audit in audits if audit["accepted"] and audit["ids"] & call_id_set]
    rejected_audits = [audit for audit in audits if not audit["accepted"] and audit["ids"] & call_id_set]
    accepted_superset = [audit for audit in accepted_audits if call_id_set <= audit["ids"]]
    rejected_superset = [audit for audit in rejected_audits if call_id_set <= audit["ids"]]

    current_superset = []
    for incident_id in linked_incidents:
        if call_id_set <= incident_call_ids(incident_id):
            current_superset.append(incident_id)

    accepted_cover = set()
    for audit in accepted_audits:
        accepted_cover.update(audit["ids"] & call_id_set)

    if accepted_superset or current_superset:
        classification = "v1_accepts_candidate_or_superset"
    elif len(accepted_audits) >= 2 and accepted_cover >= call_id_set:
        classification = "v3_overmerged_distinct_v1_incidents"
    elif rejected_superset:
        classification = "v1_rejected_candidate"
    elif accepted_audits:
        classification = "partial_v1_overlap"
    else:
        classification = "no_v1_acceptance_seen"

    candidate["classification"] = classification
    candidate["currentIncidentIds"] = sorted(linked_incidents.keys())
    candidate["topReason"] = candidate["reasons"].most_common(1)[0][0] if candidate["reasons"] else ""
    candidate["acceptedAudits"] = [
        {
            "id": audit["id"],
            "timestampUtc": audit["timestamp_utc"],
            "reason": audit["reason"],
            "callIds": sorted(audit["ids"]),
        }
        for audit in accepted_audits[-5:]
    ]
    candidate["rejectedAudits"] = [
        {
            "id": audit["id"],
            "timestampUtc": audit["timestamp_utc"],
            "reason": audit["reason"],
            "callIds": sorted(audit["ids"]),
        }
        for audit in rejected_audits[-5:]
    ]
    candidate["callDetails"] = call_details(candidate["callIds"])
    del candidate["reasons"]

create_candidates = sorted(unique_creates.values(), key=lambda row: (row["firstSeen"], row["system"], row["title"]))
classification_counts = collections.Counter(row["classification"] for row in create_candidates)

def active_incident_id(target_id):
    match = re.match(r"^active:(\d+)$", target_id or "", re.IGNORECASE)
    return int(match.group(1)) if match else None

for update in unique_updates.values():
    target_active_id = active_incident_id(update["targetIncidentId"])
    call_id_set = set(update["callIds"])
    target_call_ids = incident_call_ids(target_active_id) if target_active_id is not None else set()
    overlapping_target_call_ids = sorted(call_id_set & target_call_ids)
    links = current_links(update["callIds"])
    linked_incidents = collections.defaultdict(set)
    for link in links:
        if link.get("incident_id") is not None:
            linked_incidents[int(link["incident_id"])].add(int(link["call_id"]))

    accepted_audits = [audit for audit in audits if audit["accepted"] and audit["ids"] & call_id_set]
    rejected_audits = [audit for audit in audits if not audit["accepted"] and audit["ids"] & call_id_set]

    if not update["targetIncidentId"]:
        classification = "missing_target_incident_id"
    elif target_active_id is None:
        classification = "non_active_target_incident_id"
    elif overlapping_target_call_ids:
        classification = "active_target_with_current_overlap"
    else:
        classification = "active_target_without_current_overlap"

    update["classification"] = classification
    update["targetActiveIncidentId"] = target_active_id
    update["targetCurrentCallIds"] = sorted(target_call_ids)
    update["targetOverlapCallIds"] = overlapping_target_call_ids
    update["currentIncidentIds"] = sorted(linked_incidents.keys())
    update["topReason"] = update["reasons"].most_common(1)[0][0] if update["reasons"] else ""
    update["acceptedAudits"] = [
        {
            "id": audit["id"],
            "timestampUtc": audit["timestamp_utc"],
            "reason": audit["reason"],
            "callIds": sorted(audit["ids"]),
        }
        for audit in accepted_audits[-5:]
    ]
    update["rejectedAudits"] = [
        {
            "id": audit["id"],
            "timestampUtc": audit["timestamp_utc"],
            "reason": audit["reason"],
            "callIds": sorted(audit["ids"]),
        }
        for audit in rejected_audits[-5:]
    ]
    update["callDetails"] = call_details(update["callIds"])
    del update["reasons"]

update_candidates = sorted(unique_updates.values(), key=lambda row: (row["firstSeen"], row["system"], row["targetIncidentId"], row["title"]))
update_classification_counts = collections.Counter(row["classification"] for row in update_candidates)

payload = {
    "generatedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
    "host": None,
    "service": args.service,
    "databasePath": args.database,
    "since": args.since,
    "until": args.until,
    "summary": {
        "rawPlanHeaders": len(records),
        "parsedPlanRecords": len(plan_records),
        "parseFailures": parse_failures,
        "planActionCounts": dict(sorted(action_counts.items())),
        "writeActionCounts": dict(sorted(blockable_write_counts.items())),
        "dropReasonCounts": dict(sorted(drop_reason_counts.items())),
        "uniqueCreateCandidates": len(create_candidates),
        "createClassifications": dict(sorted(classification_counts.items())),
        "uniqueUpdateCandidates": len(update_candidates),
        "updateClassifications": dict(sorted(update_classification_counts.items())),
    },
    "createCandidates": create_candidates,
    "updateCandidates": update_candidates,
}

print(json.dumps(payload, indent=2, sort_keys=True))
'@

$sinceB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Since))
$untilB64 = if ([string]::IsNullOrWhiteSpace($Until)) { "" } else { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Until)) }

$sshArgs = @($HostName, "python3", "-", "--since-b64", $sinceB64, "--service", $ServiceName, "--database", $DatabasePath)
if (![string]::IsNullOrWhiteSpace($Until)) {
    $sshArgs += @("--until-b64", $untilB64)
}

$json = $remoteScript | & ssh @sshArgs
$report = $json | ConvertFrom-Json
$report | Add-Member -NotePropertyName host -NotePropertyValue $HostName -Force

$generated = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$outFile = Join-Path $outputFullPath "incident-v3-plan-shadow-score-$generated.json"
$report | ConvertTo-Json -Depth 100 | Set-Content -Path $outFile -Encoding UTF8
Write-Output $outFile
