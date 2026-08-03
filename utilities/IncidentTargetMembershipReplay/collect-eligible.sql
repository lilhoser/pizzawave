-- Read-only collector for exact-existing-incident membership candidates.
-- Required sqlite parameters: :start_unix and :end_unix.
-- The query emits one complete replay JSON object per line.
WITH source_segments AS (
    SELECT c.id AS call_id,
           c.system_short_name,
           c.talkgroup,
           c.start_time,
           t.source_id,
           MIN(t.start_time_ms) AS first_ms,
           MAX(t.stop_time_ms) AS last_ms,
           COUNT(*) OVER (PARTITION BY lower(c.system_short_name), t.source_id) AS radio_segment_count
    FROM calls c
    JOIN call_transmissions t ON t.call_id = c.id
    WHERE c.start_time <= :end_unix
      AND t.source_id IS NOT NULL
      AND t.source_id > 0
      AND t.start_status <> 'possibly_incomplete'
    GROUP BY c.id, c.system_short_name, c.talkgroup, c.start_time, t.source_id
), ordered_segments AS (
    SELECT *,
           LAG(call_id) OVER source_order AS linked_call_id,
           LAG(talkgroup) OVER source_order AS linked_talkgroup,
           LAG(last_ms) OVER source_order AS linked_last_ms
    FROM source_segments
    WINDOW source_order AS (
        PARTITION BY lower(system_short_name), source_id
        ORDER BY first_ms, call_id
    )
), usable_incidents AS (
    SELECT ic.incident_id,
           COUNT(*) AS call_count,
           SUM(CASE
                   WHEN c.transcription_status = 'complete'
                    AND c.quality_reason = 'ok'
                    AND trim(c.transcription) <> '' THEN 1
                   ELSE 0
               END) AS usable_call_count
    FROM incident_calls ic
    JOIN calls c ON c.id = ic.call_id
    GROUP BY ic.incident_id
    HAVING COUNT(*) BETWEEN 1 AND 5
       AND usable_call_count = call_count
), raw_candidates AS (
    SELECT o.call_id AS candidate_call_id,
           o.linked_call_id,
           o.source_id,
           MAX(0, o.first_ms - o.linked_last_ms) AS gap_ms,
           o.radio_segment_count,
           ic.incident_id,
           ui.call_count,
           ROW_NUMBER() OVER (
               PARTITION BY o.call_id, ic.incident_id
               ORDER BY MAX(0, o.first_ms - o.linked_last_ms), o.linked_call_id
           ) AS preferred_link
    FROM ordered_segments o
    JOIN incident_calls ic ON ic.call_id = o.linked_call_id
    JOIN usable_incidents ui ON ui.incident_id = ic.incident_id
    JOIN incidents i ON i.id = ic.incident_id
    JOIN calls candidate ON candidate.id = o.call_id
    WHERE o.start_time BETWEEN :start_unix AND :end_unix
      AND o.talkgroup = o.linked_talkgroup
      AND o.first_ms - o.linked_last_ms BETWEEN 0 AND 60000
      AND i.merged_into_incident_id = 0
      AND candidate.transcription_status = 'complete'
      AND candidate.quality_reason = 'ok'
      AND trim(candidate.transcription) <> ''
      AND NOT EXISTS (
          SELECT 1
          FROM incident_calls existing_member
          WHERE existing_member.incident_id = ic.incident_id
            AND existing_member.call_id = o.call_id
      )
), eligible AS (
    SELECT *
    FROM raw_candidates
    WHERE preferred_link = 1
)
SELECT json_object(
    'baseUrl', 'http://127.0.0.1:12434/v1',
    'apiKey', '',
    'model', 'pizzawave-membership-adapter',
    'incidentId', e.incident_id,
    'incidentObservationId', 'trial:incident:' || e.incident_id,
    'directlyLinkedCallId', e.linked_call_id,
    'directlyLinkedObservationId', 'trial:call:' || e.linked_call_id,
    'sourceLink', json_object(
        'gapMilliseconds', e.gap_ms,
        'radioSegmentCount', e.radio_segment_count
    ),
    'establishedCalls', json((
        SELECT json_group_array(json_object(
            'callId', c.id,
            'observationId', 'trial:call:' || c.id,
            'startTime', c.start_time,
            'stopTime', c.stop_time,
            'systemName', c.system_short_name,
            'talkgroupName', c.talkgroup_name,
            'transcript', c.transcription
        ))
        FROM incident_calls members
        JOIN calls c ON c.id = members.call_id
        WHERE members.incident_id = e.incident_id
        ORDER BY c.start_time, c.id
    )),
    'candidate', json_object(
        'callId', candidate.id,
        'observationId', 'trial:call:' || candidate.id,
        'startTime', candidate.start_time,
        'stopTime', candidate.stop_time,
        'systemName', candidate.system_short_name,
        'talkgroupName', candidate.talkgroup_name,
        'transcript', candidate.transcription
    )
)
FROM eligible e
JOIN calls candidate ON candidate.id = e.candidate_call_id
ORDER BY candidate.start_time, candidate.id, e.incident_id;
