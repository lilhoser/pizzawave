param(
    [string]$UserProfile = "C:\Users\lilhoser",
    [string]$ModelKey = "qwen3.6-35b-a3b",
    [string]$Identifier = "qwen/qwen3.6-35b-a3b@q8_0",
    [string]$BaseUrl = "http://127.0.0.1:1234",
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
    $env:TEMP = Join-Path $env:LOCALAPPDATA "Temp"
    $env:TMP = $env:TEMP
}

function Register-StartupTask {
    $args = @(
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy", "Bypass",
        "-WindowStyle", "Hidden",
        "-File", "`"$PSCommandPath`"",
        "-UserProfile", "`"$UserProfile`"",
        "-ModelKey", "`"$ModelKey`"",
        "-Identifier", "`"$Identifier`"",
        "-BaseUrl", "`"$BaseUrl`""
    ) -join " "
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $args
    $boot = New-ScheduledTaskTrigger -AtStartup
    $boot.Delay = "PT30S"
    $logon = New-ScheduledTaskTrigger -AtLogOn
    # An app update can stop inference without triggering Windows boot or logon.
    $watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) -RepetitionInterval (New-TimeSpan -Minutes 2)
    $watchdog.Repetition.StopAtDurationEnd = $false
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -StartWhenAvailable
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($boot, $logon, $watchdog) -Settings $settings -Principal $principal -Description "Restore PizzaWave Qwen at boot, at logon, and after LM Studio exits or updates." -Force | Out-Null
    Write-Log "Registered scheduled task '$TaskName' for boot, logon, and two-minute checks as SYSTEM."
}

function Invoke-Lms {
    param([string[]]$Arguments)

    # Windows PowerShell 5.1 can turn native stderr into a terminating
    # NativeCommandError even when the CLI exits successfully.
    $ErrorActionPreference = "Continue"
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & $Lms @Arguments 2>&1
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = @($output)
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

function Start-LmStudio {
    if (-not (Test-Path -LiteralPath $LmStudioExe)) {
        throw "LM Studio executable was not found: $LmStudioExe"
    }

    $running = Get-Process -Name "LM Studio" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $LmStudioExe }
    if (-not $running) {
        Write-Log "Starting LM Studio headless service."
        Start-Process -FilePath $LmStudioExe -ArgumentList "--run-as-service" -WindowStyle Hidden
    }
}

function Wait-ForApi {
    $endpoint = [uri]$BaseUrl
    for ($attempt = 1; $attempt -le $ApiAttempts; $attempt++) {
        if (Test-HttpReady) {
            return $true
        }

        if ($attempt -eq 1 -or $attempt -eq 12 -or $attempt -eq 30) {
            Start-LmStudio
        }

        if ($attempt -eq 6 -and $endpoint.IsLoopback) {
            # The app may already be running after an update with its HTTP server stopped.
            $result = Invoke-Lms -Arguments @("server", "start", "--port", ([string]$endpoint.Port))
            Write-Log "lms server start exited with code $($result.ExitCode): $($result.Output -join ' ')"
        }
        Start-Sleep -Seconds 5
    }
    return $false
}

function Test-QwenLoaded {
    $result = Invoke-Lms -Arguments @("ps", "--json")
    if ($result.ExitCode -ne 0) {
        throw "lms ps exited with code $($result.ExitCode): $($result.Output -join ' ')"
    }

    $json = ($result.Output | Where-Object { $_ -is [string] }) -join "`n"
    if (-not $json) {
        throw "lms ps returned no model inventory: $($result.Output -join ' ')"
    }

    $loaded = ConvertFrom-Json -InputObject $json
    return @($loaded | Where-Object { $_.identifier -eq $Identifier -and -not $_.deviceIdentifier }).Count -gt 0
}

function Load-Qwen {
    Write-Log "Loading $ModelKey as $Identifier."
    $result = Invoke-Lms -Arguments @("load", $ModelKey, "--identifier", $Identifier, "--yes")
    $result.Output | Where-Object { $_ -notmatch 'Loading.*\d+%' } | ForEach-Object { Write-Log ([string]$_) }
    if ($result.ExitCode -ne 0) {
        throw "lms load exited with code $($result.ExitCode)."
    }

    for ($attempt = 1; $attempt -le $LoadAttempts; $attempt++) {
        if (Test-QwenLoaded) {
            return
        }
        Start-Sleep -Seconds 5
    }
    throw "lms load returned success but no local instance appeared as $Identifier."
}

function Confirm-QwenCompletion {
    $body = @{
        model = $Identifier
        messages = @(@{ role = "user"; content = "Reply with OK only." })
        temperature = 0
        max_tokens = 12
    } | ConvertTo-Json -Compress -Depth 8
    $response = Invoke-RestMethod -TimeoutSec $ProbeTimeoutSeconds -Uri $CompletionsUrl -Method Post -ContentType "application/json" -Body $body
    if (-not $response.choices -or [string]::IsNullOrWhiteSpace($response.choices[0].message.content)) {
        throw "Completion probe returned no assistant content."
    }
    if ($response.usage.completion_tokens -le 0) {
        throw "Completion probe reported no generated tokens."
    }
    Write-Log "Qwen is loaded and completion-ready as $Identifier."
}

Set-LmStudioUserEnvironment
if ($RegisterStartupTask) {
    Register-StartupTask
    exit 0
}

if (-not (Test-Path -LiteralPath $Lms)) {
    Write-Log "lms.exe not found at $Lms"
    exit 2
}

try {
    if (-not (Wait-ForApi)) {
        throw "LM Studio API did not become ready."
    }

    if (-not (Test-QwenLoaded)) {
        Load-Qwen
        Confirm-QwenCompletion
    }
    exit 0
}
catch {
    Write-Log "Qwen recovery failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    exit 4
}
