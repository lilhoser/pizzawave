param(
    [string]$BaseUrl = "http://192.168.1.173:8080",
    [int]$Hours = 8,
    [int]$Limit = 250,
    [string]$WatchlistPath = "docs/ot-quality-watchlist.md",
    [string]$OutputDirectory = "artifacts/incident-replay-corpus"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$watchlistFullPath = Join-Path $root $WatchlistPath
$outputFullPath = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null

function Get-WatchlistLabels {
    param([string]$Path)

    if (!(Test-Path $Path)) {
        return @()
    }

    $labels = @()
    $index = 0
    foreach ($line in Get-Content -Path $Path) {
        $trimmed = $line.Trim()
        if (!$trimmed.StartsWith("-")) {
            continue
        }

        $kind = $null
        if ($trimmed -match "Negative proof caught and fixed") { $kind = "negative_proof" }
        elseif ($trimmed -match "Proven improvement") { $kind = "proven_improvement" }
        elseif ($trimmed -match "Early clean signal|Continued clean signal") { $kind = "clean_signal" }
        elseif ($trimmed -match "Watch-only|watch-only") { $kind = "watch_only" }
        elseif ($trimmed -match "Still uncertain") { $kind = "uncertain" }
        if (!$kind) {
            continue
        }

        $index++
        $rows = [regex]::Matches($trimmed, "row[s]?\s+`?(?<id>\d{4,})`?", "IgnoreCase") |
            ForEach-Object { [long]$_.Groups["id"].Value } |
            Select-Object -Unique
        $incidents = [regex]::Matches($trimmed, "incident\s+`?(?<id>\d{3,})`?", "IgnoreCase") |
            ForEach-Object { [long]$_.Groups["id"].Value } |
            Select-Object -Unique
        $labelId = if ($rows.Count -gt 0) { "$kind`:row-$($rows[0])" } else { "$kind`:watchlist-$index" }
        $labels += [pscustomobject][ordered]@{
            labelId = $labelId
            kind = $kind
            auditRows = @($rows)
            incidentIds = @($incidents)
            text = $trimmed
        }
    }

    return $labels
}

function Get-ReplayFailureClass {
    param(
        [bool]$Accepted,
        [string]$Reason
    )

    if ($null -eq $Reason) {
        $Reason = ""
    }
    $value = $Reason.ToLowerInvariant()
    if ($value.Contains("semantic")) { return "semantic_only_join" }
    if ($value.Contains("unsupported narrative")) { return "unsupported_narrative" }
    if ($value.Contains("conflicting location") -or $value.Contains("concrete incident anchor") -or $value.Contains("different locations")) { return "location_conflict" }
    if ($value.Contains("transport") -or $value.Contains("hospital handoff") -or $value.Contains("medical_transport_context")) { return "transport_handoff" }
    if ($value.Contains("routine status") -or $value.Contains("operational chatter") -or $value.Contains("radio/test traffic")) { return "routine_status" }
    if ($value.Contains("verifier")) { return "verifier_selection" }
    if ($value.Contains("single-call incident lacks")) { return "single_call_gate" }
    if ($value.Contains("retained") -or $value.Contains("pruned") -or $value.Contains("excluded weak/unrelated")) { return "membership_pruning" }
    if ($value.Contains("accepted:create")) { return "accepted_create" }
    if ($value.Contains("accepted:update")) { return "accepted_update" }
    if ($Accepted) { return "accepted_other" }
    return "rejected_other"
}

$qualityUri = "$BaseUrl/api/v1/system/quality-check?hours=$Hours"
$auditUri = "$BaseUrl/api/v1/incidents/audit?hours=$Hours&limit=$Limit"

$quality = Invoke-RestMethod -Uri $qualityUri -TimeoutSec 30
$audit = Invoke-RestMethod -Uri $auditUri -TimeoutSec 30
$auditRows = @($audit | Select-Object -ExpandProperty value -ErrorAction SilentlyContinue)
if ($auditRows.Count -eq 0) {
    $auditRows = @($audit)
}
$cases = @($auditRows | ForEach-Object {
    $failureClass = Get-ReplayFailureClass -Accepted ([bool]$_.accepted) -Reason ([string]$_.reason)
    [pscustomobject][ordered]@{
        auditId = $_.id
        timestampUtc = $_.timestampUtc
        systemShortName = $_.systemShortName
        incidentKey = $_.incidentKey
        operation = $_.operation
        accepted = $_.accepted
        reason = $_.reason
        score = $_.score
        callIds = @($_.callIds)
        failureClass = $failureClass
        metadataJson = $_.metadataJson
    }
})
$uniqueCallIds = @($cases |
    ForEach-Object { @($_.callIds) } |
    Where-Object { $_ -ne $null } |
    ForEach-Object { [long]$_ } |
    Sort-Object -Unique)
$calls = @($uniqueCallIds | ForEach-Object {
    $callId = $_
    try {
        $call = Invoke-RestMethod -Uri "$BaseUrl/api/v1/calls/$callId" -TimeoutSec 20
        [pscustomobject][ordered]@{
            callId = $call.id
            startTime = $call.startTime
            stopTime = $call.stopTime
            systemShortName = $call.systemShortName
            talkgroup = $call.talkgroup
            talkgroupName = $call.talkgroupName
            category = $call.category
            transcription = $call.transcription
            transcriptionStatus = $call.transcriptionStatus
            qualityReason = $call.qualityReason
        }
    }
    catch {
        [pscustomobject][ordered]@{
            callId = $callId
            error = $_.Exception.Message
        }
    }
})
$reasonClusters = @($cases |
    Group-Object -Property failureClass, accepted |
    ForEach-Object {
        $first = $_.Group | Select-Object -First 1
        [pscustomobject][ordered]@{
            failureClass = $first.failureClass
            accepted = $first.accepted
            count = $_.Count
        }
    } |
    Sort-Object -Property @{ Expression = "count"; Descending = $true }, failureClass)
$labels = Get-WatchlistLabels -Path $watchlistFullPath

$generated = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$outFile = Join-Path $outputFullPath "incident-replay-corpus-$generated.json"

$payload = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    source = [ordered]@{
        baseUrl = $BaseUrl
        hours = $Hours
        auditLimit = $Limit
        qualityUri = $qualityUri
        auditUri = $auditUri
        watchlistPath = $WatchlistPath
    }
    qualityCheck = $quality
    auditRows = $auditRows
    baseline = [ordered]@{
        calls = $quality.calls.totalCalls
        aiRequests = $quality.ai.requests
        evidenceVerifierRuns = $quality.evidenceVerifier.runs
        incidents = $quality.incidents.incidents
        creates = $quality.incidents.creates
        updates = $quality.incidents.updates
        rejects = $quality.incidents.rejects
        aiFailures = $quality.ai.failures
        aiTruncated = $quality.ai.truncated
        verifierRetentionMismatches = $quality.evidenceVerifier.retentionMismatches
        averageVerifierTruncatedCalls = $quality.evidenceVerifier.averageTruncatedCalls
    }
    cases = $cases
    calls = $calls
    reasonClusters = $reasonClusters
    watchlistLabels = $labels
}

$payload | ConvertTo-Json -Depth 100 | Set-Content -Path $outFile -Encoding UTF8
Write-Output $outFile
