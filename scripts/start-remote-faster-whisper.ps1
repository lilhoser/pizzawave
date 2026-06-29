param(
    [string]$InstallRoot = "C:\pizzawave\remote-faster-whisper",
    [string]$Model = "small",
    [string]$HostName = "0.0.0.0",
    [int]$Port = 9187,
    [string]$Device = "cuda",
    [string]$ComputeType = "float16",
    [int]$CpuThreads = 4,
    [int]$Workers = 1,
    [string]$Language = "en",
    [int]$BeamSize = 5,
    [double]$Temperature = 0,
    [double]$RepetitionPenalty = 1.0,
    [int]$NoRepeatNgramSize = 0,
    [double]$CompressionRatioThreshold = 2.4,
    [double]$LogProbThreshold = -1.0,
    [double]$NoSpeechThreshold = 0.6,
    [string]$ApiKey = "",
    [int]$MaxUploadMb = 64,
    [switch]$ConditionOnPreviousText,
    [switch]$VadFilter,
    [switch]$Worker
)

$ErrorActionPreference = "Stop"

$LogDir = Join-Path $InstallRoot "logs"
$LauncherLog = Join-Path $LogDir "launcher.log"
$TranscriptLog = Join-Path $LogDir "launcher-transcript.log"
$ServerLog = Join-Path $LogDir "server.log"
$Python = Join-Path $InstallRoot ".venv\Scripts\python.exe"
$Server = Join-Path $InstallRoot "remote_faster_whisper_server.py"
$HealthUrl = "http://127.0.0.1:$Port/health"

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Write-LauncherLog {
    param([string]$Message)
    $line = "{0:o} {1}" -f (Get-Date), $Message
    Add-Content -Path $LauncherLog -Value $line -Encoding UTF8
}

function Test-ServerHealthy {
    try {
        $health = Invoke-RestMethod -TimeoutSec 5 -Uri $HealthUrl
        return [bool]$health.ok
    }
    catch {
        return $false
    }
}

function New-WorkerArgumentList {
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-WindowStyle", "Hidden",
        "-File", "`"$PSCommandPath`"",
        "-InstallRoot", "`"$InstallRoot`"",
        "-Model", "`"$Model`"",
        "-HostName", "`"$HostName`"",
        "-Port", "$Port",
        "-Device", "`"$Device`"",
        "-ComputeType", "`"$ComputeType`"",
        "-CpuThreads", "$CpuThreads",
        "-Workers", "$Workers",
        "-Language", "`"$Language`"",
        "-BeamSize", "$BeamSize",
        "-Temperature", "$Temperature",
        "-RepetitionPenalty", "$RepetitionPenalty",
        "-NoRepeatNgramSize", "$NoRepeatNgramSize",
        "-CompressionRatioThreshold", "$CompressionRatioThreshold",
        "-LogProbThreshold", "$LogProbThreshold",
        "-NoSpeechThreshold", "$NoSpeechThreshold",
        "-MaxUploadMb", "$MaxUploadMb"
    )
    if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
        $args += @("-ApiKey", "`"$ApiKey`"")
    }
    if ($ConditionOnPreviousText) {
        $args += "-ConditionOnPreviousText"
    }
    if ($VadFilter) {
        $args += "-VadFilter"
    }
    $args += "-Worker"
    return $args
}

if (Test-ServerHealthy) {
    Write-LauncherLog "Remote faster-whisper is already healthy at $HealthUrl; launcher exiting."
    exit 0
}

if (-not $Worker) {
    Write-LauncherLog "Scheduled task wrapper starting hidden worker."
    Start-Process -FilePath "powershell.exe" -ArgumentList (New-WorkerArgumentList) -WindowStyle Hidden -WorkingDirectory $InstallRoot
    Write-LauncherLog "Scheduled task wrapper exiting after hidden worker handoff."
    exit 0
}

try {
    Start-Transcript -Path $TranscriptLog -Append -Force | Out-Null
}
catch {
    Add-Content -Path $LauncherLog -Value ("{0:o} Start-Transcript failed: {1}" -f (Get-Date), $_.Exception.Message) -Encoding UTF8
}

try {
    Set-Location $InstallRoot
    $cudaPaths = @(
        "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0\bin\x64",
        "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0\bin",
        "C:\Program Files\NVIDIA\CUDNN\v9.8\bin",
        "C:\Program Files\NVIDIA\CUDNN\v9.8\bin\12.8",
        "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin"
    ) | Where-Object { Test-Path $_ }
    $currentPath = ($env:Path -split ';') | Where-Object { $_ }
    $env:Path = (($cudaPaths + $currentPath) | Select-Object -Unique) -join ';'

    Write-LauncherLog "Starting PizzaWave remote faster-whisper."
    Write-LauncherLog "User=$([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) PID=$PID Root=$InstallRoot"
    Write-LauncherLog "Python=$Python"
    Write-LauncherLog "Server=$Server"

    if (-not (Test-Path $Python)) {
        throw "Python executable was not found: $Python"
    }
    if (-not (Test-Path $Server)) {
        throw "Server script was not found: $Server"
    }

    $serverArgs = @(
        $Server,
        "--host", $HostName,
        "--port", "$Port",
        "--model", $Model,
        "--device", $Device,
        "--compute-type", $ComputeType,
        "--cpu-threads", "$CpuThreads",
        "--workers", "$Workers",
        "--language", $Language,
        "--beam-size", "$BeamSize",
        "--temperature", "$Temperature",
        "--repetition-penalty", "$RepetitionPenalty",
        "--no-repeat-ngram-size", "$NoRepeatNgramSize",
        "--compression-ratio-threshold", "$CompressionRatioThreshold",
        "--log-prob-threshold", "$LogProbThreshold",
        "--no-speech-threshold", "$NoSpeechThreshold",
        "--max-upload-mb", "$MaxUploadMb"
    )
    if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
        $serverArgs += @("--api-key", $ApiKey)
    }
    if ($ConditionOnPreviousText) {
        $serverArgs += "--condition-on-previous-text"
    }
    if ($VadFilter) {
        $serverArgs += "--vad-filter"
    }

    $maxAttempts = 6
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        Add-Content -Path $ServerLog -Value ("`n{0:o} launcher starting process PID={1} attempt={2}/{3}" -f (Get-Date), $PID, $attempt, $maxAttempts) -Encoding UTF8
        Write-LauncherLog "Starting server attempt $attempt of $maxAttempts."

        $quotedArgs = @($serverArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' })
        $cmdLine = '"' + $Python + '" ' + ($quotedArgs -join ' ') + ' >> "' + $ServerLog + '" 2>&1'
        try {
            & $env:ComSpec /d /c $cmdLine
            if ($null -ne $LASTEXITCODE) {
                $exitCode = $LASTEXITCODE
            }
            else {
                $exitCode = 0
            }
        }
        catch {
            $exitCode = 1
            Write-LauncherLog "Server attempt $attempt threw: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
            Add-Content -Path $ServerLog -Value ("{0:o} server attempt {1} threw: {2}: {3}" -f (Get-Date), $attempt, $_.Exception.GetType().FullName, $_.Exception.Message) -Encoding UTF8
        }

        Write-LauncherLog "Server attempt $attempt exited with code $exitCode."
        if ($exitCode -eq 0) {
            exit 0
        }
        if ($attempt -lt $maxAttempts) {
            Write-LauncherLog "Retrying in 60 seconds."
            Start-Sleep -Seconds 60
        }
    }

    Write-LauncherLog "Server failed after $maxAttempts attempts."
    exit 1
}
catch {
    Write-LauncherLog "Launcher failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    Add-Content -Path $ServerLog -Value ("{0:o} launcher failed: {1}: {2}" -f (Get-Date), $_.Exception.GetType().FullName, $_.Exception.Message) -Encoding UTF8
    exit 1
}
finally {
    try {
        Stop-Transcript | Out-Null
    }
    catch {
    }
}
