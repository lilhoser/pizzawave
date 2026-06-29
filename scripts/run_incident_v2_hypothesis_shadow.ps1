param(
    [string]$CorpusPath = "",
    [string]$OutputDirectory = "artifacts/incident-replay-corpus",
    [string]$OpenAiBaseUrl = "http://localhost:1234/v1",
    [string]$Model = "qwen3.6-35b-a3b",
    [string]$ApiKey = "",
    [int]$MaxCases = 20,
    [int]$TimeoutSeconds = 180,
    [int]$MaxTokens = 3500,
    [string]$AuditIds = "",
    [string]$ExistingHypothesesPath = "",
    [switch]$ReuseExistingHypothesesWhenAvailable,
    [switch]$IncludeAcceptedCases,
    [switch]$IncludeRejectedCases,
    [switch]$NoResponseFormat
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputFullPath = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null

if ([string]::IsNullOrWhiteSpace($CorpusPath)) {
    $CorpusPath = Get-ChildItem -Path $outputFullPath -Filter "incident-replay-corpus-*.json" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (!(Test-Path $CorpusPath)) {
    throw "Corpus not found: $CorpusPath"
}

if (!$IncludeAcceptedCases -and !$IncludeRejectedCases) {
    $IncludeAcceptedCases = $true
    $IncludeRejectedCases = $true
}

$corpus = Get-Content -Path $CorpusPath -Raw | ConvertFrom-Json
$requestedAuditIds = @()
if (![string]::IsNullOrWhiteSpace($AuditIds)) {
    $requestedAuditIds = @($AuditIds -split "," |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 } |
        ForEach-Object { [long]$_ })
}

$callsById = @{}
foreach ($call in @($corpus.calls)) {
    if ($null -ne $call.callId) {
        $callsById[[long]$call.callId] = $call
    }
}

function New-SpanSchema {
    @{
        type = "array"
        minItems = 1
        items = @{
            type = "object"
            additionalProperties = $false
            properties = @{
                call_id = @{ type = "number" }
                start_char = @{ type = "number" }
                end_char = @{ type = "number" }
                text = @{ type = "string"; pattern = "^(?!.*\.\.\.).+$" }
            }
            required = @("call_id", "start_char", "end_char", "text")
        }
    }
}

function New-NumberArraySchema {
    @{ type = "array"; minItems = 1; items = @{ type = "number" } }
}

function New-StringArraySchema {
    @{ type = "array"; items = @{ type = "string" } }
}

function New-V2ResponseFormat {
    $spanArray = New-SpanSchema
    $numberArray = New-NumberArraySchema
    $stringArray = New-StringArraySchema
    @{
        type = "json_schema"
        json_schema = @{
            name = "pizzawave_incident_hypotheses_v2"
            strict = $false
            schema = @{
                type = "object"
                additionalProperties = $false
                properties = @{
                    hypotheses = @{
                        type = "array"
                        items = @{
                            type = "object"
                            additionalProperties = $false
                            properties = @{
                                hypothesis_id = @{ type = "string" }
                                candidate_incident_key = @{ type = "string" }
                                model_confidence = @{ type = "number" }
                                candidate_call_ids = $numberArray
                                events = @{
                                    type = "array"
                                    minItems = 1
                                    items = @{
                                        type = "object"
                                        additionalProperties = $false
                                        properties = @{
                                            event_class = @{ type = "string" }
                                            event_subtype = @{ type = "string" }
                                            strength = @{ type = "string"; enum = @("none", "weak", "continuation", "strong", "primary", "confirmed") }
                                            source_call_ids = $numberArray
                                            spans = $spanArray
                                        }
                                        required = @("event_class", "event_subtype", "strength", "source_call_ids", "spans")
                                    }
                                }
                                locations = @{
                                    type = "array"
                                    items = @{
                                        type = "object"
                                        additionalProperties = $false
                                        properties = @{
                                            kind = @{ type = "string"; enum = @("address", "intersection", "route", "landmark", "unknown") }
                                            display = @{ type = "string" }
                                            normalized_key = @{ type = "string" }
                                            confidence = @{ type = "string"; enum = @("unknown", "low", "medium", "high") }
                                            source_call_ids = $numberArray
                                            spans = $spanArray
                                        }
                                        required = @("kind", "display", "normalized_key", "confidence", "source_call_ids", "spans")
                                    }
                                }
                                membership = @{
                                    type = "array"
                                    minItems = 1
                                    items = @{
                                        type = "object"
                                        additionalProperties = $false
                                        properties = @{
                                            call_id = @{ type = "number" }
                                            role = @{ type = "string"; enum = @("primary_event", "continuation", "logistics", "routine", "unrelated", "conflicting") }
                                            decision = @{ type = "string"; enum = @("accept", "reject", "hold") }
                                            reasons = $stringArray
                                            spans = $spanArray
                                        }
                                        required = @("call_id", "role", "decision", "reasons", "spans")
                                    }
                                }
                                conflicts = @{
                                    type = "array"
                                    items = @{
                                        type = "object"
                                        additionalProperties = $false
                                        properties = @{
                                            conflict_type = @{ type = "string" }
                                            call_ids = $numberArray
                                            reason = @{ type = "string" }
                                            spans = $spanArray
                                        }
                                        required = @("conflict_type", "call_ids", "reason", "spans")
                                    }
                                }
                                narrative = @{
                                    type = "object"
                                    additionalProperties = $false
                                    properties = @{
                                        title = @{ type = "string" }
                                        detail = @{ type = "string" }
                                        facts = @{
                                            type = "array"
                                            minItems = 1
                                            items = @{
                                                type = "object"
                                                additionalProperties = $false
                                                properties = @{
                                                    kind = @{ type = "string" }
                                                    text = @{ type = "string" }
                                                    spans = $spanArray
                                                }
                                                required = @("kind", "text", "spans")
                                            }
                                        }
                                    }
                                    required = @("title", "detail", "facts")
                                }
                            }
                            required = @("hypothesis_id", "candidate_incident_key", "model_confidence", "candidate_call_ids", "events", "locations", "membership", "conflicts", "narrative")
                        }
                    }
                }
                required = @("hypotheses")
            }
        }
    }
}

function Trim-Text {
    param([string]$Text, [int]$Max = 1200)
    if ($null -eq $Text) { return "" }
    $value = $Text.Trim()
    if ($value.Length -le $Max) { return $value }
    return $value.Substring(0, [Math]::Max(0, $Max - 1)) + "..."
}

function Get-CaseCalls {
    param($Case)
    @($Case.callIds | ForEach-Object {
        $id = [long]$_
        if ($callsById.ContainsKey($id)) {
            $callsById[$id]
        }
    } | Where-Object { $null -ne $_ -and ![string]::IsNullOrWhiteSpace($_.transcription) })
}

function New-UserPrompt {
    param($Case, $CaseCalls)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("/no_think")
    $lines.Add("Return only JSON that matches the schema. Do not place the answer in reasoning_content.")
    $lines.Add("Task: produce structured incident evidence hypotheses from these public safety radio transcripts.")
    $lines.Add("The server, not you, owns persistence, incident identity, final membership, and conflict enforcement.")
    $lines.Add("For every event, location, membership, conflict, and narrative fact, cite exact source spans by call_id, start_char, end_char, and text.")
    $lines.Add("span.text must be an exact substring copied from the transcript, including casing, punctuation, grammar errors, and transcription mistakes. Do not paraphrase span.text. Do not use ellipses or combine separate transcript fragments into one span. If unsure, use a shorter exact phrase.")
    $lines.Add("Use short literal spans when possible, usually 2 to 8 words. Do not cite a long sentence unless the whole sentence is exactly copied from the transcript.")
    $lines.Add("Never output null in spans, reasons, or facts. If you cannot cite an exact span for an event, location, membership decision, conflict, or narrative fact, do not make that claim.")
    $lines.Add("If hypotheses is not empty, every hypothesis must include at least one events item with strength strong, primary, or confirmed. Do not put event evidence only in narrative.facts.")
    $lines.Add("Every span.call_id must be one of these candidate call IDs: $(@($Case.callIds) -join ', '). Never use 0 or an invented call_id.")
    $lines.Add("Do not use retrieval score, semantic similarity, talkgroup, category, or an existing title as proof. Those fields only explain why a call was reviewed.")
    $lines.Add("Classify every candidate call as primary_event, continuation, logistics, routine, unrelated, or conflicting.")
    $lines.Add("membership is mandatory. Include exactly one membership row for every candidate_call_id. For the first source-backed dispatch/event call use role=primary_event and decision=accept. For same-event updates use role=continuation and decision=accept. For unrelated or routine calls use decision=reject.")
    $lines.Add("Do not drop an initial dispatch call merely because later calls have clearer event wording. If it is the same incident, include it with an exact dispatch or location span.")
    $lines.Add("When a candidate set mixes unrelated calls, keep the source-backed incident subset and reject the unrelated calls. Do not reject the whole hypothesis merely because retrieval brought in an unrelated neighbor.")
    $lines.Add("Reject standalone routine activity: non-emergency transport, hospital handoff, facility transfer, supervisor/driver/schedule/computer operations, routine traffic stops, tag checks, warrant checks, music/noise complaints without threat, and standalone EMS assist or lift assist. If such traffic clearly belongs to an existing source-backed emergency, mark it as logistics or continuation for that parent incident instead of a new incident.")
    $lines.Add("Use conflicts for same symptom at different addresses, unrelated locations, unrelated patients, unrelated vehicles, or semantic neighbors with no shared event proof.")
    $lines.Add("Use narrative facts only when the exact fact is supported by retained source spans.")
    $lines.Add("If no real incident is source-backed, return hypotheses: [].")
    $lines.Add("Minimum non-empty structure: hypotheses[0].events has at least one strong event with spans; hypotheses[0].membership has one row per candidate call; hypotheses[0].narrative.facts has at least one fact with spans.")
    $lines.Add("")
    $lines.Add("V1 audit context, for comparison only:")
    $lines.Add("audit_id=$($Case.auditId); v1_accepted=$($Case.accepted); failure_class=$($Case.failureClass); v1_reason=$(Trim-Text ([string]$Case.reason) 400); incident_key=$($Case.incidentKey)")
    $lines.Add("")
    $lines.Add("Candidate calls:")
    foreach ($call in $CaseCalls) {
        $lines.Add("- call_id=$($call.callId); system=$($call.systemShortName); category=$($call.category); talkgroup=$($call.talkgroupName); start_unix=$($call.startTime)")
        $lines.Add("  transcript=$(Trim-Text ([string]$call.transcription) 1600)")
    }
    return ($lines -join "`n")
}

function Get-JsonContent {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return "" }
    $value = $Text.Trim()
    if ($value.StartsWith("{") -and $value.EndsWith("}")) { return $value }
    $start = $value.IndexOf("{")
    $end = $value.LastIndexOf("}")
    if ($start -ge 0 -and $end -gt $start) {
        return $value.Substring($start, $end - $start + 1)
    }
    return $value
}

function Get-AllSpans {
    param($Hypothesis)
    $spans = @()
    foreach ($event in @($Hypothesis.events)) { $spans += @($event.spans) }
    foreach ($location in @($Hypothesis.locations)) { $spans += @($location.spans) }
    foreach ($membership in @($Hypothesis.membership)) { $spans += @($membership.spans) }
    foreach ($conflict in @($Hypothesis.conflicts)) { $spans += @($conflict.spans) }
    foreach ($fact in @($Hypothesis.narrative.facts)) { $spans += @($fact.spans) }
    return $spans
}

function ConvertTo-LongValues {
    param($Values)
    $result = New-Object System.Collections.Generic.List[long]
    foreach ($value in @($Values)) {
        if ($null -eq $value) {
            continue
        }

        try {
            $result.Add([long]$value)
        }
        catch {
        }
    }

    return @($result.ToArray())
}

function Normalize-HypothesisShape {
    param($Case, $Hypothesis)

    if ($null -eq $Hypothesis) {
        return $null
    }

    $candidateCallIds = @(ConvertTo-LongValues $Hypothesis.candidate_call_ids)
    if ($candidateCallIds.Count -eq 0) {
        $candidateCallIds = @(ConvertTo-LongValues $Case.callIds)
    }

    $events = @($Hypothesis.events | ForEach-Object {
        $event = $_
        $spans = @($event.spans | Where-Object { $null -ne $_ })
        $sourceCallIds = @(ConvertTo-LongValues $event.source_call_ids)
        if ($sourceCallIds.Count -eq 0) {
            $sourceCallIds = @(ConvertTo-LongValues (@($spans | ForEach-Object { $_.call_id })))
        }

        $eventClass = [string]$event.event_class
        if ([string]::IsNullOrWhiteSpace($eventClass)) {
            $eventClass = [string]$event.type
        }
        if ([string]::IsNullOrWhiteSpace($eventClass)) {
            $eventClass = [string]$event.class
        }

        $eventSubtype = [string]$event.event_subtype
        if ([string]::IsNullOrWhiteSpace($eventSubtype)) {
            $eventSubtype = [string]$event.subtype
        }
        if ([string]::IsNullOrWhiteSpace($eventSubtype)) {
            $eventSubtype = [string]$event.type
        }

        [pscustomobject][ordered]@{
            event_class = $eventClass
            event_subtype = $eventSubtype
            strength = if ([string]::IsNullOrWhiteSpace([string]$event.strength)) { "weak" } else { [string]$event.strength }
            source_call_ids = $sourceCallIds
            spans = $spans
        }
    })

    $locations = @()
    if ($null -ne $Hypothesis.locations) {
        $locations += @($Hypothesis.locations | ForEach-Object {
            $location = $_
            [pscustomobject][ordered]@{
                kind = if ([string]::IsNullOrWhiteSpace([string]$location.kind)) { "unknown" } else { [string]$location.kind }
                display = [string]$location.display
                normalized_key = [string]$location.normalized_key
                confidence = if ([string]::IsNullOrWhiteSpace([string]$location.confidence)) { "medium" } else { [string]$location.confidence }
                source_call_ids = @(ConvertTo-LongValues (@($location.source_call_ids) + @($location.spans | Where-Object { $null -ne $_ } | ForEach-Object { $_.call_id })))
                spans = @($location.spans | Where-Object { $null -ne $_ })
            }
        })
    }
    if ($null -ne $Hypothesis.location) {
        $location = $Hypothesis.location
        $display = [string]$location.display
        if ([string]::IsNullOrWhiteSpace($display)) {
            $display = [string]$location.address
        }
        if ([string]::IsNullOrWhiteSpace($display)) {
            $display = [string]$location.intersection
        }
        if ([string]::IsNullOrWhiteSpace($display)) {
            $display = [string]$location.route
        }
        if ([string]::IsNullOrWhiteSpace($display)) {
            $display = [string]$location.landmark
        }

        $locations += [pscustomobject][ordered]@{
            kind = if ([string]::IsNullOrWhiteSpace([string]$location.kind)) { "unknown" } else { [string]$location.kind }
            display = $display
            normalized_key = if ([string]::IsNullOrWhiteSpace([string]$location.normalized_key)) { $display.ToLowerInvariant() } else { [string]$location.normalized_key }
            confidence = if ([string]::IsNullOrWhiteSpace([string]$location.confidence)) { "medium" } else { [string]$location.confidence }
            source_call_ids = @(ConvertTo-LongValues (@($location.source_call_ids) + @($location.spans | Where-Object { $null -ne $_ } | ForEach-Object { $_.call_id })))
            spans = @($location.spans | Where-Object { $null -ne $_ })
        }
    }

    $conflicts = @($Hypothesis.conflicts | ForEach-Object {
        $conflict = $_
        $spans = @($conflict.spans | Where-Object { $null -ne $_ })
        $callIds = @(ConvertTo-LongValues $conflict.call_ids)
        if ($callIds.Count -eq 0) {
            $callIds = @(ConvertTo-LongValues (@($spans | ForEach-Object { $_.call_id })))
        }

        $conflictType = [string]$conflict.conflict_type
        if ([string]::IsNullOrWhiteSpace($conflictType)) {
            $conflictType = [string]$conflict.type
        }
        if ([string]::IsNullOrWhiteSpace($conflictType)) {
            $conflictType = "unspecified"
        }

        $reason = [string]$conflict.reason
        if ([string]::IsNullOrWhiteSpace($reason)) {
            $reason = [string]$conflict.description
        }

        [pscustomobject][ordered]@{
            conflict_type = $conflictType
            call_ids = $callIds
            reason = $reason
            spans = $spans
        }
    })

    $membership = @($Hypothesis.membership | ForEach-Object {
        $row = $_
        $callId = $row.call_id
        if ($null -eq $callId) {
            $callId = $row.candidate_call_id
        }

        $reasons = @($row.reasons)
        if ($reasons.Count -eq 0 -and ![string]::IsNullOrWhiteSpace([string]$row.reasoning)) {
            $reasons = @([string]$row.reasoning)
        }

        [pscustomobject][ordered]@{
            call_id = [long]$callId
            role = if ([string]::IsNullOrWhiteSpace([string]$row.role)) { "unrelated" } else { [string]$row.role }
            decision = if ([string]::IsNullOrWhiteSpace([string]$row.decision)) { "reject" } else { [string]$row.decision }
            reasons = $reasons
            spans = @($row.spans | Where-Object { $null -ne $_ })
        }
    })

    $narrativeFacts = @($Hypothesis.narrative.facts | ForEach-Object {
        $fact = $_
        $text = [string]$fact.text
        if ([string]::IsNullOrWhiteSpace($text)) {
            $text = [string]$fact.fact
        }

        [pscustomobject][ordered]@{
            kind = if ([string]::IsNullOrWhiteSpace([string]$fact.kind)) { "fact" } else { [string]$fact.kind }
            text = $text
            spans = @($fact.spans | Where-Object { $null -ne $_ })
        }
    })

    $title = [string]$Hypothesis.narrative.title
    if ([string]::IsNullOrWhiteSpace($title) -and $narrativeFacts.Count -gt 0) {
        $title = [string]$narrativeFacts[0].text
    }
    if ([string]::IsNullOrWhiteSpace($title) -and $events.Count -gt 0) {
        $title = [string]$events[0].event_subtype
    }

    [pscustomobject][ordered]@{
        hypothesis_id = if ([string]::IsNullOrWhiteSpace([string]$Hypothesis.hypothesis_id)) { "hyp_$($Case.auditId)" } else { [string]$Hypothesis.hypothesis_id }
        candidate_incident_key = if ([string]::IsNullOrWhiteSpace([string]$Hypothesis.candidate_incident_key)) { "shadow" } else { [string]$Hypothesis.candidate_incident_key }
        model_confidence = if ($null -eq $Hypothesis.model_confidence) { 0.5 } else { [double]$Hypothesis.model_confidence }
        candidate_call_ids = $candidateCallIds
        events = $events
        locations = $locations
        membership = $membership
        conflicts = $conflicts
        narrative = [pscustomobject][ordered]@{
            title = $title
            detail = [string]$Hypothesis.narrative.detail
            facts = $narrativeFacts
        }
    }
}

function Normalize-EvidenceLookupText {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) {
        return ""
    }

    $builder = [System.Text.StringBuilder]::new()
    $lastWasSpace = $true
    foreach ($ch in $Value.ToCharArray()) {
        if ([char]::IsLetterOrDigit($ch)) {
            [void]$builder.Append([char]::ToLowerInvariant($ch))
            $lastWasSpace = $false
            continue
        }

        if (!$lastWasSpace) {
            [void]$builder.Append(' ')
            $lastWasSpace = $true
        }
    }

    return $builder.ToString().Trim()
}

function Test-QuotedEvidenceText {
    param(
        [string]$Transcript,
        [string]$Quoted
    )

    if ([string]::IsNullOrWhiteSpace($Quoted)) {
        return $false
    }

    if ($Transcript.IndexOf($Quoted, [StringComparison]::Ordinal) -ge 0) {
        return $true
    }

    $normalizedTranscript = Normalize-EvidenceLookupText $Transcript
    $normalizedQuote = Normalize-EvidenceLookupText $Quoted
    return ![string]::IsNullOrWhiteSpace($normalizedQuote) -and
        $normalizedTranscript.IndexOf($normalizedQuote, [StringComparison]::Ordinal) -ge 0
}

function Test-HypothesisSpans {
    param($Hypothesis, $TranscriptsByCallId)
    $errors = New-Object System.Collections.Generic.List[string]
    foreach ($span in @(Get-AllSpans $Hypothesis)) {
        $callId = [long]$span.call_id
        if (!$TranscriptsByCallId.ContainsKey($callId)) {
            $errors.Add("call ${callId}: transcript unavailable for evidence span")
            continue
        }
        $transcript = [string]$TranscriptsByCallId[$callId]
        $start = [int]$span.start_char
        $end = [int]$span.end_char
        $quoted = [string]$span.text
        if ($start -lt 0 -or $end -lt $start -or $end -gt $transcript.Length) {
            if (!(Test-QuotedEvidenceText -Transcript $transcript -Quoted $quoted)) {
                $errors.Add("call ${callId}: evidence span ${start}-${end} is outside transcript bounds")
            }
            continue
        }
        $actual = $transcript.Substring($start, $end - $start)
        if ($actual -cne [string]$span.text) {
            if (!(Test-QuotedEvidenceText -Transcript $transcript -Quoted $quoted)) {
                $errors.Add("call ${callId}: evidence span text mismatch")
            }
        }
    }
    return @($errors)
}

function Test-SpanGrounded {
    param($Span, $TranscriptsByCallId)
    if ($null -eq $Span) {
        return $false
    }

    $callId = [long]$Span.call_id
    if (!$TranscriptsByCallId.ContainsKey($callId)) {
        return $false
    }

    $transcript = [string]$TranscriptsByCallId[$callId]
    $start = [int]$Span.start_char
    $end = [int]$Span.end_char
    $quoted = [string]$Span.text
    if ($start -lt 0 -or $end -lt $start -or $end -gt $transcript.Length) {
        return Test-QuotedEvidenceText -Transcript $transcript -Quoted $quoted
    }

    $actual = $transcript.Substring($start, $end - $start)
    return ($actual -ceq $quoted) -or (Test-QuotedEvidenceText -Transcript $transcript -Quoted $quoted)
}

function Test-AnySpanGrounded {
    param($Spans, $TranscriptsByCallId)
    foreach ($span in @($Spans)) {
        if (Test-SpanGrounded $span $TranscriptsByCallId) {
            return $true
        }
    }
    return $false
}

function Test-PrimaryStrength {
    param([string]$Strength)
    $value = if ($null -eq $Strength) { "" } else { $Strength.ToLowerInvariant() }
    return $value -in @("strong", "primary", "dispatch", "confirmed")
}

function Test-RoutineOrAdministrativePrimaryEvent {
    param($Event)
    $value = ("$($Event.event_class) $($Event.event_subtype)").Trim().ToLowerInvariant()
    if ($value.Length -eq 0) { return $true }
    $spanText = (@($Event.spans) | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_.text }) -join " "
    $sourceText = "$value $spanText".ToLowerInvariant()
    if ($sourceText.Contains("code 73") -or $sourceText.Contains("doa") -or $sourceText.Contains("doi call")) {
        return $false
    }

    foreach ($token in @("routine", "administrative", "logistics", "medical_assist", "ems_assist", "ems assist", "status", "traffic_stop", "vehicle_stop", "tag_check", "license_check", "warrant_check", "person_check", "subject_check", "subject_location", "subject_identity", "identity_check", "non_emergency", "status_update", "unit_status", "code_status", "transport")) {
        if ($sourceText.Contains($token)) {
            return $true
        }
    }
    if ($value.Contains("communication_failure")) {
        return $true
    }
    if ($sourceText.Contains("non-emergency")) {
        return $true
    }
    $genericDispatchOrUpdate = (
        $value -eq "dispatch dispatch" -or
        $value -eq "update update" -or
        $value -eq "dispatch update" -or
        $value -eq "update dispatch" -or
        $value.Contains("code")
    ) -and !(Test-UrgentPublicSafetySignal $sourceText)
    if ($genericDispatchOrUpdate) {
        return $true
    }
    return $false
}

function Test-ServerRecognizedBlockingConflict {
    param($Conflict)

    $type = ([string]$Conflict.conflict_type).ToLowerInvariant()
    return $type -in @(
        "location_conflict",
        "person_conflict",
        "vehicle_conflict",
        "parent_event_conflict"
    )
}

function Test-MedicalEmergencySignal {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    $value = $Text.ToLowerInvariant()
    return $value.Contains("unconscious") -or
        $value.Contains("not breathing") -or
        $value.Contains("cpr") -or
        $value.Contains("heart attack") -or
        $value.Contains("bar attack") -or
        $value.Contains("another onset") -or
        $value.Contains("heart problems") -or
        $value.Contains("overdose") -or
        $value.Contains("seizure") -or
        $value.Contains("chest pain")
}

function Test-InitialMedicalDispatchSignal {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    $value = $Text.ToLowerInvariant()
    return ($value.Contains("engine") -or
            $value.Contains("medic") -or
            $value.Contains("ems") -or
            $value.Contains("ambulance")) -and
        (Test-MedicalEmergencySignal $value)
}

function Test-MedicalEmergencyEvent {
    param($Event)

    $value = "$([string]$Event.event_class) $([string]$Event.event_subtype)".ToLowerInvariant()
    return $value.Contains("medical") -or
        $value.Contains("ems") -or
        $value.Contains("overdose") -or
        $value.Contains("unconscious") -or
        $value.Contains("cpr")
}

function Test-AcceptedMedicalEmergencyEvidence {
    param($Hypothesis, $AcceptedCallIds, $TranscriptsByCallId)

    foreach ($event in @($Hypothesis.events)) {
        $sourceIds = @(ConvertTo-LongValues $event.source_call_ids)
        if (@($sourceIds | Where-Object { $AcceptedCallIds -contains $_ }).Count -eq 0) {
            continue
        }
        if (@($event.spans).Count -eq 0 -or !(Test-AnySpanGrounded -Spans $event.spans -TranscriptsByCallId $TranscriptsByCallId)) {
            continue
        }
        if (Test-MedicalEmergencyEvent $event) {
            return $true
        }
        foreach ($span in @($event.spans)) {
            if (Test-MedicalEmergencySignal ([string]$span.text)) {
                return $true
            }
        }
    }

    return $false
}

function Test-RecognizedBlockingConflictForCall {
    param($Hypothesis, [long]$CallId, $TranscriptsByCallId)

    foreach ($conflict in @($Hypothesis.conflicts)) {
        $callIds = @(ConvertTo-LongValues $conflict.call_ids)
        if (($callIds -contains $CallId) -and
            (Test-ServerRecognizedBlockingConflict $conflict) -and
            (@($conflict.spans).Count -eq 0 -or (Test-AnySpanGrounded -Spans $conflict.spans -TranscriptsByCallId $TranscriptsByCallId))) {
            return $true
        }
    }

    return $false
}

function Add-ServerRecognizedInitialDispatchCalls {
    param($Hypothesis, $AcceptedCallIds, $TranscriptsByCallId, $Reasons)

    $accepted = @(ConvertTo-LongValues $AcceptedCallIds | Sort-Object -Unique)
    if ($accepted.Count -eq 0 -or !(Test-AcceptedMedicalEmergencyEvidence -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)) {
        return $accepted
    }

    $earliestAccepted = @($accepted | Sort-Object | Select-Object -First 1)[0]
    $retained = @()
    foreach ($callId in @(ConvertTo-LongValues $Hypothesis.candidate_call_ids | Sort-Object -Unique)) {
        if (($accepted -contains $callId) -or $callId -gt $earliestAccepted) {
            continue
        }
        if (!$TranscriptsByCallId.ContainsKey($callId)) {
            continue
        }
        if (!(Test-InitialMedicalDispatchSignal ([string]$TranscriptsByCallId[$callId]))) {
            continue
        }
        if (Test-RecognizedBlockingConflictForCall -Hypothesis $Hypothesis -CallId $callId -TranscriptsByCallId $TranscriptsByCallId) {
            continue
        }

        $retained += $callId
    }

    if ($retained.Count -gt 0) {
        $Reasons.Add("server retained source-backed initial emergency dispatch call(s): $(@($retained) -join ',')")
    }

    return @($accepted + $retained | Sort-Object -Unique)
}

function Test-ContinuationCandidate {
    param($Membership)

    $role = ([string]$Membership.role).ToLowerInvariant()
    $decision = ([string]$Membership.decision).ToLowerInvariant()
    if ($role -in @("routine", "routine_status", "unrelated", "conflicting", "conflict")) { return $false }
    if ($role -eq "primary_event") { return $false }
    if ($decision -eq "hold") { return $false }

    return ($role -in @("continuation", "supporting", "update")) -or
        ($decision -in @("accept", "accepted", "retain", "retained", "supporting", "reject", "rejected"))
}

function Get-TranscriptLocationKeys {
    param([string]$Transcript)

    $pattern = "\b\d{1,5}\s*,?\s+(?:[a-z0-9]+\.?\s+){0,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)(?:\s*,?\s+(?:n|s|e|w|ne|nw|se|sw|north|south|east|west|northeast|northwest|southeast|southwest))?\b"
    @([regex]::Matches(([string]$Transcript).ToLowerInvariant(), $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) |
        ForEach-Object { Normalize-LocationConflictKey $_.Value.Trim() } |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
}

function Test-SharesConcreteLocationWithAcceptedCall {
    param([long]$CallId, [string]$Transcript, $AcceptedCallIds, $TranscriptsByCallId)

    $candidateKeys = @(Get-TranscriptLocationKeys $Transcript)
    if ($candidateKeys.Count -eq 0) { return $false }

    foreach ($acceptedItem in @($AcceptedCallIds | Sort-Object -Unique)) {
        $acceptedCallId = [long]$acceptedItem
        if ($acceptedCallId -eq $CallId -or !$TranscriptsByCallId.ContainsKey($acceptedCallId)) { continue }
        $acceptedKeys = @(Get-TranscriptLocationKeys ([string]$TranscriptsByCallId[$acceptedCallId]))
        foreach ($candidate in $candidateKeys) {
            foreach ($accepted in $acceptedKeys) {
                if (Test-LocationKeysCompatible ([string]$candidate) ([string]$accepted)) { return $true }
            }
        }
    }

    return $false
}

function Test-HasConflictingConcreteLocation {
    param([long]$CallId, [string]$Transcript, $AcceptedCallIds, $TranscriptsByCallId)

    $candidateKeys = @(Get-TranscriptLocationKeys $Transcript)
    if ($candidateKeys.Count -eq 0) { return $false }

    foreach ($acceptedItem in @($AcceptedCallIds | Sort-Object -Unique)) {
        $acceptedCallId = [long]$acceptedItem
        if ($acceptedCallId -eq $CallId -or !$TranscriptsByCallId.ContainsKey($acceptedCallId)) { continue }
        $acceptedKeys = @(Get-TranscriptLocationKeys ([string]$TranscriptsByCallId[$acceptedCallId]))
        if ($acceptedKeys.Count -eq 0) { continue }

        $compatible = $false
        foreach ($candidate in $candidateKeys) {
            foreach ($accepted in $acceptedKeys) {
                if (Test-LocationKeysCompatible ([string]$candidate) ([string]$accepted)) {
                    $compatible = $true
                    break
                }
            }
            if ($compatible) { break }
        }
        if (!$compatible) { return $true }
    }

    return $false
}

function Test-SharesParentEventGroupWithAcceptedCall {
    param([string]$Transcript, $AcceptedCallIds, $TranscriptsByCallId)

    $group = Get-TranscriptParentEventGroup $Transcript
    if ([string]::IsNullOrWhiteSpace($group)) { return $false }

    foreach ($acceptedItem in @($AcceptedCallIds | Sort-Object -Unique)) {
        $acceptedCallId = [long]$acceptedItem
        if (!$TranscriptsByCallId.ContainsKey($acceptedCallId)) { continue }
        $acceptedGroup = Get-TranscriptParentEventGroup ([string]$TranscriptsByCallId[$acceptedCallId])
        if (![string]::IsNullOrWhiteSpace($acceptedGroup) -and $group.Equals($acceptedGroup, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-ContinuationLanguage {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    return $value.Contains("responding") -or
        $value.Contains("enroute") -or
        $value.Contains("en route") -or
        $value.Contains("currently") -or
        $value.Contains("same address") -or
        $value.Contains("caller hung up") -or
        $value.Contains("calling back") -or
        $value.Contains("tried calling back") -or
        $value.Contains("output address") -or
        $value.Contains("second one") -or
        $value.Contains("gave me") -or
        $value.Contains("reference") -or
        $value.Contains("serial") -or
        $value.Contains("put him on") -or
        $value.Contains("put her on") -or
        $value.Contains("on this") -or
        $value.Contains("card") -or
        $value.Contains("still") -or
        $value.Contains("update")
}

function Get-IncidentTokens {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    $tokens = @()
    if ($value.Contains("stabbing") -or $value.Contains("tabbing")) { $tokens += "stabbing" }
    if ($value.Contains("suicid") -or $value.Contains("kill herself") -or $value.Contains("kill himself")) { $tokens += "suicidal" }
    if ($value.Contains("gun") -or $value.Contains("firearm") -or $value.Contains("armed")) { $tokens += "weapon" }
    if ($value.Contains("black car") -or $value.Contains("black vehicle")) { $tokens += "black_vehicle" }
    if ($value.Contains("accident") -or $value.Contains("crash") -or $value.Contains("wreck")) { $tokens += "traffic_collision" }
    if ($value.Contains("structure fire") -or $value.Contains("fire structure") -or $value.Contains("on fire")) { $tokens += "structure_fire" }
    if (($value.Contains("911") -or $value.Contains("unknown to one call") -or $value.Contains("unknown one call") -or $value.Contains("unknown 1 call")) -and
        ($value.Contains("hang") -or $value.Contains("hung up") -or $value.Contains("calling back"))) { $tokens += "911_hangup" }
    if ($value.Contains("stolen firearm")) { $tokens += "stolen_firearm" }
    if ($value.Contains("dog") -or $value.Contains("humane")) { $tokens += "animal" }
    if ($value.Contains("doa") -or $value.Contains("doi call")) { $tokens += "doa" }
    return @($tokens | Sort-Object -Unique)
}

function Test-ResponderStatusForAcceptedEmergency {
    param([string]$Transcript, $AcceptedCallIds, $TranscriptsByCallId)

    $value = ([string]$Transcript).ToLowerInvariant()
    $hasResponderStatus = ($value.Contains("pd") -or
            $value.Contains("so") -or
            $value.Contains("sheriff") -or
            $value.Contains("police") -or
            $value.Contains("ems") -or
            $value.Contains("medic")) -and
        ($value.Contains("still enroute") -or
            $value.Contains("still en route") -or
            $value.Contains("enroute") -or
            $value.Contains("en route"))
    if (!$hasResponderStatus) { return $false }

    foreach ($acceptedItem in @($AcceptedCallIds | Sort-Object -Unique)) {
        $acceptedCallId = [long]$acceptedItem
        if (!$TranscriptsByCallId.ContainsKey($acceptedCallId)) { continue }
        if ((Get-TranscriptParentEventGroup ([string]$TranscriptsByCallId[$acceptedCallId])) -eq "medical") {
            return $true
        }
    }

    return $false
}

function Test-SharedIncidentToken {
    param([string]$Transcript, $AcceptedCallIds, $TranscriptsByCallId)

    $candidate = @(Get-IncidentTokens $Transcript)
    if ($candidate.Count -eq 0) { return $false }

    foreach ($acceptedItem in @($AcceptedCallIds | Sort-Object -Unique)) {
        $acceptedCallId = [long]$acceptedItem
        if (!$TranscriptsByCallId.ContainsKey($acceptedCallId)) { continue }
        $acceptedTokens = @(Get-IncidentTokens ([string]$TranscriptsByCallId[$acceptedCallId]))
        if (@($candidate | Where-Object { $acceptedTokens -contains $_ }).Count -gt 0) {
            return $true
        }
    }

    return $false
}

function Test-RoutineOrTransportOnlyTranscript {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    $nonEmergencyTransport = $value.Contains("non-emergency") -and
        ($value.Contains("facility") -or $value.Contains("hospital") -or $value.Contains("transferred") -or $value.Contains("transfer") -or $value.Contains("orders") -or $value.Contains("eta") -or $value.Contains("ea"))
    $transitOperations = ($value.Contains("supervisor") -or $value.Contains("driver") -or $value.Contains("schedule") -or $value.Contains("computer") -or $value.Contains("same issue")) -and
        ($value.Contains("not letting me") -or $value.Contains("switch over") -or $value.Contains("on time") -or $value.Contains("same schedule"))
    $trafficStop = ($value.Contains("traffic stop") -or $value.Contains("vehicle stop")) -and
        !(Test-UrgentPublicSafetySignal $value)

    return $nonEmergencyTransport -or $transitOperations -or $trafficStop
}

function Test-SourceBackedContinuationAnchor {
    param([long]$CallId, [string]$Transcript, $AcceptedCallIds, $TranscriptsByCallId)

    if ((Test-SharesConcreteLocationWithAcceptedCall -CallId $CallId -Transcript $Transcript -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId) -and
        ((Test-ContinuationLanguage $Transcript) -or (Test-SharesParentEventGroupWithAcceptedCall -Transcript $Transcript -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId))) {
        return $true
    }

    if ((Test-ResponderStatusForAcceptedEmergency -Transcript $Transcript -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId) -and
        !(Test-HasConflictingConcreteLocation -CallId $CallId -Transcript $Transcript -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId)) {
        return $true
    }

    return (Test-SharesParentEventGroupWithAcceptedCall -Transcript $Transcript -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId) -and
        (Test-ContinuationLanguage $Transcript) -and
        (Test-SharedIncidentToken -Transcript $Transcript -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId) -and
        !(Test-HasConflictingConcreteLocation -CallId $CallId -Transcript $Transcript -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId)
}

function Add-ServerRecognizedContinuationCalls {
    param($Hypothesis, $AcceptedCallIds, $TranscriptsByCallId, $Reasons)

    $accepted = @(ConvertTo-LongValues $AcceptedCallIds | Sort-Object -Unique)
    if ($accepted.Count -eq 0) { return $accepted }

    $retained = @()
    foreach ($row in @($Hypothesis.membership | Sort-Object { [long]$_.call_id })) {
        $callId = [long]$row.call_id
        if (($accepted -contains $callId) -or ($retained -contains $callId)) { continue }
        if (!(Test-ContinuationCandidate $row)) { continue }
        if (!$TranscriptsByCallId.ContainsKey($callId)) { continue }
        $transcript = [string]$TranscriptsByCallId[$callId]
        if (Test-RoutineOrTransportOnlyTranscript $transcript) { continue }
        if (Test-RecognizedBlockingConflictForCall -Hypothesis $Hypothesis -CallId $callId -TranscriptsByCallId $TranscriptsByCallId) { continue }

        $anchorSet = @($accepted + $retained | Sort-Object -Unique)
        if (!(Test-SourceBackedContinuationAnchor -CallId $callId -Transcript $transcript -AcceptedCallIds $anchorSet -TranscriptsByCallId $TranscriptsByCallId)) { continue }

        $retained += $callId
    }

    if ($retained.Count -gt 0) {
        $Reasons.Add("server retained source-backed continuation/update call(s): $(@($retained) -join ',')")
    }

    return @($accepted + $retained | Sort-Object -Unique)
}

function Add-ServerRecognizedPrimaryEventCalls {
    param($Hypothesis, $TranscriptsByCallId, $Reasons)

    $recognized = @()
    foreach ($item in @(ConvertTo-LongValues $Hypothesis.candidate_call_ids | Sort-Object -Unique)) {
        $callId = [long]$item
        if (!$TranscriptsByCallId.ContainsKey($callId)) { continue }
        $transcript = [string]$TranscriptsByCallId[$callId]
        if (Test-RoutineOrTransportOnlyTranscript $transcript) { continue }
        $group = Get-TranscriptParentEventGroup $transcript
        if ([string]::IsNullOrWhiteSpace($group)) { continue }
        $span = Get-PrimarySignalSpan -CallId $callId -Transcript $transcript -Group $group
        if ($null -eq $span) { continue }
        $recognized += [pscustomobject][ordered]@{
            callId = $callId
            group = $group
        }
    }

    if ($recognized.Count -eq 0) { return @() }

    $dominantGroup = @($recognized |
        Group-Object { Get-ParentEventCluster ([string]$_.group) } |
        Sort-Object @{ Expression = "Count"; Descending = $true }, @{ Expression = { ($_.Group | ForEach-Object { [long]$_.callId } | Sort-Object | Select-Object -First 1) } } |
        Select-Object -First 1).Name
    $retained = @($recognized |
        Where-Object { (Get-ParentEventCluster ([string]$_.group)).Equals([string]$dominantGroup, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { [long]$_.callId } |
        Sort-Object -Unique)

    if ($retained.Count -gt 0) {
        $Reasons.Add("server retained source-backed primary event call(s) after model rejected all membership: $(@($retained) -join ',')")
    }
    return $retained
}

function Get-ParentEventGroupForAcceptedCall {
    param($Hypothesis, [long]$CallId, $TranscriptsByCallId)

    if ($TranscriptsByCallId.ContainsKey($CallId)) {
        $transcriptGroup = Get-TranscriptParentEventGroup ([string]$TranscriptsByCallId[$CallId])
        if (![string]::IsNullOrWhiteSpace($transcriptGroup)) {
            return $transcriptGroup
        }
    }

    $modelGroups = @($Hypothesis.events | Where-Object {
        (Test-PrimaryStrength ([string]$_.strength)) -and
        (@(ConvertTo-LongValues $_.source_call_ids) -contains $CallId)
    } | ForEach-Object {
        Get-ParentEventGroup $_
    } | Where-Object {
        ![string]::IsNullOrWhiteSpace($_)
    } | Sort-Object -Unique)

    if ($modelGroups.Count -eq 1) { return [string]$modelGroups[0] }
    return ""
}

function Select-DominantParentEventGroupCalls {
    param($Hypothesis, $AcceptedCallIds, $TranscriptsByCallId, $Reasons)

    $accepted = @(ConvertTo-LongValues $AcceptedCallIds | Sort-Object -Unique)
    $groups = @()
    foreach ($item in $accepted) {
        $callId = [long]$item
        $group = Get-ParentEventGroupForAcceptedCall -Hypothesis $Hypothesis -CallId $callId -TranscriptsByCallId $TranscriptsByCallId
        if (![string]::IsNullOrWhiteSpace($group)) {
            $groups += [pscustomobject][ordered]@{
                callId = $callId
                group = $group
            }
        }
    }

    if ($groups.Count -eq 0) { return $accepted }

    $dominantGroup = @($groups |
        Group-Object { Get-ParentEventCluster ([string]$_.group) } |
        Sort-Object @{ Expression = "Count"; Descending = $true }, @{ Expression = { ($_.Group | ForEach-Object { [long]$_.callId } | Sort-Object | Select-Object -First 1) } } |
        Select-Object -First 1).Name
    $retained = @($accepted | Where-Object {
        $group = Get-ParentEventGroupForAcceptedCall -Hypothesis $Hypothesis -CallId ([long]$_) -TranscriptsByCallId $TranscriptsByCallId
        [string]::IsNullOrWhiteSpace($group) -or (Get-ParentEventCluster ([string]$group)).Equals([string]$dominantGroup, [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object -Unique)

    if ($retained.Count -lt $accepted.Count) {
        $dropped = @($accepted | Where-Object { $retained -notcontains $_ } | Sort-Object -Unique)
        $Reasons.Add("server dropped parent-event-conflicting neighbor call(s): $(@($dropped) -join ',')")
    }

    return $retained
}

function Test-ConcreteLocationKind {
    param([string]$Kind)

    $value = if ($null -eq $Kind) { "" } else { $Kind.ToLowerInvariant() }
    return $value -in @("address", "intersection", "highway_mile_marker")
}

function Test-HighConfidenceLocation {
    param([string]$Confidence)

    $value = if ($null -eq $Confidence) { "" } else { $Confidence.ToLowerInvariant() }
    return $value -in @("high", "strong", "confirmed")
}

function Normalize-LocationToken {
    param([string]$Token)

    switch ($Token) {
        "street" { return "st" }
        "road" { return "rd" }
        "drive" { return "dr" }
        "avenue" { return "ave" }
        "lane" { return "ln" }
        "court" { return "ct" }
        "circle" { return "cir" }
        "boulevard" { return "blvd" }
        "highway" { return "hwy" }
        "interstate" { return "i" }
        "southwest" { return "sw" }
        "south" { return "s" }
        "northwest" { return "nw" }
        "north" { return "n" }
        "southeast" { return "se" }
        "east" { return "e" }
        "northeast" { return "ne" }
        "west" { return "w" }
        default { return $Token }
    }
}

function Normalize-LocationConflictKey {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $tokens = [regex]::Matches($Value.ToLowerInvariant(), "[a-z0-9]+") |
        ForEach-Object { Normalize-LocationToken $_.Value } |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) }
    return ($tokens -join " ")
}

function Test-LocationKeysCompatible {
    param([string]$Left, [string]$Right)

    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) {
        return $false
    }

    return $Left.Equals($Right, [StringComparison]::OrdinalIgnoreCase) -or
        $Left.IndexOf($Right, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Right.IndexOf($Left, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-ServerDerivedLocationConflicts {
    param($Hypothesis, $AcceptedCallIds, $TranscriptsByCallId)

    $claims = @($Hypothesis.locations | Where-Object {
        (Test-ConcreteLocationKind ([string]$_.kind)) -and
        (Test-HighConfidenceLocation ([string]$_.confidence)) -and
        @($_.source_call_ids | ForEach-Object { [long]$_ } | Where-Object { $AcceptedCallIds -contains $_ }).Count -gt 0 -and
        @($_.spans).Count -gt 0 -and
        (Test-AnySpanGrounded -Spans $_.spans -TranscriptsByCallId $TranscriptsByCallId)
    } | ForEach-Object {
        $location = $_
        $display = [string]$location.display
        $keySource = if ([string]::IsNullOrWhiteSpace([string]$location.normalized_key)) { $display } else { [string]$location.normalized_key }
        $sourceCallIds = @($location.source_call_ids |
            ForEach-Object { [long]$_ } |
            Where-Object { $AcceptedCallIds -contains $_ } |
            Sort-Object -Unique)
        $spans = @($location.spans | Where-Object { $AcceptedCallIds -contains [long]$_.call_id })

        [pscustomobject][ordered]@{
            key = Normalize-LocationConflictKey $keySource
            display = $display
            sourceCallIds = $sourceCallIds
            spans = $spans
        }
    } | Where-Object {
        ![string]::IsNullOrWhiteSpace([string]$_.key) -and @($_.sourceCallIds).Count -gt 0
    })

    for ($i = 0; $i -lt $claims.Count; $i++) {
        for ($j = $i + 1; $j -lt $claims.Count; $j++) {
            $left = $claims[$i]
            $right = $claims[$j]
            if (@($left.sourceCallIds | Where-Object { $right.sourceCallIds -contains $_ }).Count -gt 0) {
                continue
            }
            if (Test-LocationKeysCompatible ([string]$left.key) ([string]$right.key)) {
                continue
            }

            return @([pscustomobject][ordered]@{
                conflict_type = "location_conflict"
                call_ids = @($left.sourceCallIds + $right.sourceCallIds | Sort-Object -Unique)
                reason = "accepted calls contain incompatible concrete locations: $($left.display) vs $($right.display)"
                spans = @($left.spans + $right.spans)
            })
        }
    }

    return @()
}

function Get-TranscriptLocationClaims {
    param($AcceptedCallIds, $TranscriptsByCallId)

    $pattern = "\b\d{1,5}\s*,?\s+(?:[a-z0-9]+\.?\s+){0,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)(?:\s*,?\s+(?:n|s|e|w|ne|nw|se|sw|north|south|east|west|northeast|northwest|southeast|southwest))?\b"
    $claims = @()
    foreach ($callId in @($AcceptedCallIds | Sort-Object -Unique)) {
        if (!$TranscriptsByCallId.ContainsKey([long]$callId)) {
            continue
        }
        $match = [regex]::Match(([string]$TranscriptsByCallId[[long]$callId]).ToLowerInvariant(), $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (!$match.Success) {
            continue
        }
        $display = $match.Value.Trim()
        $claims += [pscustomobject][ordered]@{
            callId = [long]$callId
            key = Normalize-LocationConflictKey $display
            display = $display
        }
    }
    return $claims
}

function Get-ServerDerivedTranscriptLocationConflicts {
    param($AcceptedCallIds, $TranscriptsByCallId)

    foreach ($callId in @($AcceptedCallIds | Sort-Object -Unique)) {
        if (!$TranscriptsByCallId.ContainsKey([long]$callId) -or !(Test-FireEventSignal ([string]$TranscriptsByCallId[[long]$callId]))) {
            return @()
        }
    }

    $claims = @(Get-TranscriptLocationClaims -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId | Where-Object { ![string]::IsNullOrWhiteSpace([string]$_.key) })
    for ($i = 0; $i -lt $claims.Count; $i++) {
        for ($j = $i + 1; $j -lt $claims.Count; $j++) {
            $left = $claims[$i]
            $right = $claims[$j]
            if (Test-LocationKeysCompatible ([string]$left.key) ([string]$right.key)) {
                continue
            }

            return @([pscustomobject][ordered]@{
                conflict_type = "location_conflict"
                call_ids = @([long]$left.callId, [long]$right.callId)
                reason = "accepted transcripts contain incompatible concrete locations: $($left.display) vs $($right.display)"
                spans = @()
            })
        }
    }

    return @()
}

function Get-ParentEventGroup {
    param($Event)

    $value = "$([string]$Event.event_class) $([string]$Event.event_subtype)".ToLowerInvariant()
    if ($value.Contains("medical") -or $value.Contains("ems") -or $value.Contains("overdose") -or $value.Contains("stroke") -or $value.Contains("chest") -or $value.Contains("unconscious") -or $value.Contains("cpr")) { return "medical" }
    if ($value.Contains("traffic") -or $value.Contains("crash") -or $value.Contains("mvc") -or $value.Contains("mva") -or $value.Contains("accident") -or $value.Contains("road_hazard")) { return "traffic" }
    if ($value.Contains("shoot") -or $value.Contains("shot") -or $value.Contains("stab") -or $value.Contains("assault") -or $value.Contains("suicid") -or $value.Contains("weapon")) { return "violent_police" }
    if ($value.Contains("theft") -or $value.Contains("burglary") -or $value.Contains("stolen") -or $value.Contains("vandal")) { return "property_police" }
    if ($value.Contains("structure_fire") -or $value.Contains("fire_alarm") -or (Test-FireEventSignal $value)) { return "fire" }
    if ($value.Contains("animal") -or $value.Contains("dog")) { return "animal" }
    if ($value.Contains("911") -or $value.Contains("hang_up") -or $value.Contains("hangup")) { return "service_call" }
    return ""
}

function Test-FireEventSignal {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    return $value.Contains("structure fire") -or
        $value.Contains("fire structure") -or
        $value.Contains("commercial structure") -or
        $value.Contains("fire alarm") -or
        $value.Contains("automatic fire") -or
        $value.Contains("carbon") -or
        $value.Contains("gas leak") -or
        $value.Contains("on fire") -or
        $value.Contains("alarm")
}

function Get-TranscriptParentEventGroup {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    if ((Test-TrafficIncidentSignal $value) -or (Test-PedestrianTrafficHazardSignal $value)) { return "traffic" }
    if ((Test-MedicalEmergencySignal $value) -or $value.Contains("difficulty breathing") -or $value.Contains("patient") -or $value.Contains("diabetic") -or $value.Contains("code 73") -or $value.Contains("doa") -or $value.Contains("doi call")) { return "medical" }
    if ($value.Contains("shots fired") -or $value.Contains("stabbing") -or $value.Contains("tabbing") -or $value.Contains("fight call") -or $value.Contains("assault") -or $value.Contains("arguing with someone") -or $value.Contains("ring help") -or $value.Contains("suicid") -or $value.Contains("kill herself") -or $value.Contains("kill himself") -or (Test-ContainsWord $value "gun") -or $value.Contains("firearm") -or $value.Contains("armed")) { return "violent_police" }
    if ($value.Contains("stolen firearm") -or $value.Contains("stolen vehicle") -or $value.Contains("burglary") -or $value.Contains("theft") -or $value.Contains("hit the property") -or $value.Contains("mailbox") -or $value.Contains("trash can") -or $value.Contains("property damage")) { return "property_police" }
    if ($value.Contains("traffic stop") -or $value.Contains("vehicle stop") -or $value.Contains("accident") -or $value.Contains("crash") -or $value.Contains("wreck")) { return "traffic" }
    if ((Test-FireEventSignal $value) -or $value.Contains("carbon monoxide") -or $value.Contains("oxide detection")) { return "fire" }
    if (($value.Contains("911") -or $value.Contains("unknown to one call") -or $value.Contains("unknown one call") -or $value.Contains("unknown 1 call")) -and
        ($value.Contains("hang") -or $value.Contains("hung up") -or $value.Contains("calling back"))) { return "service_call" }
    if ($value.Contains("dog") -or $value.Contains("humane")) { return "animal" }
    return ""
}

function Test-TrafficIncidentSignal {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    return $value.Contains("accident") -or
        $value.Contains("crash") -or
        $value.Contains("wreck") -or
        $value.Contains("mvc") -or
        $value.Contains("mva") -or
        $value.Contains("nvc") -or
        $value.Contains("nva") -or
        $value.Contains("entrapment") -or
        (Test-PedestrianTrafficHazardSignal $value) -or
        $value.Contains("lanes are blocked") -or
        $value.Contains("both lanes") -or
        $value.Contains("interstate")
}

function Test-ContainsWord {
    param([string]$Text, [string]$Word)

    if ([string]::IsNullOrWhiteSpace($Word)) { return $false }
    return [regex]::IsMatch([string]$Text, "(?i)(?<![A-Za-z0-9])$([regex]::Escape($Word))(?![A-Za-z0-9])")
}

function Test-PedestrianTrafficHazardSignal {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    if ($value.Contains("crossing all lanes of traffic") -or
        $value.Contains("crossing lanes of traffic") -or
        $value.Contains("walking in traffic")) { return $true }

    $hasPedestrian = $value.Contains("pedestrian") -or
        $value.Contains("person") -or
        $value.Contains("male") -or
        $value.Contains("female") -or
        $value.Contains("walker")
    if (!$hasPedestrian) { return $false }

    $hasRoadwayMovement = $value.Contains("crossing") -or
        $value.Contains("walking") -or
        $value.Contains("in the roadway") -or
        $value.Contains("in traffic")
    if (!$hasRoadwayMovement) { return $false }

    return $value.Contains("lanes of traffic") -or
        $value.Contains("lane of traffic") -or
        $value.Contains("interstate") -or
        $value.Contains("roadway") -or
        $value.Contains("northbound") -or
        $value.Contains("southbound") -or
        $value.Contains("eastbound") -or
        $value.Contains("westbound")
}

function Test-ParentEventGroupsCompatible {
    param([string]$Left, [string]$Right)

    if ($Left.Equals($Right, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $leftPolice = $Left -in @("violent_police", "property_police")
    $rightPolice = $Right -in @("violent_police", "property_police")
    return $leftPolice -and $rightPolice
}

function Get-ParentEventCluster {
    param([string]$Group)

    if ($Group -in @("violent_police", "property_police")) { return "police" }
    return $Group
}

function Get-ServerDerivedParentEventConflicts {
    param($Hypothesis, $AcceptedCallIds, $TranscriptsByCallId)

    $events = @($Hypothesis.events | Where-Object {
        $sourceIds = @(ConvertTo-LongValues $_.source_call_ids)
        @($sourceIds | Where-Object { $AcceptedCallIds -contains $_ }).Count -gt 0 -and
        @($_.spans).Count -gt 0 -and
        (Test-AnySpanGrounded -Spans $_.spans -TranscriptsByCallId $TranscriptsByCallId) -and
        (Test-PrimaryStrength ([string]$_.strength))
    } | ForEach-Object {
        $event = $_
        $sourceIds = @(ConvertTo-LongValues $event.source_call_ids | Where-Object { $AcceptedCallIds -contains $_ } | Sort-Object -Unique)
        [pscustomobject][ordered]@{
            group = Get-ParentEventGroup $event
            sourceCallIds = $sourceIds
            spans = @($event.spans | Where-Object { $AcceptedCallIds -contains [long]$_.call_id })
        }
    } | Where-Object { ![string]::IsNullOrWhiteSpace([string]$_.group) -and @($_.sourceCallIds).Count -gt 0 })

    for ($i = 0; $i -lt $events.Count; $i++) {
        for ($j = $i + 1; $j -lt $events.Count; $j++) {
            $left = $events[$i]
            $right = $events[$j]
            if (@($left.sourceCallIds | Where-Object { $right.sourceCallIds -contains $_ }).Count -gt 0) {
                continue
            }
            if (Test-ParentEventGroupsCompatible ([string]$left.group) ([string]$right.group)) {
                continue
            }

            return @([pscustomobject][ordered]@{
                conflict_type = "parent_event_conflict"
                call_ids = @($left.sourceCallIds + $right.sourceCallIds | Sort-Object -Unique)
                reason = "accepted calls contain incompatible parent event types: $($left.group) vs $($right.group)"
                spans = @($left.spans + $right.spans)
            })
        }
    }

    return @()
}

function Get-ServerDerivedTranscriptParentEventConflicts {
    param($AcceptedCallIds, $TranscriptsByCallId)

    $events = @()
    foreach ($item in @($AcceptedCallIds | Sort-Object -Unique)) {
        $callId = [long]$item
        if (!$TranscriptsByCallId.ContainsKey($callId)) {
            continue
        }
        $group = Get-TranscriptParentEventGroup ([string]$TranscriptsByCallId[$callId])
        if ([string]::IsNullOrWhiteSpace($group)) {
            continue
        }
        $events += [pscustomobject][ordered]@{
            group = $group
            sourceCallIds = @($callId)
            spans = @()
        }
    }

    for ($i = 0; $i -lt $events.Count; $i++) {
        for ($j = $i + 1; $j -lt $events.Count; $j++) {
            $left = $events[$i]
            $right = $events[$j]
            if (Test-ParentEventGroupsCompatible ([string]$left.group) ([string]$right.group)) {
                continue
            }

            return @([pscustomobject][ordered]@{
                conflict_type = "parent_event_conflict"
                call_ids = @($left.sourceCallIds + $right.sourceCallIds | Sort-Object -Unique)
                reason = "accepted transcripts contain incompatible parent event signals: $($left.group) vs $($right.group)"
                spans = @()
            })
        }
    }

    return @()
}

function Test-ContinuationWithCompatibleTranscriptEvent {
    param($Membership, $Hypothesis, $TranscriptsByCallId)

    if (([string]$Membership.role).ToLowerInvariant() -ne "continuation") {
        return $false
    }

    $callId = [long]$Membership.call_id
    if (!$TranscriptsByCallId.ContainsKey($callId)) {
        return $false
    }

    $group = Get-TranscriptParentEventGroup ([string]$TranscriptsByCallId[$callId])
    if ([string]::IsNullOrWhiteSpace($group)) {
        return $false
    }

    $modelGroups = @($Hypothesis.events | Where-Object {
        Test-PrimaryStrength ([string]$_.strength)
    } | ForEach-Object {
        Get-ParentEventGroup $_
    } | Where-Object {
        ![string]::IsNullOrWhiteSpace($_)
    } | Sort-Object -Unique)
    foreach ($modelGroup in $modelGroups) {
        if (Test-ParentEventGroupsCompatible ([string]$modelGroup) ([string]$group)) {
            return $true
        }
    }

    return $false
}

function Get-TokenSet {
    param([string]$Text)

    @([regex]::Matches(([string]$Text).ToLowerInvariant(), "[a-z0-9]+") | ForEach-Object { $_.Value } | Sort-Object -Unique)
}

function Normalize-VehicleKind {
    param([string]$Value)

    if ($Value -eq "chevrolet") { return "chevy" }
    if ($Value -eq "grey") { return "gray" }
    return $Value
}

function Get-VehicleClaims {
    param($AcceptedCallIds, $TranscriptsByCallId)

    $colors = @("black", "white", "red", "blue", "silver", "gray", "grey", "green", "gold", "tan", "brown")
    $kinds = @("ford", "chevy", "chevrolet", "toyota", "honda", "nissan", "dodge", "jeep", "subaru", "kia", "hyundai", "truck", "sedan", "suv", "van", "pickup", "wrangler", "silverado", "camry", "civic", "accord")
    $claims = @()
    foreach ($callId in @($AcceptedCallIds | Sort-Object -Unique)) {
        if (!$TranscriptsByCallId.ContainsKey([long]$callId)) {
            continue
        }
        $tokens = @(Get-TokenSet ([string]$TranscriptsByCallId[[long]$callId]))
        $color = @($colors | Where-Object { $tokens -contains $_ } | Select-Object -First 1)
        $kind = @($kinds | Where-Object { $tokens -contains $_ } | Select-Object -First 1)
        if ($color.Count -eq 0 -or $kind.Count -eq 0) {
            continue
        }

        $claims += [pscustomobject][ordered]@{
            callId = [long]$callId
            key = "$($color[0]) $(Normalize-VehicleKind $kind[0])"
            display = "$($color[0]) $($kind[0])"
        }
    }

    return $claims
}

function Get-ServerDerivedVehicleConflicts {
    param($AcceptedCallIds, $TranscriptsByCallId)

    $vehicles = @(Get-VehicleClaims -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId)
    for ($i = 0; $i -lt $vehicles.Count; $i++) {
        for ($j = $i + 1; $j -lt $vehicles.Count; $j++) {
            $left = $vehicles[$i]
            $right = $vehicles[$j]
            if ([string]$left.key -eq [string]$right.key) {
                continue
            }

            return @([pscustomobject][ordered]@{
                conflict_type = "vehicle_conflict"
                call_ids = @([long]$left.callId, [long]$right.callId)
                reason = "accepted calls contain incompatible vehicle descriptions: $($left.display) vs $($right.display)"
                spans = @()
            })
        }
    }

    return @()
}

function Test-MultiPatientLanguage {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    return $value.Contains("two patients") -or $value.Contains("multiple patients") -or $value.Contains("second patient") -or $value.Contains("another patient")
}

function Get-PersonAge {
    param([string]$Text)

    $match = [regex]::Match(([string]$Text).ToLowerInvariant(), "\b(?<age>\d{1,3})\s+year\s+old\b")
    if (!$match.Success) { return $null }
    $age = [int]$match.Groups["age"].Value
    if ($age -le 0 -or $age -ge 120) { return $null }
    return $age
}

function Get-PersonClaims {
    param($AcceptedCallIds, $TranscriptsByCallId)

    $claims = @()
    foreach ($callId in @($AcceptedCallIds | Sort-Object -Unique)) {
        if (!$TranscriptsByCallId.ContainsKey([long]$callId)) {
            continue
        }
        $text = ([string]$TranscriptsByCallId[[long]$callId]).ToLowerInvariant()
        $gender = if ($text.Contains("female")) { "female" } elseif ($text.Contains("male")) { "male" } else { "" }
        $age = Get-PersonAge $text
        if ([string]::IsNullOrWhiteSpace($gender) -or $null -eq $age) {
            continue
        }
        $claims += [pscustomobject][ordered]@{
            callId = [long]$callId
            key = "$age`:$gender"
            display = "$age-year-old $gender"
        }
    }

    return $claims
}

function Get-ServerDerivedPersonConflicts {
    param($AcceptedCallIds, $TranscriptsByCallId)

    $joined = (@($AcceptedCallIds | ForEach-Object { if ($TranscriptsByCallId.ContainsKey([long]$_)) { [string]$TranscriptsByCallId[[long]$_] } }) -join " ")
    if (Test-MultiPatientLanguage $joined) {
        return @()
    }

    $people = @(Get-PersonClaims -AcceptedCallIds $AcceptedCallIds -TranscriptsByCallId $TranscriptsByCallId)
    for ($i = 0; $i -lt $people.Count; $i++) {
        for ($j = $i + 1; $j -lt $people.Count; $j++) {
            $left = $people[$i]
            $right = $people[$j]
            if ([string]$left.key -eq [string]$right.key) {
                continue
            }

            return @([pscustomobject][ordered]@{
                conflict_type = "person_conflict"
                call_ids = @([long]$left.callId, [long]$right.callId)
                reason = "accepted calls contain incompatible patient/person descriptions: $($left.display) vs $($right.display)"
                spans = @()
            })
        }
    }

    return @()
}

function Clean-NarrativeText {
    param([string]$Text)

    return (([string]$Text) -replace "\s+", " ").Trim(" ", ".", ",", ";", ":")
}

function Limit-NarrativeText {
    param([string]$Text, [int]$Max)

    $clean = Clean-NarrativeText $Text
    if ($clean.Length -le $Max) { return $clean }
    return $clean.Substring(0, [Math]::Max(0, $Max - 1)).TrimEnd() + "."
}

function New-ServerNarrative {
    param($Hypothesis, $AcceptedCallIds, $PrimaryEvents, $GroundedNarrativeFacts, $TranscriptsByCallId)

    $eventText = ""
    foreach ($event in @($PrimaryEvents)) {
        foreach ($span in @($event.spans)) {
            if (($AcceptedCallIds -contains [long]$span.call_id) -and (Test-SpanGrounded -Span $span -TranscriptsByCallId $TranscriptsByCallId)) {
                $eventText = Clean-NarrativeText ([string]$span.text)
                break
            }
        }
        if (![string]::IsNullOrWhiteSpace($eventText)) { break }
    }
    if ([string]::IsNullOrWhiteSpace($eventText)) {
        $eventText = "Public safety incident"
    }

    $locationText = ""
    foreach ($location in @($Hypothesis.locations)) {
        $sourceIds = @(ConvertTo-LongValues $location.source_call_ids)
        if (@($sourceIds | Where-Object { $AcceptedCallIds -contains $_ }).Count -eq 0) {
            continue
        }
        if (@($location.spans).Count -gt 0 -and !(Test-AnySpanGrounded -Spans $location.spans -TranscriptsByCallId $TranscriptsByCallId)) {
            continue
        }
        $locationText = Clean-NarrativeText ([string]$location.display)
        if (![string]::IsNullOrWhiteSpace($locationText)) { break }
    }

    $title = (Clean-NarrativeText $eventText)
    if ($title.Length -gt 0) {
        $title = $title.Substring(0,1).ToUpperInvariant() + $title.Substring(1)
    }
    if (![string]::IsNullOrWhiteSpace($locationText) -and $title.IndexOf($locationText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        $title = "$title at $locationText"
    }

    $facts = @($GroundedNarrativeFacts |
        ForEach-Object { Clean-NarrativeText ([string]$_.text) } |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique |
        Select-Object -First 3)
    $detail = if ($facts.Count -eq 0) {
        "Accepted calls contain source-backed evidence for $eventText."
    }
    else {
        "Accepted calls contain source-backed evidence: $($facts -join '; ')."
    }

    [pscustomobject][ordered]@{
        title = Limit-NarrativeText $title 120
        detail = Limit-NarrativeText $detail 420
    }
}

function Test-UrgentPublicSafetySignal {
    param([string]$Text)

    $value = ([string]$Text).ToLowerInvariant()
    return (Test-MedicalEmergencySignal $value) -or
        $value.Contains("shots fired") -or
        $value.Contains("stabbing") -or
        $value.Contains("assault") -or
        $value.Contains("crash") -or
        $value.Contains("wreck") -or
        $value.Contains("fire") -or
        $value.Contains("alarm") -or
        $value.Contains("weapon")
}

function Test-RoutineStandaloneActivity {
    param($AcceptedCallIds, $TranscriptsByCallId)

    $text = (@($AcceptedCallIds | Sort-Object -Unique | ForEach-Object {
        $callId = [long]$_
        if ($TranscriptsByCallId.ContainsKey($callId)) { [string]$TranscriptsByCallId[$callId] }
    }) -join " ").ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($text)) { return $false }

    $nonEmergencyTransport = $text.Contains("non-emergency") -and
        ($text.Contains("facility") -or $text.Contains("hospital") -or $text.Contains("transferred") -or $text.Contains("transfer") -or $text.Contains("orders") -or $text.Contains("eta") -or $text.Contains("ea"))
    if ($nonEmergencyTransport) { return $true }

    $transitOperations = ($text.Contains("supervisor") -or $text.Contains("driver") -or $text.Contains("schedule") -or $text.Contains("computer") -or $text.Contains("same issue")) -and
        ($text.Contains("not letting me") -or $text.Contains("switch over") -or $text.Contains("on time") -or $text.Contains("same schedule"))
    if ($transitOperations) { return $true }

    $noiseComplaint = ($text.Contains("music") -or $text.Contains("noise")) -and
        ($text.Contains("refuse") -or $text.Contains("information")) -and
        !(Test-UrgentPublicSafetySignal $text)
    if ($noiseComplaint) { return $true }

    $vehicleMeetup = ($text.Contains("requesting to meet") -or $text.Contains("eta")) -and
        ($text.Contains("stolen") -or $text.Contains("taken off")) -and
        !$text.Contains("just occurred") -and
        !$text.Contains("in progress")
    if ($vehicleMeetup) { return $true }

    $liftAssist = $text.Contains("lift assist") -and
        !(Test-MedicalEmergencySignal $text) -and
        !$text.Contains("fall") -and
        !$text.Contains("injur")
    if ($liftAssist) { return $true }

    $trafficStop = ($text.Contains("traffic stop") -or $text.Contains("vehicle stop")) -and
        !(Test-UrgentPublicSafetySignal $text)
    return $trafficStop
}

function Test-IncidentDispatchOrRequestLanguage {
    param([string]$Text)

    $value = [string]$Text
    return $value.Contains("respond", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("copy", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("caller reports", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("caller advised", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("rp is advising", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("rp advising", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("reports of", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("requesting", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("we're sending", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("sending an", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("sending a", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("dispatched", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("on a 911", [StringComparison]::OrdinalIgnoreCase)
}

function Test-PoliceEmergencySignal {
    param([string]$Text)

    $value = [string]$Text
    return $value.Contains("shots fired", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("stabbing", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("tabbing", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("fight call", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("assault", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("domestic disorder", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("domestic disturbance", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("suicid", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("kill herself", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("kill himself", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("possibly armed", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("with a gun", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("gun in his hand", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("gun in her hand", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("pointing the gun", [StringComparison]::OrdinalIgnoreCase) -or
        $value.Contains("pointed the gun", [StringComparison]::OrdinalIgnoreCase)
}

function Test-BareCode73Signal {
    param([string]$Transcript, [string]$PrimarySignal)

    if (!([string]$PrimarySignal).Contains("code 73", [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $withoutCode = ([string]$Transcript).Replace("code 73", "", [StringComparison]::OrdinalIgnoreCase)
    return !(Test-MedicalEmergencySignal $withoutCode) -and
        @(Get-TranscriptLocationKeys $Transcript).Count -eq 0 -and
        !(Test-IncidentDispatchOrRequestLanguage $Transcript)
}

function Test-HighInformationEventSignal {
    param([string]$Group, [string]$Transcript, [string]$PrimarySignal)

    if ($Group.Equals("medical", [StringComparison]::OrdinalIgnoreCase)) {
        return (Test-MedicalEmergencySignal $Transcript) -and !(Test-BareCode73Signal -Transcript $Transcript -PrimarySignal $PrimarySignal)
    }
    if ($Group.Equals("traffic", [StringComparison]::OrdinalIgnoreCase)) {
        return (Test-TrafficIncidentSignal $Transcript) -or (Test-PedestrianTrafficHazardSignal $Transcript)
    }
    if ($Group.Equals("fire", [StringComparison]::OrdinalIgnoreCase)) { return Test-FireEventSignal $Transcript }
    if ($Group.Equals("violent_police", [StringComparison]::OrdinalIgnoreCase)) { return Test-PoliceEmergencySignal $Transcript }
    if ($Group.Equals("property_police", [StringComparison]::OrdinalIgnoreCase)) {
        $value = [string]$Transcript
        return $value.Contains("burglary", [StringComparison]::OrdinalIgnoreCase) -or
            $value.Contains("theft", [StringComparison]::OrdinalIgnoreCase) -or
            $value.Contains("stolen", [StringComparison]::OrdinalIgnoreCase)
    }

    return $Group -in @("rescue", "animal", "service_call")
}

function Test-LowInformationStandalonePrimarySignal {
    param([string]$Group, [string]$Transcript, [string]$PrimarySignal)

    if (Test-BareCode73Signal -Transcript $Transcript -PrimarySignal $PrimarySignal) { return $true }
    if ($Group.Equals("violent_police", [StringComparison]::OrdinalIgnoreCase) -and
        $PrimarySignal.Equals("gun", [StringComparison]::OrdinalIgnoreCase) -and
        !(Test-PoliceEmergencySignal $Transcript)) { return $true }
    return $false
}

function Test-PersistenceReadiness {
    param($AcceptedCallIds, $PrimaryEvents, $TranscriptsByCallId)

    $reasons = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($AcceptedCallIds | Sort-Object -Unique)) {
        $callId = [long]$item
        $transcript = if ($TranscriptsByCallId.ContainsKey($callId)) { [string]$TranscriptsByCallId[$callId] } else { "" }
        $group = Get-TranscriptParentEventGroup $transcript
        $span = if (![string]::IsNullOrWhiteSpace($group)) { Get-PrimarySignalSpan -CallId $callId -Transcript $transcript -Group $group } else { $null }
        $modelHasGroundedPrimary = $false
        foreach ($event in @($PrimaryEvents)) {
            foreach ($eventSpan in @($event.spans)) {
                if ([long]$eventSpan.call_id -eq $callId -and (Test-SpanGrounded -Span $eventSpan -TranscriptsByCallId $TranscriptsByCallId)) {
                    $modelHasGroundedPrimary = $true
                    break
                }
            }
            if ($modelHasGroundedPrimary) { break }
        }
        if ($null -eq $span -and !$modelHasGroundedPrimary) { continue }

        $primarySignal = if ($null -ne $span) { [string]$span.text } else { "" }
        $hasLocation = @(Get-TranscriptLocationKeys $transcript).Count -gt 0
        $hasDispatchOrRequest = Test-IncidentDispatchOrRequestLanguage $transcript
        $hasHighInformationEvent = Test-HighInformationEventSignal -Group $group -Transcript $transcript -PrimarySignal $primarySignal
        $weakSignal = Test-LowInformationStandalonePrimarySignal -Group $group -Transcript $transcript -PrimarySignal $primarySignal
        if (!$weakSignal -and ($hasDispatchOrRequest -or $hasLocation -or $hasHighInformationEvent)) {
            return [pscustomobject][ordered]@{ ready = $true; reasons = @() }
        }

        $pendingReason = if ($weakSignal) {
            "primary signal is too terse or status-like to create an incident without later corroboration"
        }
        else {
            "primary signal lacks dispatch/request language, concrete location, or high-information emergency detail"
        }
        $reasons.Add("pending call ${callId}: $pendingReason")
    }

    $allReasons = @("candidate remains pending because no accepted call is a complete incident anchor yet") + @($reasons)
    return [pscustomobject][ordered]@{ ready = $false; reasons = $allReasons }
}

function Get-PrimarySignalSpan {
    param([long]$CallId, [string]$Transcript, [string]$Group)

    $phrases = switch ($Group) {
        "medical" { @("heart attack", "bar attack", "another onset", "code 73", "DOA", "DOI call", "difficulty breathing", "possible stroke", "unresponsive", "unconscious", "diabetic emergency", "chest pain", "chest tanks") }
        "fire" { @("commercial structure fire", "structure fire", "automatic fire alarm", "fire alarm", "on fire", "oxide detection", "carbon monoxide") }
        "violent_police" { @("shots fired", "shots for our call", "stabbing", "tabbing", "fight call", "possibly armed", "assault", "suicidal", "kill herself", "kill himself", "firearm", "gun") }
        "property_police" { @("stolen firearm", "stolen vehicle", "hit the property", "mailbox", "trash can", "property damage", "stolen", "burglary", "theft") }
        "traffic" { @("crash", "wreck", "accident", "MVC", "MVA", "NVC", "NVA", "entrapment", "lanes are blocked", "both lanes", "road hazard", "crossing all lanes of traffic", "crossing lanes of traffic", "walking in traffic", "in the roadway") }
        "animal" { @("aggressive dog", "aggressive dogs", "dogs", "humane") }
        "service_call" { @("911 hang", "911 call", "caller hung up", "hung up") }
        default { @() }
    }

    foreach ($phrase in @($phrases)) {
        $index = $Transcript.IndexOf($phrase, [StringComparison]::OrdinalIgnoreCase)
        if ($index -lt 0) {
            continue
        }
        if ($phrase.Equals("gun", [StringComparison]::OrdinalIgnoreCase) -and !(Test-ContainsWord $Transcript "gun")) {
            continue
        }

        $text = $Transcript.Substring($index, $phrase.Length)
        return [pscustomobject][ordered]@{
            call_id = $CallId
            start_char = $index
            end_char = $index + $phrase.Length
            text = $text
        }
    }

    return $null
}

function Get-SourceBackedPrimaryEvents {
    param($Hypothesis, $AcceptedCallIds, $TranscriptsByCallId, $Reasons)

    $groups = @($Hypothesis.events | Where-Object {
        Test-PrimaryStrength ([string]$_.strength)
    } | ForEach-Object {
        Get-ParentEventGroup $_
    } | Where-Object {
        ![string]::IsNullOrWhiteSpace($_)
    } | Sort-Object -Unique)
    if ($groups.Count -eq 0) {
        $groups = @($AcceptedCallIds | Sort-Object -Unique | ForEach-Object {
            $callId = [long]$_
            if ($TranscriptsByCallId.ContainsKey($callId)) {
                Get-TranscriptParentEventGroup ([string]$TranscriptsByCallId[$callId])
            }
        } | Where-Object {
            ![string]::IsNullOrWhiteSpace($_)
        } | Sort-Object -Unique)
    }

    $derived = @()
    foreach ($group in $groups) {
        $spans = @()
        foreach ($item in @($AcceptedCallIds | Sort-Object -Unique)) {
            $callId = [long]$item
            if (!$TranscriptsByCallId.ContainsKey($callId)) {
                continue
            }
            $span = Get-PrimarySignalSpan -CallId $callId -Transcript ([string]$TranscriptsByCallId[$callId]) -Group $group
            if ($null -ne $span) {
                $spans += $span
            }
        }
        if ($spans.Count -eq 0) {
            continue
        }

        $derived += [pscustomobject][ordered]@{
            event_class = "derived_$group"
            event_subtype = $group
            strength = "strong"
            source_call_ids = @($spans | ForEach-Object { [long]$_.call_id } | Sort-Object -Unique)
            spans = $spans
        }
    }

    if ($derived.Count -gt 0) {
        $Reasons.Add("server derived source-backed primary event evidence from accepted transcripts")
    }

    return $derived
}

function Get-NarrativeFactsFromPrimaryEvents {
    param($PrimaryEvents, $AcceptedCallIds, $TranscriptsByCallId)

    @($PrimaryEvents | ForEach-Object { $_.spans } | Where-Object {
        $null -ne $_ -and
        ($AcceptedCallIds -contains [long]$_.call_id) -and
        (Test-SpanGrounded -Span $_ -TranscriptsByCallId $TranscriptsByCallId)
    } | Select-Object -First 3 | ForEach-Object {
        [pscustomobject][ordered]@{
            kind = "event"
            text = [string]$_.text
            spans = @($_)
        }
    })
}

function Get-Category {
    param([string]$EventClass)
    $value = if ($null -eq $EventClass) { "" } else { $EventClass.ToLowerInvariant() }
    if ($value.Contains("medical") -or $value.Contains("ems")) { return "ems" }
    if ($value.Contains("shoot") -or $value.Contains("shot") -or $value.Contains("stab") -or $value.Contains("assault") -or $value.Contains("weapon") -or $value.Contains("police")) { return "police" }
    if ($value.Contains("traffic") -or $value.Contains("crash") -or $value.Contains("road")) { return "traffic" }
    if ($value.Contains("structure_fire") -or $value.Contains("fire_alarm") -or (Test-FireEventSignal $value) -or $value.Contains("hazard")) { return "fire" }
    return "other"
}

function Convert-HypothesisToDecision {
    param($Case, $Hypothesis, $TranscriptsByCallId)

    $reasons = New-Object System.Collections.Generic.List[string]
    $spanErrors = @(Test-HypothesisSpans $Hypothesis $TranscriptsByCallId)
    if ($spanErrors.Count -gt 0) {
        $reasons.Add("ignored unsupported optional spans: $($spanErrors.Count)")
    }

    $groundedEventSourceCallIds = @($Hypothesis.events |
        Where-Object { (Test-PrimaryStrength ([string]$_.strength)) -and @($_.spans).Count -gt 0 -and (Test-AnySpanGrounded -Spans $_.spans -TranscriptsByCallId $TranscriptsByCallId) } |
        ForEach-Object { $_.source_call_ids } |
        ForEach-Object { [long]$_ } |
        Sort-Object -Unique)
    $groundedNarrativeSourceCallIds = @($Hypothesis.narrative.facts |
        ForEach-Object { $_.spans } |
        Where-Object { $null -ne $_ -and (Test-SpanGrounded -Span $_ -TranscriptsByCallId $TranscriptsByCallId) } |
        ForEach-Object { [long]$_.call_id } |
        Sort-Object -Unique)
    $groundedHypothesisSourceCallIds = @(
        $Hypothesis.events |
            ForEach-Object { $_.spans }
        $Hypothesis.locations |
            ForEach-Object { $_.spans }
        $Hypothesis.membership |
            ForEach-Object { $_.spans }
        $Hypothesis.narrative.facts |
            ForEach-Object { $_.spans }
    ) | Where-Object {
        $null -ne $_ -and (Test-SpanGrounded -Span $_ -TranscriptsByCallId $TranscriptsByCallId)
    } | ForEach-Object {
        [long]$_.call_id
    } | Sort-Object -Unique

    $accepted = @($Hypothesis.membership |
        Where-Object {
            (
                (([string]$_.decision).ToLowerInvariant() -in @("accept", "accepted", "retain", "retained", "supporting") -and
                    ([string]$_.role).ToLowerInvariant() -notin @("routine", "routine_status", "unrelated", "conflicting", "conflict")) -or
                (([string]$_.decision).ToLowerInvariant() -notin @("reject", "rejected", "hold") -and
                    (($groundedNarrativeSourceCallIds -contains [long]$_.call_id) -or
                    ($groundedHypothesisSourceCallIds -contains [long]$_.call_id)) -and
                    ([string]$_.role).ToLowerInvariant() -notin @("routine", "routine_status", "conflicting", "conflict") -and
                    ([string]$_.role).ToLowerInvariant() -ne "unrelated")
            ) -and
            ((@($_.spans).Count -eq 0 -and ((([string]$_.role).ToLowerInvariant() -eq "primary_event") -or ($groundedEventSourceCallIds -contains [long]$_.call_id) -or (Test-ContinuationWithCompatibleTranscriptEvent -Membership $_ -Hypothesis $Hypothesis -TranscriptsByCallId $TranscriptsByCallId))) -or
                (Test-AnySpanGrounded -Spans $_.spans -TranscriptsByCallId $TranscriptsByCallId) -or
                ($groundedEventSourceCallIds -contains [long]$_.call_id))
        } |
        ForEach-Object { [long]$_.call_id } |
        Sort-Object -Unique)
    $accepted = @(Add-ServerRecognizedInitialDispatchCalls -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId -Reasons $reasons)
    $accepted = @(Add-ServerRecognizedContinuationCalls -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId -Reasons $reasons)
    if ($accepted.Count -eq 0) {
        $accepted = @(Add-ServerRecognizedPrimaryEventCalls -Hypothesis $Hypothesis -TranscriptsByCallId $TranscriptsByCallId -Reasons $reasons)
    }
    if ($accepted.Count -eq 0) {
        $reasons.Add("no accepted event-member calls")
        return [pscustomobject][ordered]@{
            auditId = [long]$Case.auditId
            decision = "shadow_reject"
            incidentKey = "shadow"
            acceptedCallIds = @()
            rejectedCallIds = @($Case.callIds | ForEach-Object { [long]$_ })
            title = ""
            detail = ""
            category = "other"
            reasons = @($reasons)
            conflicts = @($Hypothesis.conflicts)
        }
    }

    $blockingConflicts = @($Hypothesis.conflicts | Where-Object {
        $conflictCallIds = @(ConvertTo-LongValues $_.call_ids | Sort-Object -Unique)
        (Test-ServerRecognizedBlockingConflict $_) -and
        $conflictCallIds.Count -ge 2 -and
        (@($_.spans).Count -eq 0 -or (Test-AnySpanGrounded -Spans $_.spans -TranscriptsByCallId $TranscriptsByCallId))
    })
    $locationConflicts = @(Get-ServerDerivedLocationConflicts -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
    $blockingConflicts += $locationConflicts
    if ($locationConflicts.Count -eq 0) {
        $blockingConflicts += @(Get-ServerDerivedTranscriptLocationConflicts -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
    }
    $blockingConflicts += @(Get-ServerDerivedParentEventConflicts -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
    $blockingConflicts += @(Get-ServerDerivedTranscriptParentEventConflicts -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
    $blockingConflicts += @(Get-ServerDerivedVehicleConflicts -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
    $blockingConflicts += @(Get-ServerDerivedPersonConflicts -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
    if (@($blockingConflicts | Where-Object { ([string]$_.conflict_type).Equals("parent_event_conflict", [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        $prunedAccepted = @(Select-DominantParentEventGroupCalls -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId -Reasons $reasons)
        if ($prunedAccepted.Count -gt 0 -and $prunedAccepted.Count -lt $accepted.Count) {
            $accepted = $prunedAccepted
            $blockingConflicts = @($Hypothesis.conflicts | Where-Object {
                $conflictCallIds = @(ConvertTo-LongValues $_.call_ids | Sort-Object -Unique)
                (Test-ServerRecognizedBlockingConflict $_) -and
                $conflictCallIds.Count -ge 2 -and
                (@($_.spans).Count -eq 0 -or (Test-AnySpanGrounded -Spans $_.spans -TranscriptsByCallId $TranscriptsByCallId))
            })
            $locationConflicts = @(Get-ServerDerivedLocationConflicts -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
            $blockingConflicts += $locationConflicts
            if ($locationConflicts.Count -eq 0) {
                $blockingConflicts += @(Get-ServerDerivedTranscriptLocationConflicts -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
            }
            $blockingConflicts += @(Get-ServerDerivedParentEventConflicts -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
            $blockingConflicts += @(Get-ServerDerivedTranscriptParentEventConflicts -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
            $blockingConflicts += @(Get-ServerDerivedVehicleConflicts -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
            $blockingConflicts += @(Get-ServerDerivedPersonConflicts -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
        }
    }
    if ($blockingConflicts.Count -gt 0) {
        $reasons.Add("hypothesis contains blocking conflicts")
        return [pscustomobject][ordered]@{
            auditId = [long]$Case.auditId
            decision = "shadow_reject"
            incidentKey = "shadow"
            acceptedCallIds = @()
            rejectedCallIds = @($Case.callIds | ForEach-Object { [long]$_ })
            title = ""
            detail = ""
            category = "other"
            reasons = @($reasons)
            conflicts = $blockingConflicts
        }
    }

    if (Test-RoutineStandaloneActivity -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId) {
        $reasons.Add("accepted calls contain only routine, transport, administrative, or non-emergency service traffic")
        return [pscustomobject][ordered]@{
            auditId = [long]$Case.auditId
            decision = "shadow_reject"
            incidentKey = "shadow"
            acceptedCallIds = @()
            rejectedCallIds = @($Case.callIds | ForEach-Object { [long]$_ })
            title = ""
            detail = ""
            category = "other"
            reasons = @($reasons)
            conflicts = @($Hypothesis.conflicts)
        }
    }

    $primaryEvents = @($Hypothesis.events | Where-Object {
        $sourceIds = @($_.source_call_ids | ForEach-Object { [long]$_ })
        @($_.spans).Count -gt 0 -and
        (Test-AnySpanGrounded -Spans $_.spans -TranscriptsByCallId $TranscriptsByCallId) -and
        (Test-PrimaryStrength ([string]$_.strength)) -and
        !(Test-RoutineOrAdministrativePrimaryEvent $_) -and
        @($sourceIds | Where-Object { $accepted -contains $_ }).Count -gt 0
    })
    $derivedPrimaryEvents = @()
    if ($primaryEvents.Count -eq 0) {
        $derivedPrimaryEvents = @(Get-SourceBackedPrimaryEvents -Hypothesis $Hypothesis -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId -Reasons $reasons)
        $primaryEvents = $derivedPrimaryEvents
    }
    if ($primaryEvents.Count -eq 0) {
        $reasons.Add("no accepted call has source-backed primary event evidence")
        return [pscustomobject][ordered]@{
            auditId = [long]$Case.auditId
            decision = "shadow_reject"
            incidentKey = "shadow"
            acceptedCallIds = @()
            rejectedCallIds = @($Case.callIds | ForEach-Object { [long]$_ })
            title = ""
            detail = ""
            category = "other"
            reasons = @($reasons)
            conflicts = @($Hypothesis.conflicts)
        }
    }

    $groundedNarrativeFacts = @($Hypothesis.narrative.facts | Where-Object {
        @($_.spans).Count -gt 0 -and (Test-AnySpanGrounded -Spans $_.spans -TranscriptsByCallId $TranscriptsByCallId)
    })
    if ($groundedNarrativeFacts.Count -eq 0) {
        $groundedNarrativeFacts = @(Get-NarrativeFactsFromPrimaryEvents -PrimaryEvents $primaryEvents -AcceptedCallIds $accepted -TranscriptsByCallId $TranscriptsByCallId)
    }
    if ($groundedNarrativeFacts.Count -eq 0) {
        $reasons.Add("narrative has no source-backed facts")
        $accepted = @()
    }
    if ($accepted.Count -eq 0) {
        return [pscustomobject][ordered]@{
            auditId = [long]$Case.auditId
            decision = "shadow_reject"
            incidentKey = "shadow"
            acceptedCallIds = @()
            rejectedCallIds = @($Case.callIds | ForEach-Object { [long]$_ })
            title = ""
            detail = ""
            category = "other"
            reasons = @($reasons)
            conflicts = @($Hypothesis.conflicts)
        }
    }

    $category = Get-Category ([string]$primaryEvents[0].event_class)
    $narrative = New-ServerNarrative -Hypothesis $Hypothesis -AcceptedCallIds $accepted -PrimaryEvents $primaryEvents -GroundedNarrativeFacts $groundedNarrativeFacts -TranscriptsByCallId $TranscriptsByCallId
    $readiness = Test-PersistenceReadiness -AcceptedCallIds $accepted -PrimaryEvents $primaryEvents -TranscriptsByCallId $TranscriptsByCallId
    if (!$readiness.ready) {
        foreach ($reason in @($readiness.reasons)) {
            $reasons.Add([string]$reason)
        }
        return [pscustomobject][ordered]@{
            auditId = [long]$Case.auditId
            decision = "shadow_pending"
            incidentKey = "shadow"
            acceptedCallIds = @()
            pendingCallIds = $accepted
            rejectedCallIds = @($Case.callIds | ForEach-Object { [long]$_ } | Where-Object { $accepted -notcontains $_ })
            title = [string]$narrative.title
            detail = [string]$narrative.detail
            category = $category
            reasons = @($reasons)
            conflicts = @($Hypothesis.conflicts)
        }
    }

    $reasons.Add("accepted by v2 shadow guardrails: spans, primary event evidence, conflicts, and narrative facts are grounded")
    return [pscustomobject][ordered]@{
        auditId = [long]$Case.auditId
        decision = "shadow_accept"
        incidentKey = "shadow"
        acceptedCallIds = $accepted
        pendingCallIds = @()
        rejectedCallIds = @($Case.callIds | ForEach-Object { [long]$_ } | Where-Object { $accepted -notcontains $_ })
        title = [string]$narrative.title
        detail = [string]$narrative.detail
        category = $category
        reasons = @($reasons)
        conflicts = @()
    }
}

function Invoke-HypothesisRequest {
    param($Case, $CaseCalls)

    $endpoint = ($OpenAiBaseUrl.TrimEnd('/')) + "/chat/completions"
    $prompt = New-UserPrompt $Case $CaseCalls
    $responseFormatAllowed = !$NoResponseFormat
    while ($true) {
    $body = @{
        model = $Model
        temperature = 0.0
        max_tokens = $MaxTokens
        messages = @(
            @{
                role = "system"
                content = "You extract structured incident evidence from public safety radio transcripts. You predict claims with exact source spans. The server, not you, owns persistence, incident identity, final membership, and conflict enforcement."
            },
            @{
                role = "user"
                content = $prompt
            }
        )
    }
    if ($responseFormatAllowed) {
        $body.response_format = New-V2ResponseFormat
    }

    $headers = @{}
    if (![string]::IsNullOrWhiteSpace($ApiKey)) {
        $headers["Authorization"] = "Bearer $ApiKey"
    }

    $payload = $body | ConvertTo-Json -Depth 100
    try {
        $response = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $headers -ContentType "application/json" -Body $payload -TimeoutSec $TimeoutSeconds
    }
    catch {
        $bodyText = ""
        if ($_.Exception.Response -and $_.Exception.Response.GetResponseStream()) {
            $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
            $bodyText = $reader.ReadToEnd()
        }
        if ($responseFormatAllowed -and $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 400) {
            $responseFormatAllowed = $false
            continue
        }
        if (![string]::IsNullOrWhiteSpace($bodyText)) {
            throw "$($_.Exception.Message): $bodyText"
        }
        throw
    }
    $content = [string]$response.choices[0].message.content
    $json = Get-JsonContent $content
    try {
        return $json | ConvertFrom-Json
    }
    catch {
        return Invoke-JsonRepairRequest -BrokenJson $json -Endpoint $endpoint -Headers $headers
    }
    }
}

function Invoke-JsonRepairRequest {
    param(
        [string]$BrokenJson,
        [string]$Endpoint,
        $Headers
    )

    $body = @{
        model = $Model
        temperature = 0.0
        max_tokens = $MaxTokens
        messages = @(
            @{
                role = "system"
                content = "Repair invalid JSON. Return only valid JSON. Preserve field names and values where possible. Do not add commentary."
            },
            @{
                role = "user"
                content = "Repair this JSON so it parses. It must have top-level property hypotheses.`n`n$BrokenJson"
            }
        )
    }

    $payload = $body | ConvertTo-Json -Depth 100
    $response = Invoke-RestMethod -Method Post -Uri $Endpoint -Headers $Headers -ContentType "application/json" -Body $payload -TimeoutSec $TimeoutSeconds
    $content = [string]$response.choices[0].message.content
    $json = Get-JsonContent $content
    return $json | ConvertFrom-Json
}

if ($requestedAuditIds.Count -gt 0) {
    $caseRows = @($requestedAuditIds | ForEach-Object {
        $id = $_
        $corpus.cases | Where-Object { [long]$_.auditId -eq $id } | Select-Object -First 1
    } | Where-Object { $null -ne $_ -and @(Get-CaseCalls $_).Count -gt 0 })
}
else {
    $caseRows = @($corpus.cases | Where-Object {
        (($IncludeAcceptedCases -and $_.accepted) -or ($IncludeRejectedCases -and !$_.accepted)) -and
        @(Get-CaseCalls $_).Count -gt 0
    } | Select-Object -First $MaxCases)
}

$decisions = @()
$runErrors = @()
$rawHypotheses = @()
$existingHypothesesByAuditId = @{}
if (![string]::IsNullOrWhiteSpace($ExistingHypothesesPath)) {
    if (!(Test-Path $ExistingHypothesesPath)) {
        throw "Existing hypotheses artifact not found: $ExistingHypothesesPath"
    }

    $existingArtifact = Get-Content -Path $ExistingHypothesesPath -Raw | ConvertFrom-Json
    foreach ($row in @($existingArtifact.rawHypotheses)) {
        if ($null -ne $row.auditId) {
            $items = New-Object System.Collections.Generic.List[object]
            foreach ($hypothesis in @($row.hypotheses)) {
                if ($null -ne $hypothesis -and $hypothesis.PSObject.Properties.Name -contains "events") {
                    $items.Add($hypothesis)
                }
                elseif ($null -ne $hypothesis) {
                    foreach ($nested in @($hypothesis)) {
                        if ($null -ne $nested -and $nested.PSObject.Properties.Name -contains "events") {
                            $items.Add($nested)
                        }
                    }
                }
            }
            $existingHypothesesByAuditId[[long]$row.auditId] = @($items.ToArray())
        }
    }
}
$generated = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$outFile = Join-Path $outputFullPath "incident-v2-decisions-$generated.json"

function Write-DecisionArtifact {
    $payload = [ordered]@{
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        corpusPath = $CorpusPath
        openAiBaseUrl = $OpenAiBaseUrl
        model = $Model
        requestedAuditIds = @($requestedAuditIds)
        requestedCases = @($caseRows).Count
        decisionCount = @($decisions).Count
        errorCount = @($runErrors).Count
        decisions = @($decisions)
        errors = @($runErrors)
        rawHypotheses = @($rawHypotheses)
        sourceHypothesesPath = $ExistingHypothesesPath
    }

    $payload | ConvertTo-Json -Depth 100 | Set-Content -Path $outFile -Encoding UTF8
}

foreach ($case in $caseRows) {
    $caseCalls = @(Get-CaseCalls $case)
    $transcriptsByCallId = @{}
    foreach ($call in $caseCalls) {
        $transcriptsByCallId[[long]$call.callId] = [string]$call.transcription
    }

    try {
        if ($existingHypothesesByAuditId.ContainsKey([long]$case.auditId)) {
            $hypotheses = @($existingHypothesesByAuditId[[long]$case.auditId] | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -contains "events" })
        }
        elseif (![string]::IsNullOrWhiteSpace($ExistingHypothesesPath) -and !$ReuseExistingHypothesesWhenAvailable) {
            $runErrors += [pscustomobject][ordered]@{
                auditId = [long]$case.auditId
                message = "no raw hypotheses found in existing artifact"
            }
            Write-DecisionArtifact
            continue
        }
        else {
            $result = Invoke-HypothesisRequest $case $caseCalls
            $hypotheses = @($result.hypotheses)
        }
        $hypotheses = @($hypotheses |
            ForEach-Object { Normalize-HypothesisShape $case $_ } |
            Where-Object { $null -ne $_ })
        $rawHypotheses += [pscustomobject][ordered]@{
            auditId = [long]$case.auditId
            hypotheses = $hypotheses
        }
        if ($hypotheses.Count -eq 0) {
            $decisions += [pscustomobject][ordered]@{
                auditId = [long]$case.auditId
                decision = "shadow_reject"
                incidentKey = "shadow"
                acceptedCallIds = @()
                rejectedCallIds = @($case.callIds | ForEach-Object { [long]$_ })
                title = ""
                detail = ""
                category = "other"
                reasons = @("model returned no structured incident hypotheses")
                conflicts = @()
            }
            continue
        }

        $candidateDecisions = @($hypotheses | ForEach-Object { Convert-HypothesisToDecision $case $_ $transcriptsByCallId })
        $accepted = @($candidateDecisions | Where-Object { $_.decision -eq "shadow_accept" } | Select-Object -First 1)
        if ($accepted.Count -gt 0) {
            $decisions += $accepted[0]
        }
        elseif (@($candidateDecisions | Where-Object { $_.decision -eq "shadow_pending" }).Count -gt 0) {
            $decisions += @($candidateDecisions | Where-Object { $_.decision -eq "shadow_pending" } | Select-Object -First 1)[0]
        }
        else {
            $decisions += $candidateDecisions[0]
        }
    }
    catch {
        $runErrors += [pscustomobject][ordered]@{
            auditId = [long]$case.auditId
            message = $_.Exception.Message
        }
    }
    Write-DecisionArtifact
}

Write-DecisionArtifact
Write-Output $outFile
