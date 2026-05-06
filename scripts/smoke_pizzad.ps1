param(
    [string]$PublishDir = ".\artifacts\pizzad-engine-smoke",
    [int]$HttpPort = 18080,
    [int]$CallstreamPort = 19123,
    [switch]$UseRunningServer
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cfg = Join-Path $root "artifacts\pizzad-smoke.json"
$db = Join-Path $root "artifacts\pizzad-smoke.db"
$audio = Join-Path $root "artifacts\pizzad-smoke-audio"
$cache = Join-Path $root "artifacts\pizzad-smoke-cache"
$token = Join-Path $root "artifacts\pizzad-smoke.token"
$dll = Join-Path $root "$PublishDir\pizzad.dll"
$proc = $null

if (-not $UseRunningServer) {
    @{
        server = @{ httpBind = "127.0.0.1"; httpPort = $HttpPort }
        auth = @{ mode = "open"; readRequiresAuth = $false; writeRequiresAuth = $false; tokenFile = $token }
        storage = @{ databasePath = $db; audioRoot = $audio; importCacheRoot = $cache }
        ingest = @{ callstreamBind = "127.0.0.1"; callstreamPort = $CallstreamPort }
        transcription = @{ provider = "none"; analogSampleRate = 8000 }
        trunkRecorder = @{ configPath = "/etc/trunk-recorder/config.json"; logServiceName = "trunk-recorder"; healthWindowMinutes = 5 }
        sftpImport = @{ enabled = $false }
    } | ConvertTo-Json -Depth 8 | Set-Content $cfg

    $proc = Start-Process -FilePath "dotnet" -ArgumentList @($dll, "--config", $cfg) -WorkingDirectory $root -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
}

try {
    $client = [System.Net.Sockets.TcpClient]::new("127.0.0.1", $CallstreamPort)
    try {
        $stream = $client.GetStream()
        $meta = @{
            Source = 0
            Talkgroup = 5508
            PatchedTalkgroups = @()
            Frequency = 851937500.0
            SystemShortName = "smoke-system"
            CallId = 1
            StartTime = [int][double]::Parse((Get-Date -UFormat %s))
            StopTime = ([int][double]::Parse((Get-Date -UFormat %s)) + 3)
        } | ConvertTo-Json -Compress
        $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($meta)
        $sampleCount = 8000
        $pcm = New-Object byte[] ($sampleCount * 2)
        $payload = New-Object System.IO.MemoryStream
        $payload.Write([BitConverter]::GetBytes([int]0x415A5A50), 0, 4)
        $payload.Write([BitConverter]::GetBytes([int64]$jsonBytes.Length), 0, 8)
        $payload.Write([BitConverter]::GetBytes([int]$sampleCount), 0, 4)
        $payload.Write($jsonBytes, 0, $jsonBytes.Length)
        $payload.Write($pcm, 0, $pcm.Length)
        $bytes = $payload.ToArray()
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally {
        $client.Dispose()
    }
    Start-Sleep -Seconds 2
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$HttpPort/api/v1/health" -TimeoutSec 10
    $dashboard = Invoke-RestMethod -Uri "http://127.0.0.1:$HttpPort/api/v1/dashboard" -TimeoutSec 10
    $index = & curl.exe -fsS "http://127.0.0.1:$HttpPort/"
    [pscustomobject]@{
        Health = $health.status
        Kpis = $dashboard.kpis.Count
        Calls = ($dashboard.kpis | Where-Object { $_.label -eq "Total Calls" }).value
        HasWebBundle = [bool]($index -match "/assets/")
    }
}
finally {
    if ($proc -ne $null) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}
