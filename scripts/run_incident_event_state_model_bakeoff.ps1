param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$BaseUrl = 'http://127.0.0.1:1234/v1',
    [string]$Model = 'incident-qwen36',
    [string[]]$BundleId = @(),
    [int]$MaxOutputTokens = 8192,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

$resolvedInput = (Resolve-Path -LiteralPath $InputPath).Path
if ($resolvedInput.IndexOf('heldout-sealed', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The model bakeoff runner refuses sealed held-out corpus paths.'
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
$corpus = Get-Content -Raw -LiteralPath $resolvedInput | ConvertFrom-Json
$selectedBundles = @($corpus.bundles)
if ($BundleId.Count -gt 0) {
    $requested = [Collections.Generic.HashSet[string]]::new(
        [string[]]$BundleId,
        [StringComparer]::Ordinal)
    $selectedBundles = @($selectedBundles | Where-Object { $requested.Contains($_.bundleId) })
    $missing = @($BundleId | Where-Object { $_ -notin $selectedBundles.bundleId })
    if ($missing.Count -gt 0) {
        throw "Unknown bundle id(s): $($missing -join ', ')"
    }
}

$promptIdentity = 'incident-event-state-transcript-only-v1'
$systemPrompt = @'
You are reviewing a bounded chronological set of radio-call transcript observations.
Infer zero or more possible real-world event accounts without using a fixed event taxonomy.

Important boundaries:
- Do not assume every observation belongs to an event.
- Do not infer membership from time proximity or similar wording alone.
- Transcripts may be incomplete, wrong, repetitive, or hallucinated by speech recognition.
- Keep genuinely unresolved interpretations unresolved.
- A hypothesis may contain one observation when that is all the evidence supports.
- Separate distinct events even when they occur near each other.
- Every claim, relationship, and alternative must cite exact transcript text that appears verbatim in the referenced transcript.
- Do not invent facts, identifiers, quotations, or observations.
- Uncertainty is a number from 0 to 1, where 0 means little uncertainty and 1 means highly uncertain.
- Return an empty hypotheses array when no event account is supported.
'@

$provenanceSchema = @{
    type = 'object'
    properties = @{
        observation_id = @{ type = 'string' }
        transcript_id = @{ type = 'string' }
        exact_quote = @{ type = 'string' }
    }
    required = @('observation_id', 'transcript_id', 'exact_quote')
    additionalProperties = $false
}

$schema = @{
    type = 'object'
    properties = @{
        hypotheses = @{
            type = 'array'
            items = @{
                type = 'object'
                properties = @{
                    hypothesis_id = @{ type = 'string' }
                    description = @{ type = 'string' }
                    uncertainty = @{ type = 'number'; minimum = 0; maximum = 1 }
                    observation_ids = @{ type = 'array'; minItems = 1; items = @{ type = 'string' } }
                    claims = @{
                        type = 'array'
                        items = @{
                            type = 'object'
                            properties = @{
                                claim_id = @{ type = 'string' }
                                statement = @{ type = 'string' }
                                uncertainty = @{ type = 'number'; minimum = 0; maximum = 1 }
                                provenance = @{ type = 'array'; minItems = 1; items = $provenanceSchema }
                            }
                            required = @('claim_id', 'statement', 'uncertainty', 'provenance')
                            additionalProperties = $false
                        }
                    }
                    relationships = @{
                        type = 'array'
                        items = @{
                            type = 'object'
                            properties = @{
                                relationship_id = @{ type = 'string' }
                                statement = @{ type = 'string' }
                                uncertainty = @{ type = 'number'; minimum = 0; maximum = 1 }
                                observation_ids = @{ type = 'array'; minItems = 1; items = @{ type = 'string' } }
                                provenance = @{ type = 'array'; minItems = 1; items = $provenanceSchema }
                            }
                            required = @('relationship_id', 'statement', 'uncertainty', 'observation_ids', 'provenance')
                            additionalProperties = $false
                        }
                    }
                    alternatives = @{
                        type = 'array'
                        items = @{
                            type = 'object'
                            properties = @{
                                alternative_id = @{ type = 'string' }
                                statement = @{ type = 'string' }
                                uncertainty = @{ type = 'number'; minimum = 0; maximum = 1 }
                                provenance = @{ type = 'array'; minItems = 1; items = $provenanceSchema }
                            }
                            required = @('alternative_id', 'statement', 'uncertainty', 'provenance')
                            additionalProperties = $false
                        }
                    }
                    unresolved_questions = @{ type = 'array'; items = @{ type = 'string' } }
                }
                required = @(
                    'hypothesis_id',
                    'description',
                    'uncertainty',
                    'observation_ids',
                    'claims',
                    'relationships',
                    'alternatives',
                    'unresolved_questions')
                additionalProperties = $false
            }
        }
    }
    required = @('hypotheses')
    additionalProperties = $false
}

function Get-Sha256([string]$Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($bytes) }
    finally { $algorithm.Dispose() }
    return ([BitConverter]::ToString($hash)).Replace('-', '')
}

function Test-Uncertainty($Value) {
    if ($null -eq $Value) { return $false }
    $number = 0.0
    if (-not [double]::TryParse(
            [string]$Value,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$number)) {
        return $false
    }
    return -not [double]::IsNaN($number) -and
        -not [double]::IsInfinity($number) -and
        $number -ge 0 -and
        $number -le 1
}

function Test-Provenance($Item, [hashtable]$Observations, [Collections.Generic.List[string]]$Errors, [string]$Owner) {
    if ($null -eq $Item) {
        $Errors.Add("$Owner has null provenance")
        return
    }
    $observationId = [string]$Item.observation_id
    $transcriptId = [string]$Item.transcript_id
    $exactQuote = [string]$Item.exact_quote
    if (-not $Observations.ContainsKey($observationId)) {
        $Errors.Add("$Owner references unknown observation '$observationId'")
        return
    }
    $transcript = @($Observations[$observationId].transcripts) |
        Where-Object { $_.transcriptId -ceq $transcriptId } |
        Select-Object -First 1
    if ($null -eq $transcript) {
        $Errors.Add("$Owner references unknown transcript '$transcriptId'")
        return
    }
    if ([string]::IsNullOrWhiteSpace($exactQuote) -or
        ([string]$transcript.text).IndexOf($exactQuote, [StringComparison]::Ordinal) -lt 0) {
        $Errors.Add("$Owner quote does not occur exactly in transcript '$transcriptId'")
    }
}

foreach ($bundle in $selectedBundles) {
    $observations = @($bundle.observations | ForEach-Object {
        [ordered]@{
            observation_id = $_.observationId
            observed_at_unix_seconds = $_.observedAtUnixSeconds
            transcripts = @($_.transcripts | ForEach-Object {
                [ordered]@{
                    transcript_id = $_.transcriptId
                    text = $_.text
                }
            })
        }
    })

    $inputDocument = [ordered]@{
        bundle_id = $bundle.bundleId
        observations = $observations
    }
    $inputJson = $inputDocument | ConvertTo-Json -Depth 20 -Compress
    $request = @{
        model = $Model
        messages = @(
            @{ role = 'system'; content = $systemPrompt },
            @{ role = 'user'; content = $inputJson })
        temperature = 0
        max_tokens = $MaxOutputTokens
        response_format = @{
            type = 'json_schema'
            json_schema = @{
                name = 'incident_event_state_proposal'
                strict = $true
                schema = $schema
            }
        }
    }
    $requestJson = $request | ConvertTo-Json -Depth 40 -Compress
    $runId = [Guid]::NewGuid().ToString('N')
    $artifactPath = Join-Path $resolvedOutput "$($bundle.bundleId)-$runId.json"
    $startedAt = [DateTimeOffset]::UtcNow
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $response = $null
    $requestError = ''
    try {
        $response = Invoke-RestMethod `
            -Uri "$($BaseUrl.TrimEnd('/'))/chat/completions" `
            -Method Post `
            -ContentType 'application/json' `
            -Body $requestJson `
            -TimeoutSec $TimeoutSeconds
    }
    catch {
        $requestError = $_.Exception.Message
    }
    $timer.Stop()

    $content = if ($null -ne $response) { [string]$response.choices[0].message.content } else { '' }

    # Preserve the expensive model response before parsing and validation. If the
    # runner itself fails below, this envelope still makes the run inspectable.
    $artifact = [ordered]@{
        run_id = $runId
        bundle_id = $bundle.bundleId
        started_at_utc = $startedAt.ToString('O')
        duration_milliseconds = $timer.ElapsedMilliseconds
        input_path = $resolvedInput
        corpus_sha256 = (Get-FileHash -LiteralPath $resolvedInput -Algorithm SHA256).Hash
        input_sha256 = Get-Sha256 $inputJson
        observation_count = $observations.Count
        prompt_identity = $promptIdentity
        prompt_sha256 = Get-Sha256 ($systemPrompt + "`n" + $inputJson)
        request_sha256 = Get-Sha256 $requestJson
        model_requested = $Model
        model_responded = if ($null -ne $response) { $response.model } else { '' }
        max_output_tokens = $MaxOutputTokens
        finish_reason = if ($null -ne $response) { $response.choices[0].finish_reason } else { '' }
        usage = if ($null -ne $response) { $response.usage } else { $null }
        request_error = $requestError
        validation_errors = @('validation did not complete')
        response_content = $content
        parsed_response = $null
    }
    $artifact | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath $artifactPath -Encoding utf8

    $parsed = $null
    $validationErrors = [Collections.Generic.List[string]]::new()
    if ([string]::IsNullOrWhiteSpace($content)) {
        $validationErrors.Add('response content is empty')
    }
    else {
        try { $parsed = $content | ConvertFrom-Json }
        catch { $validationErrors.Add("response content is not valid JSON: $($_.Exception.Message)") }
    }

    if ($null -ne $parsed) {
        $knownObservations = @{}
        foreach ($observation in @($bundle.observations)) {
            $knownObservations[[string]$observation.observationId] = $observation
        }
        foreach ($hypothesis in @($parsed.hypotheses)) {
            if (-not (Test-Uncertainty $hypothesis.uncertainty)) {
                $validationErrors.Add("hypothesis '$($hypothesis.hypothesis_id)' has invalid uncertainty")
            }
            if (@($hypothesis.observation_ids).Count -eq 0) {
                $validationErrors.Add("hypothesis '$($hypothesis.hypothesis_id)' must reference at least one observation")
            }
            foreach ($observationId in @($hypothesis.observation_ids)) {
                if (-not $knownObservations.ContainsKey([string]$observationId)) {
                    $validationErrors.Add("hypothesis '$($hypothesis.hypothesis_id)' references unknown observation '$observationId'")
                }
            }
            foreach ($claim in @($hypothesis.claims)) {
                if (-not (Test-Uncertainty $claim.uncertainty)) {
                    $validationErrors.Add("claim '$($claim.claim_id)' has invalid uncertainty")
                }
                if (@($claim.provenance).Count -eq 0) {
                    $validationErrors.Add("claim '$($claim.claim_id)' must include source provenance")
                }
                foreach ($provenance in @($claim.provenance)) {
                    Test-Provenance $provenance $knownObservations $validationErrors "claim '$($claim.claim_id)'"
                }
            }
            foreach ($relationship in @($hypothesis.relationships)) {
                if (-not (Test-Uncertainty $relationship.uncertainty)) {
                    $validationErrors.Add("relationship '$($relationship.relationship_id)' has invalid uncertainty")
                }
                if (@($relationship.observation_ids).Count -eq 0) {
                    $validationErrors.Add("relationship '$($relationship.relationship_id)' must reference at least one observation")
                }
                if (@($relationship.provenance).Count -eq 0) {
                    $validationErrors.Add("relationship '$($relationship.relationship_id)' must include source provenance")
                }
                foreach ($provenance in @($relationship.provenance)) {
                    Test-Provenance $provenance $knownObservations $validationErrors "relationship '$($relationship.relationship_id)'"
                }
            }
            foreach ($alternative in @($hypothesis.alternatives)) {
                if (-not (Test-Uncertainty $alternative.uncertainty)) {
                    $validationErrors.Add("alternative '$($alternative.alternative_id)' has invalid uncertainty")
                }
                if (@($alternative.provenance).Count -eq 0) {
                    $validationErrors.Add("alternative '$($alternative.alternative_id)' must include source provenance")
                }
                foreach ($provenance in @($alternative.provenance)) {
                    Test-Provenance $provenance $knownObservations $validationErrors "alternative '$($alternative.alternative_id)'"
                }
            }
        }
    }

    $artifact.validation_errors = @($validationErrors)
    $artifact.parsed_response = $parsed
    $artifact | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath $artifactPath -Encoding utf8
    [pscustomobject]@{
        bundle_id = $bundle.bundleId
        artifact = $artifactPath
        duration_milliseconds = $timer.ElapsedMilliseconds
        finish_reason = $artifact.finish_reason
        completion_tokens = $artifact.usage.completion_tokens
        reasoning_tokens = $artifact.usage.completion_tokens_details.reasoning_tokens
        validation_errors = $validationErrors.Count
        request_error = $requestError
    }
}
