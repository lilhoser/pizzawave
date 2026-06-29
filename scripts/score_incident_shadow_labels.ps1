param(
    [string]$ReportPath = "",
    [string]$LabelsPath = "docs/incident-v2-replay-labels.json",
    [string]$OutputDirectory = "artifacts/incident-replay-corpus"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputFullPath = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Get-ChildItem -Path $outputFullPath -Filter "incident-shadow-report-*.json" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (!(Test-Path $ReportPath)) {
    throw "Report not found: $ReportPath"
}

$labelsFullPath = if ([System.IO.Path]::IsPathRooted($LabelsPath)) {
    $LabelsPath
}
else {
    Join-Path $root $LabelsPath
}

if (!(Test-Path $labelsFullPath)) {
    throw "Labels not found: $labelsFullPath"
}

function ConvertTo-LongArray {
    param($Values)
    $converted = New-Object System.Collections.Generic.List[long]
    foreach ($value in @($Values)) {
        if ($null -eq $value) {
            continue
        }

        try {
            $converted.Add([long]$value)
        }
        catch {
            continue
        }
    }

    return @($converted.ToArray())
}

function Test-SameLongSet {
    param($Left, $Right)
    $leftValues = @(ConvertTo-LongArray $Left | Sort-Object -Unique)
    $rightValues = @(ConvertTo-LongArray $Right | Sort-Object -Unique)
    if ($leftValues.Count -ne $rightValues.Count) {
        return $false
    }

    for ($i = 0; $i -lt $leftValues.Count; $i++) {
        if ($leftValues[$i] -ne $rightValues[$i]) {
            return $false
        }
    }

    return $true
}

$report = Get-Content -Path $ReportPath -Raw | ConvertFrom-Json
$labels = Get-Content -Path $labelsFullPath -Raw | ConvertFrom-Json

$diffsByAuditId = @{}
foreach ($diff in @($report.diffs)) {
    $diffsByAuditId[[long]$diff.auditId] = $diff
}

$rows = @()
foreach ($label in @($labels.cases)) {
    $auditId = [long]$label.audit_id
    $expected = [string]$label.expected_outcome
    $diff = $null
    if ($diffsByAuditId.ContainsKey($auditId)) {
        $diff = $diffsByAuditId[$auditId]
    }

    $actualOutcome = if ($null -eq $diff -or $null -eq $diff.v2Accepted) {
        "missing"
    }
    elseif ($diff.v2Accepted) {
        "accept"
    }
    elseif ($diff.v2Status -eq "shadow_pending") {
        "pending"
    }
    else {
        "reject"
    }

    $callSetExpected = $null -ne $label.expected_call_ids
    $callSetMatched = $true
    if ($callSetExpected -and $null -ne $diff) {
        $callSetMatched = Test-SameLongSet $diff.v2AcceptedCallIds $label.expected_call_ids
    }

    $scored = $expected -ne "review"
    $passed = $false
    if ($scored) {
        $passed = $actualOutcome -eq $expected -and (!$callSetExpected -or $callSetMatched)
    }

    $rows += [pscustomobject][ordered]@{
        auditId = $auditId
        label = [string]$label.label
        expectedOutcome = $expected
        actualOutcome = $actualOutcome
        scored = $scored
        passed = $passed
        expectedCallIds = if ($callSetExpected) { @(ConvertTo-LongArray $label.expected_call_ids) } else { @() }
        actualCallIds = if ($null -ne $diff) { @(ConvertTo-LongArray $diff.v2AcceptedCallIds) } else { @() }
        pendingCallIds = if ($null -ne $diff) { @(ConvertTo-LongArray $diff.v2PendingCallIds) } else { @() }
        diffKind = if ($null -ne $diff) { [string]$diff.diffKind } else { "missing_diff" }
        reasons = if ($null -ne $diff) { @($diff.v2Reasons) } else { @("report did not include this audit row") }
        rationale = [string]$label.rationale
    }
}

$scoredRows = @($rows | Where-Object { $_.scored })
$passedRows = @($scoredRows | Where-Object { $_.passed })
$reviewRows = @($rows | Where-Object { !$_.scored })

$summary = [ordered]@{
    labelSet = [string]$labels.label_set
    reportPath = $ReportPath
    labelsPath = $labelsFullPath
    totalLabels = @($rows).Count
    scoredLabels = @($scoredRows).Count
    reviewLabels = @($reviewRows).Count
    passed = @($passedRows).Count
    failed = @($scoredRows).Count - @($passedRows).Count
    passRate = if (@($scoredRows).Count -gt 0) { [Math]::Round(@($passedRows).Count / @($scoredRows).Count, 4) } else { 0 }
}

$generated = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$outFile = Join-Path $outputFullPath "incident-shadow-label-score-$generated.json"
$payload = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    summary = $summary
    rows = $rows
}

$payload | ConvertTo-Json -Depth 100 | Set-Content -Path $outFile -Encoding UTF8
Write-Output $outFile
