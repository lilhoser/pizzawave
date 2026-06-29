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
    [switch]$CreateStartupTask,
    [switch]$StartNow
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$serverSource = Join-Path $repoRoot "scripts\remote_faster_whisper_server.py"
$launcherSource = Join-Path $repoRoot "scripts\start-remote-faster-whisper.ps1"
if (!(Test-Path $serverSource)) {
    throw "Server script not found: $serverSource"
}
if (!(Test-Path $launcherSource)) {
    throw "Launcher script not found: $launcherSource"
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
$venv = Join-Path $InstallRoot ".venv"
$serverTarget = Join-Path $InstallRoot "remote_faster_whisper_server.py"
$startScript = Join-Path $InstallRoot "start-remote-faster-whisper.ps1"
$logDir = Join-Path $InstallRoot "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Copy-Item -LiteralPath $serverSource -Destination $serverTarget -Force
Copy-Item -LiteralPath $launcherSource -Destination $startScript -Force

if (!(Test-Path (Join-Path $venv "Scripts\python.exe"))) {
    python -m venv $venv
}

$python = Join-Path $venv "Scripts\python.exe"
& $python -m pip install --upgrade pip
& $python -m pip install "fastapi==0.115.6" "uvicorn[standard]==0.34.0" "python-multipart==0.0.20" "faster-whisper==1.2.1"

$startArgs = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-WindowStyle", "Hidden",
    "-File", "`"$startScript`"",
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
if (![string]::IsNullOrWhiteSpace($ApiKey)) {
    $startArgs += @("-ApiKey", "`"$ApiKey`"")
}
if ($ConditionOnPreviousText) {
    $startArgs += "-ConditionOnPreviousText"
}
if ($VadFilter) {
    $startArgs += "-VadFilter"
}

if ($CreateStartupTask) {
    try {
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument ($startArgs -join " ")
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 2)
        $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest
        Register-ScheduledTask -TaskName "PizzaWave Remote faster-whisper" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description "PizzaWave GPU transcription server" -Force | Out-Null
    }
    catch {
        Write-Warning "Startup task was not created: $($_.Exception.Message)"
    }
}

if ($StartNow) {
    Start-Process -FilePath "powershell.exe" -ArgumentList $startArgs -WindowStyle Hidden
}

Write-Host "Remote faster-whisper server installed at $InstallRoot"
Write-Host "Start script: $startScript"
Write-Host "Health URL: http://localhost:$Port/health"
if ($CreateStartupTask) {
    Write-Host "Startup task: PizzaWave Remote faster-whisper"
}
