param(
    [string]$CorpusPath = "",
    [string]$V2DecisionsPath = "",
    [string]$OutputDirectory = "artifacts/incident-replay-corpus"
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

$corpus = Get-Content -Path $CorpusPath -Raw | ConvertFrom-Json

function ConvertTo-LongArray {
    param($Values)

    @($Values | ForEach-Object {
        if ($null -eq $_) {
            return
        }

        [long]$_
    })
}

$v2ByAuditId = @{}
if (![string]::IsNullOrWhiteSpace($V2DecisionsPath)) {
    if (!(Test-Path $V2DecisionsPath)) {
        throw "V2 decisions file not found: $V2DecisionsPath"
    }

    $loaded = Get-Content -Path $V2DecisionsPath -Raw | ConvertFrom-Json
    $rows = if ($null -ne $loaded.decisions) { @($loaded.decisions) } else { @($loaded) }
    foreach ($row in $rows) {
        if ($null -eq $row.auditId) {
            throw "V2 decision row is missing auditId."
        }

        $v2ByAuditId[[long]$row.auditId] = $row
    }
}

$diffs = @($corpus.cases | ForEach-Object {
    $case = $_
    $v1CallIds = if ($case.accepted) { @($case.callIds | ForEach-Object { [long]$_ }) } else { @() }
    if (!$v2ByAuditId.ContainsKey([long]$case.auditId)) {
        return [pscustomobject][ordered]@{
            auditId = $case.auditId
            failureClass = $case.failureClass
            incidentKey = $case.incidentKey
            v1Accepted = $case.accepted
            v1CallIds = $v1CallIds
            v1Reason = $case.reason
            v2Status = "missing_hypothesis"
            v2Accepted = $null
            v2AcceptedCallIds = @()
            v2PendingCallIds = @()
            v2RejectedCallIds = @()
            diffKind = "v2_missing"
            callsOnlyInV1 = $v1CallIds
            callsOnlyInV2 = @()
            v2Reasons = @("no structured v2 hypothesis decision supplied for replay case")
        }
    }

    $v2 = $v2ByAuditId[[long]$case.auditId]
    $v2Accepted = $v2.decision -eq "shadow_accept"
    $v2Pending = $v2.decision -eq "shadow_pending"
    $pendingValues = if ($null -ne $v2.pendingCallIds) { $v2.pendingCallIds } else { $v2.pending_call_ids }
    $v2AcceptedCallIds = if ($v2Accepted) { ConvertTo-LongArray $v2.acceptedCallIds } else { @() }
    $v2PendingCallIds = ConvertTo-LongArray $pendingValues
    $v2RejectedCallIds = ConvertTo-LongArray $v2.rejectedCallIds
    $onlyInV1 = @($v1CallIds | Where-Object { $v2AcceptedCallIds -notcontains $_ })
    $onlyInV2 = @($v2AcceptedCallIds | Where-Object { $v1CallIds -notcontains $_ })
    if ($v2Pending -and $case.accepted) {
        $diffKind = "v2_pending_v1_accept"
    }
    elseif ($v2Pending -and !$case.accepted) {
        $diffKind = "v2_pending_v1_reject"
    }
    elseif ($case.accepted -and !$v2Accepted) {
        $diffKind = "v2_rejects_v1_accept"
    }
    elseif (!$case.accepted -and $v2Accepted) {
        $diffKind = "v2_accepts_v1_reject"
    }
    elseif (!$case.accepted -and !$v2Accepted) {
        $diffKind = "both_reject"
    }
    elseif ($onlyInV1.Count -gt 0 -or $onlyInV2.Count -gt 0) {
        $diffKind = "same_acceptance_call_diff"
    }
    else {
        $diffKind = "same_acceptance_no_call_diff"
    }

    [pscustomobject][ordered]@{
        auditId = $case.auditId
        failureClass = $case.failureClass
        incidentKey = $case.incidentKey
        v1Accepted = $case.accepted
        v1CallIds = $v1CallIds
        v1Reason = $case.reason
        v2Status = $v2.decision
        v2Accepted = $v2Accepted
        v2AcceptedCallIds = $v2AcceptedCallIds
        v2PendingCallIds = $v2PendingCallIds
        v2RejectedCallIds = $v2RejectedCallIds
        diffKind = $diffKind
        callsOnlyInV1 = $onlyInV1
        callsOnlyInV2 = $onlyInV2
        v2Reasons = @($v2.reasons)
    }
})

$summary = [ordered]@{
    v1Accepted = @($diffs | Where-Object { $_.v1Accepted }).Count
    v1Rejected = @($diffs | Where-Object { !$_.v1Accepted }).Count
    v2Accepted = @($diffs | Where-Object { $_.v2Accepted }).Count
    v2Rejected = @($diffs | Where-Object { $_.v2Accepted -eq $false -and $_.v2Status -ne "shadow_pending" }).Count
    v2Pending = @($diffs | Where-Object { $_.v2Status -eq "shadow_pending" }).Count
    missingV2 = @($diffs | Where-Object { $_.diffKind -eq "v2_missing" }).Count
    sameAcceptance = @($diffs | Where-Object { $_.v2Status -ne "shadow_pending" -and $_.v2Accepted -eq $_.v1Accepted }).Count
    acceptanceChanged = @($diffs | Where-Object { $_.v2Status -ne "shadow_pending" -and $null -ne $_.v2Accepted -and $_.v2Accepted -ne $_.v1Accepted }).Count
    callSetChanged = @($diffs | Where-Object { @($_.callsOnlyInV1).Count -gt 0 -or @($_.callsOnlyInV2).Count -gt 0 }).Count
}

$generated = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$outFile = Join-Path $outputFullPath "incident-shadow-report-$generated.json"
$payload = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    corpusPath = $CorpusPath
    v2DecisionsPath = $V2DecisionsPath
    caseCount = @($diffs).Count
    summary = $summary
    diffs = $diffs
}

$payload | ConvertTo-Json -Depth 100 | Set-Content -Path $outFile -Encoding UTF8
Write-Output $outFile
