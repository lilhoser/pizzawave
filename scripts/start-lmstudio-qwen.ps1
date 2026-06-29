param(
    [string]$UserProfile = "C:\Users\lilhoser",
    [string]$ModelKey = "qwen3.6-35b-a3b",
    [string]$Identifier = "qwen/qwen3.6-35b-a3b@q8_0",
    [string]$BaseUrl = "http://127.0.0.1:1234",
    [int]$MaxRecoveryCycles = 4,
    [int]$ApiAttempts = 60,
    [int]$LoadAttempts = 60,
    [int]$ProbeTimeoutSeconds = 45,
    [switch]$RegisterStartupTask
)

$ErrorActionPreference = "Stop"

$LmStudioExe = Join-Path $UserProfile "AppData\Local\Programs\LM Studio\LM Studio.exe"
$Lms = Join-Path $UserProfile ".cache\lm-studio\bin\lms.exe"
$ModelsUrl = "$BaseUrl/v1/models"
$CompletionsUrl = "$BaseUrl/v1/chat/completions"
$LogDir = "C:\pizzawave\lmstudio\logs"
$LogPath = Join-Path $LogDir "boot-qwen.log"
$TaskName = "PizzaWave LM Studio Qwen boot loader"

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ssK"
    Add-Content -LiteralPath $LogPath -Value "$timestamp $Message"
}

function Set-LmStudioUserEnvironment {
    $env:USERPROFILE = $UserProfile
    $env:HOMEDRIVE = [System.IO.Path]::GetPathRoot($UserProfile).TrimEnd("\")
    $env:HOMEPATH = $UserProfile.Substring(2)
    $env:APPDATA = Join-Path $UserProfile "AppData\Roaming"
    $env:LOCALAPPDATA = Join-Path $UserProfile "AppData\Local"
    $env:TEMP = Join-Path $UserProfile "AppData\Local\Temp"
    $env:TMP = $env:TEMP
}

function Register-StartupTask {
    $scriptPath = if ($PSCommandPath) { $PSCommandPath } else { Join-Path (Get-Location) "load-qwen-lmstudio.ps1" }
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-WindowStyle", "Hidden",
        "-File", "`"$scriptPath`"",
        "-UserProfile", "`"$UserProfile`"",
        "-ModelKey", "`"$ModelKey`"",
        "-Identifier", "`"$Identifier`"",
        "-BaseUrl", "`"$BaseUrl`""
    ) -join " "
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $args
    $boot = New-ScheduledTaskTrigger -AtStartup
    $boot.Delay = "PT30S"
    $logon = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 45) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 2)
    try {
        $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($boot, $logon) -Settings $settings -Principal $principal -Description "Start LM Studio, load Qwen, and verify chat completions for PizzaWave." -Force -ErrorAction Stop | Out-Null
        Write-Log "Registered scheduled task '$TaskName' for boot and logon as SYSTEM."
    }
    catch {
        Write-Log "SYSTEM scheduled task registration failed: $($_.Exception.Message)"
        $userTrigger = New-ScheduledTaskTrigger -AtLogOn
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $userTrigger -Settings $settings -Description "Start LM Studio, load Qwen, and verify chat completions for PizzaWave." -Force | Out-Null
        Write-Log "Registered scheduled task '$TaskName' for current-user logon fallback."
    }
}

function Test-HttpReady {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri $ModelsUrl
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Test-QwenCompletion {
    try {
        $body = @{
            model = $Identifier
            messages = @(@{ role = "user"; content = "Reply with OK only." })
            temperature = 0
            max_tokens = 4
        } | ConvertTo-Json -Compress -Depth 8
        $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec $ProbeTimeoutSeconds -Uri $CompletionsUrl -Method Post -ContentType "application/json" -Body $body
        if ($response.StatusCode -ne 200) {
            Write-Log "Completion probe returned HTTP $($response.StatusCode)."
            return $false
        }

        $json = $response.Content | ConvertFrom-Json
        $content = ""
        if ($json.choices -and $json.choices.Count -gt 0 -and $json.choices[0].message -and $null -ne $json.choices[0].message.content) {
            $content = [string]$json.choices[0].message.content
        }
        $promptTokens = 0
        if ($json.usage -and $null -ne $json.usage.prompt_tokens) {
            $promptTokens = [int]$json.usage.prompt_tokens
        }
        $completionTokens = 0
        if ($json.usage -and $null -ne $json.usage.completion_tokens) {
            $completionTokens = [int]$json.usage.completion_tokens
        }
        $totalTokens = 0
        if ($json.usage -and $null -ne $json.usage.total_tokens) {
            $totalTokens = [int]$json.usage.total_tokens
        }
        if ([string]::IsNullOrWhiteSpace($content) -or ($completionTokens -le 0 -and $totalTokens -le 0)) {
            Write-Log "Completion probe returned no valid result. contentLength=$($content.Length) promptTokens=$promptTokens completionTokens=$completionTokens totalTokens=$totalTokens"
            return $false
        }

        Write-Log "Completion probe succeeded. content='$content' promptTokens=$promptTokens completionTokens=$completionTokens totalTokens=$totalTokens"
        return $true
    }
    catch {
        Write-Log "Completion probe failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
        return $false
    }
}

function Test-QwenLoaded {
    if (-not (Test-Path -LiteralPath $Lms)) {
        Write-Log "lms.exe not found at $Lms"
        return $false
    }

    try {
        $loaded = & $Lms ps --json | ConvertFrom-Json
        $matched = @($loaded | Where-Object { $_.identifier -eq $Identifier -or $_.modelKey -eq $ModelKey }).Count -gt 0
        if (-not $matched) {
            Write-Log "Qwen is not present in lms ps output."
            return $false
        }
        return $true
    }
    catch {
        Write-Log "lms ps failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
        return $false
    }
}

function Start-LmStudio {
    if (-not (Test-Path -LiteralPath $LmStudioExe)) {
        throw "LM Studio executable was not found: $LmStudioExe"
    }

    $running = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $LmStudioExe }

    if ($running) {
        Write-Log "LM Studio process already exists: $(@($running | Select-Object -ExpandProperty Id) -join ',')."
        return
    }

    Write-Log "Starting LM Studio headless service."
    Start-Process -FilePath $LmStudioExe -ArgumentList "--run-as-service" -WindowStyle Hidden | Out-Null
}

function Stop-LmStudioRelatedProcesses {
    Write-Log "Stopping LM Studio related processes for recovery."
    $processes = Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessName -in @("LM Studio", "lmlink-connector", "lms") -or
            ($_.Path -and ($_.Path -like "$UserProfile\AppData\Local\Programs\LM Studio\*" -or $_.Path -like "$UserProfile\.cache\lm-studio\*"))
        }

    foreach ($process in $processes) {
        try {
            Write-Log "Stopping process $($process.ProcessName) pid=$($process.Id)."
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
            Write-Log "Failed stopping pid=$($process.Id): $($_.Exception.Message)"
        }
    }
    Start-Sleep -Seconds 8
}

function Wait-ForApi {
    for ($attempt = 1; $attempt -le $ApiAttempts; $attempt++) {
        if (Test-HttpReady) {
            Write-Log "LM Studio API is ready after attempt $attempt."
            return $true
        }

        if ($attempt -eq 1 -or $attempt -eq 12 -or $attempt -eq 30) {
            Start-LmStudio
        }

        if (($attempt % 12) -eq 0) {
            Write-Log "LM Studio API still not ready after $attempt attempts."
        }

        Start-Sleep -Seconds 5
    }

    return $false
}

function Ensure-QwenLoaded {
    if (Test-QwenLoaded) {
        Write-Log "Qwen is present in lms ps as $Identifier."
        return $true
    }

    Write-Log "Loading $ModelKey as $Identifier."
    & $Lms load $ModelKey --identifier $Identifier --yes *>&1 | ForEach-Object { Write-Log $_ }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Write-Log "lms load exited with code $exitCode."
        return $false
    }

    for ($attempt = 1; $attempt -le $LoadAttempts; $attempt++) {
        if (Test-QwenLoaded) {
            Write-Log "Qwen became present in lms ps after load attempt $attempt."
            return $true
        }
        Start-Sleep -Seconds 5
    }

    Write-Log "lms load returned success but Qwen was not found in lms ps."
    return $false
}

Set-LmStudioUserEnvironment
if ($RegisterStartupTask) {
    Register-StartupTask
    exit 0
}

Write-Log "Qwen health check starting as $([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)."
if (-not (Test-Path -LiteralPath $Lms)) {
    Write-Log "lms.exe not found at $Lms"
    exit 2
}

for ($cycle = 1; $cycle -le $MaxRecoveryCycles; $cycle++) {
    Write-Log "Recovery cycle $cycle of $MaxRecoveryCycles."
    Start-LmStudio

    if ((Wait-ForApi) -and (Ensure-QwenLoaded) -and (Test-QwenCompletion)) {
        Write-Log "Qwen is loaded and completion-ready as $Identifier."
        exit 0
    }

    if ($cycle -lt $MaxRecoveryCycles) {
        Stop-LmStudioRelatedProcesses
    }
}

Write-Log "Qwen did not become completion-ready after $MaxRecoveryCycles recovery cycle(s)."
exit 4
