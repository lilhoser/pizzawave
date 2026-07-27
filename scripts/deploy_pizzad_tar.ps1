param(
    [Parameter(Mandatory = $true)]
    [string]$HostName,

    [string]$SshKey = "",

    [string]$Rid = "",

    [string]$Configuration = "Release",

    [string]$RemoteTar = "/tmp/pizzad-direct-deploy.tar.gz",

    [switch]$WebOnly,

    [switch]$BackendOnly,

    [switch]$SkipNpmCi,

    [switch]$ForceBuild,

    [switch]$ForceDeploy,

    [switch]$NoRestart,

    [int]$HealthTimeoutSeconds = 20
)

$ErrorActionPreference = "Stop"
$overallTimer = [System.Diagnostics.Stopwatch]::StartNew()

if ($WebOnly -and $BackendOnly) {
    throw "-WebOnly and -BackendOnly are mutually exclusive."
}

if (($HostName -match "(^|@)(192\.168\.1\.173|omicrontheta)(:|$)") -and $Rid -and $Rid -ne "linux-x64") {
    throw "Refusing to deploy RID '$Rid' to OT ($HostName). OT is x86_64; use -Rid linux-x64."
}

function Get-SshArgs {
    $args = @("-o", "BatchMode=yes")
    if ($SshKey) {
        $args += @("-i", $SshKey, "-o", "IdentitiesOnly=yes")
    }
    return $args
}

function Assert-NativeCommand([string]$Name) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Invoke-TimedStage([string]$Name, [scriptblock]$Action) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    & $Action
    $timer.Stop()
    Write-Host ("[{0}] {1:n1}s" -f $Name, $timer.Elapsed.TotalSeconds)
}

function Get-StringHash([string]$Value) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-TreeHash([string]$BasePath, [string[]]$ExcludedDirectories = @()) {
    if (-not (Test-Path -LiteralPath $BasePath)) {
        return ""
    }

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath)
    $baseUri = [Uri]($baseFullPath.TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar)
    $excluded = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($directory in $ExcludedDirectories) {
        [void]$excluded.Add($directory)
    }

    $files = [System.Collections.Generic.List[string]]::new()
    $directories = [System.Collections.Generic.Stack[string]]::new()
    $directories.Push($baseFullPath)
    while ($directories.Count -gt 0) {
        $current = $directories.Pop()
        foreach ($directory in [System.IO.Directory]::EnumerateDirectories($current)) {
            if (-not $excluded.Contains([System.IO.Path]::GetFileName($directory))) {
                $directories.Push($directory)
            }
        }
        foreach ($file in [System.IO.Directory]::EnumerateFiles($current)) {
            $files.Add($file)
        }
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $sortedFiles = $files.ToArray()
        [Array]::Sort($sortedFiles, [System.StringComparer]::Ordinal)
        $lines = foreach ($file in $sortedFiles) {
            $relative = [Uri]::UnescapeDataString($baseUri.MakeRelativeUri([Uri]$file).ToString())
            $stream = [System.IO.File]::OpenRead($file)
            try {
                $fileHash = ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
            }
            finally {
                $stream.Dispose()
            }
            "$relative`t$fileHash"
        }
    }
    finally {
        $sha256.Dispose()
    }
    return Get-StringHash (($lines -join "`n") + "`n")
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Write-JsonFile([string]$Path, [object]$Value) {
    $parent = Split-Path -Parent $Path
    if ($parent) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    [System.IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
}

function ConvertTo-BashSingleQuoted([string]$Value) {
    return "'" + $Value.Replace("'", "'""'""'") + "'"
}

function Invoke-RemoteDeployScript([string]$ScriptBody) {
    $remoteScriptPath = Join-Path ([System.IO.Path]::GetTempPath()) "pizzad-direct-deploy-$([Guid]::NewGuid().ToString('N')).sh"
    $sshArgs = Get-SshArgs
    try {
        [System.IO.File]::WriteAllText($remoteScriptPath, $ScriptBody.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
        scp @sshArgs $remoteScriptPath "${HostName}:/tmp/pizzad-direct-deploy.sh"
        Assert-NativeCommand "scp deployment script"
        ssh @sshArgs $HostName "bash /tmp/pizzad-direct-deploy.sh"
        Assert-NativeCommand "remote deployment script"
    }
    finally {
        if (Test-Path $remoteScriptPath) {
            Remove-Item -LiteralPath $remoteScriptPath -Force
        }
    }
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$artifactsDir = Join-Path $root "artifacts"
$webTarPath = Join-Path $artifactsDir "pizzad-web.tar.gz"
$webDir = Join-Path $root "pizzad\web"
$webOutputDir = Join-Path $root "pizzad\wwwroot"
$webSourceMarkerPath = Join-Path $webOutputDir ".pizzawave-source-hash"
$webBuildStatePath = Join-Path $artifactsDir "pizzad-web-build-state.json"
$webDependencyStatePath = Join-Path $artifactsDir "pizzad-web-dependency-state.json"

New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$webSourceHash = Get-TreeHash $webDir @("node_modules", "dist")
$webOutputHash = Get-TreeHash $webOutputDir
$checkedInWebSourceHash = if (Test-Path -LiteralPath $webSourceMarkerPath) { (Get-Content -LiteralPath $webSourceMarkerPath -Raw).Trim() } else { "" }
$webBuildCurrent = $checkedInWebSourceHash -eq $webSourceHash -and
    (Test-Path -LiteralPath (Join-Path $webOutputDir "index.html"))

if (-not $BackendOnly -and ($ForceBuild -or -not $webBuildCurrent)) {
    Push-Location $webDir
    try {
        $packageLockPath = Join-Path $webDir "package-lock.json"
        $packageLockHash = if (Test-Path $packageLockPath) { (Get-FileHash $packageLockPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { "" }
        $dependencyState = Read-JsonFile $webDependencyStatePath
        $dependenciesCurrent = (Test-Path -LiteralPath (Join-Path $webDir "node_modules")) -and
            $dependencyState -and $dependencyState.packageLockHash -eq $packageLockHash

        if (-not $SkipNpmCi -and ($ForceBuild -or -not $dependenciesCurrent)) {
            Invoke-TimedStage "npm ci" {
                npm ci
                Assert-NativeCommand "npm ci"
            }
            Write-JsonFile $webDependencyStatePath ([ordered]@{
                packageLockHash = $packageLockHash
                completedAtUtc = [DateTime]::UtcNow.ToString("O")
            })
        }
        elseif (-not (Test-Path -LiteralPath (Join-Path $webDir "node_modules"))) {
            throw "Frontend dependencies are missing. Run without -SkipNpmCi."
        }

        Invoke-TimedStage "frontend build" {
            npm run build
            Assert-NativeCommand "frontend build"
        }
    }
    finally {
        Pop-Location
    }
    $builtWebSourceHash = if (Test-Path -LiteralPath $webSourceMarkerPath) { (Get-Content -LiteralPath $webSourceMarkerPath -Raw).Trim() } else { "" }
    if ($builtWebSourceHash -ne $webSourceHash) {
        throw "Frontend build did not produce a matching pizzad/wwwroot/.pizzawave-source-hash marker."
    }
    $webOutputHash = Get-TreeHash $webOutputDir
    Write-JsonFile $webBuildStatePath ([ordered]@{
        sourceHash = $webSourceHash
        outputHash = $webOutputHash
        completedAtUtc = [DateTime]::UtcNow.ToString("O")
    })
}
elseif (-not $BackendOnly) {
    Write-Host "[frontend build] reused verified wwwroot"
}

if (-not (Test-Path -LiteralPath (Join-Path $webOutputDir "index.html"))) {
    throw "Built frontend assets are missing from pizzad/wwwroot."
}
$webOutputHash = Get-TreeHash $webOutputDir

$sshArgs = Get-SshArgs

if (-not $Rid) {
    $remoteMachine = (ssh @sshArgs $HostName "uname -m").Trim()
    Assert-NativeCommand "remote architecture detection"
    $Rid = switch -Regex ($remoteMachine) {
        "^(aarch64|arm64)$" { "linux-arm64"; break }
        "^(x86_64|amd64)$" { "linux-x64"; break }
        default { throw "Unable to infer .NET RID from remote architecture '$remoteMachine'. Pass -Rid explicitly." }
    }
    Write-Host "Inferred RID '$Rid' from remote architecture '$remoteMachine'."
}

$publishDir = Join-Path $artifactsDir "pizzad-direct-$Rid"
$tarPath = Join-Path $artifactsDir "pizzad-direct-$Rid.tar"

if (($HostName -match "(^|@)(192\.168\.1\.173|omicrontheta)(:|$)") -and $Rid -ne "linux-x64") {
    throw "Refusing to deploy RID '$Rid' to OT ($HostName). OT is x86_64; use -Rid linux-x64."
}

$backendSourceHash = Get-TreeHash (Join-Path $root "pizzad") @("web", "wwwroot", "node_modules", "bin", "obj")
$remoteManifestText = ssh @sshArgs $HostName "sudo cat /opt/pizzawave/pizzad/.pizzawave-deploy.json 2>/dev/null || true"
Assert-NativeCommand "remote deployment-state read"
$remoteManifest = $null
if (-not [string]::IsNullOrWhiteSpace($remoteManifestText)) {
    try { $remoteManifest = $remoteManifestText | ConvertFrom-Json } catch { $remoteManifest = $null }
}

$effectiveWebOnly = [bool]$WebOnly
if (-not $WebOnly -and -not $BackendOnly -and $remoteManifest -and $remoteManifest.rid -eq $Rid -and $remoteManifest.backendHash -eq $backendSourceHash) {
    if (-not $ForceDeploy -and $remoteManifest.webHash -eq $webOutputHash) {
        Invoke-TimedStage "health check" {
            ssh @sshArgs $HostName "systemctl is-active pizzad >/dev/null && curl -fsS http://127.0.0.1:8080/api/v1/health >/dev/null"
            Assert-NativeCommand "remote health check"
        }
        $overallTimer.Stop()
        Write-Host ("No deployable changes; live artifact already matches. Total {0:n1}s." -f $overallTimer.Elapsed.TotalSeconds)
        return
    }
    $effectiveWebOnly = $true
    Write-Host "Auto-selected web-only deployment; backend inputs match the live artifact."
}

if ($effectiveWebOnly) {
    $manifestBackendHash = if ($remoteManifest) { [string]$remoteManifest.backendHash } else { "" }
    $manifestRid = if ($remoteManifest -and $remoteManifest.rid) { [string]$remoteManifest.rid } else { $Rid }
    $deployManifest = [ordered]@{
        schemaVersion = 1
        backendHash = $manifestBackendHash
        webHash = $webOutputHash
        rid = $manifestRid
        deployedAtUtc = [DateTime]::UtcNow.ToString("O")
    }
    $deployManifestJson = $deployManifest | ConvertTo-Json -Compress

    Invoke-TimedStage "web archive" {
        if (Test-Path $webTarPath) { Remove-Item -LiteralPath $webTarPath -Force }
        tar -czf $webTarPath -C (Join-Path $root "pizzad") wwwroot
        Assert-NativeCommand "web archive creation"
    }
    Invoke-TimedStage "web upload" {
        scp @sshArgs $webTarPath "${HostName}:$RemoteTar"
        Assert-NativeCommand "web archive upload"
    }

    $remoteScript = @"
REMOTE_TAR=$(ConvertTo-BashSingleQuoted $RemoteTar)
HEALTH_TIMEOUT_SECONDS=$(ConvertTo-BashSingleQuoted ([string]$HealthTimeoutSeconds))
DEPLOY_MANIFEST=$(ConvertTo-BashSingleQuoted $deployManifestJson)
set -e
work=/tmp/pizzad-web-deploy
maintenance_start=`$(date -u +%Y-%m-%dT%H:%M:%SZ)
maintenance_token=`$(sudo cat /etc/pizzawave/pizzad.token 2>/dev/null || true)
maintenance_response=""
if [ -n "`$maintenance_token" ]; then
  maintenance_response=`$(curl -fsS -H "Authorization: Bearer `$maintenance_token" -H 'Content-Type: application/json' -d '{"source":"deployment_helper","reason":"PizzaWave web deployment","excludeFromBaselines":true}' http://127.0.0.1:8080/api/v1/system/maintenance 2>/dev/null || true)
fi
maintenance_id=`$(printf '%s' "`$maintenance_response" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
rm -rf "`$work"
mkdir -p "`$work"
tar -xzf "`$REMOTE_TAR" -C "`$work"
sudo install -d -m 0755 /opt/pizzawave/pizzad/wwwroot
sudo rsync -a --delete "`$work"/wwwroot/ /opt/pizzawave/pizzad/wwwroot/
printf '%s\n' "`$DEPLOY_MANIFEST" | sudo tee /opt/pizzawave/pizzad/.pizzawave-deploy.json >/dev/null
sudo chown -R root:root /opt/pizzawave/pizzad/wwwroot /opt/pizzawave/pizzad/.pizzawave-deploy.json
systemctl is-active pizzad
deadline=`$((`$HEALTH_TIMEOUT_SECONDS * 2))
for i in `$(seq 1 "`$deadline"); do
  if curl -fsS http://127.0.0.1:8080/api/v1/health >/dev/null; then
    healthy=1
    break
  fi
  sleep 0.5
done
if [ "`$healthy" = "1" ] && [ -n "`$maintenance_token" ]; then
  if [ -n "`$maintenance_id" ]; then
    curl -fsS -X POST -H "Authorization: Bearer `$maintenance_token" http://127.0.0.1:8080/api/v1/system/maintenance/`$maintenance_id/close >/dev/null
  else
    maintenance_end=`$(date -u +%Y-%m-%dT%H:%M:%SZ)
    curl -fsS -H "Authorization: Bearer `$maintenance_token" -H 'Content-Type: application/json' -d "{\"source\":\"deployment_helper\",\"reason\":\"PizzaWave web deployment\",\"startUtc\":\"`$maintenance_start\",\"endUtc\":\"`$maintenance_end\",\"excludeFromBaselines\":true}" http://127.0.0.1:8080/api/v1/system/maintenance >/dev/null
  fi
  echo "pizzad web deploy complete"
  exit 0
fi
echo "pizzad health check did not pass within `$HEALTH_TIMEOUT_SECONDS seconds" >&2
exit 1
"@
    Invoke-TimedStage "remote web install" { Invoke-RemoteDeployScript $remoteScript }
    $overallTimer.Stop()
    Write-Host ("Web-only deploy complete in {0:n1}s; pizzad was not restarted." -f $overallTimer.Elapsed.TotalSeconds)
    return
}

$publishStatePath = Join-Path $artifactsDir "pizzad-direct-$Rid.publish-state.json"
$archiveStatePath = Join-Path $artifactsDir "pizzad-direct-$Rid.archive-state.json"
$publishInputHash = Get-StringHash "$backendSourceHash`n$webOutputHash`n$Rid`n$Configuration`n"
$publishState = Read-JsonFile $publishStatePath
$publishReady = -not $ForceBuild -and $publishState -and $publishState.inputHash -eq $publishInputHash -and
    (Test-Path -LiteralPath (Join-Path $publishDir "pizzad"))
$deployManifest = [ordered]@{
    schemaVersion = 1
    backendHash = $backendSourceHash
    webHash = $webOutputHash
    rid = $Rid
    deployedAtUtc = [DateTime]::UtcNow.ToString("O")
}

if (-not $publishReady) {
    $artifactsFullPath = [System.IO.Path]::GetFullPath($artifactsDir).TrimEnd("\") + "\"
    $publishFullPath = [System.IO.Path]::GetFullPath($publishDir)
    if (-not $publishFullPath.StartsWith($artifactsFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace publish directory outside artifacts: $publishFullPath"
    }
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Invoke-TimedStage "backend build" {
        dotnet build (Join-Path $root "pizzad\pizzad.csproj") `
            -c $Configuration `
            -r $Rid `
            --self-contained true `
            -p:SelfContained=true `
            -p:PublishSingleFile=false
        Assert-NativeCommand "backend build"
    }
    Invoke-TimedStage "backend publish" {
        dotnet publish (Join-Path $root "pizzad\pizzad.csproj") `
            -c $Configuration `
            -r $Rid `
            --self-contained true `
            -p:SelfContained=true `
            -p:PublishSingleFile=false `
            --no-build `
            --no-restore `
            -o $publishDir
        Assert-NativeCommand "backend publish"
    }
    Write-JsonFile (Join-Path $publishDir ".pizzawave-deploy.json") $deployManifest
    Write-JsonFile $publishStatePath ([ordered]@{
        inputHash = $publishInputHash
        completedAtUtc = [DateTime]::UtcNow.ToString("O")
    })
}
else {
    Write-Host "[backend publish] reused verified $Rid artifact"
}

$archiveState = Read-JsonFile $archiveStatePath
$archiveReady = -not $ForceBuild -and $archiveState -and $archiveState.inputHash -eq $publishInputHash -and (Test-Path -LiteralPath $tarPath)
if (-not $archiveReady) {
    Invoke-TimedStage "backend archive" {
        if (Test-Path $tarPath) { Remove-Item -LiteralPath $tarPath -Force }
        tar -cf $tarPath -C $publishDir .
        Assert-NativeCommand "backend archive creation"
    }
    Write-JsonFile $archiveStatePath ([ordered]@{
        inputHash = $publishInputHash
        completedAtUtc = [DateTime]::UtcNow.ToString("O")
    })
}
else {
    Write-Host "[backend archive] reused verified $Rid archive"
}

Invoke-TimedStage "backend upload" {
    scp @sshArgs $tarPath "${HostName}:$RemoteTar"
    Assert-NativeCommand "backend archive upload"
}

$restartPizzad = if ($NoRestart) { "0" } else { "1" }
$remoteScript = @"
REMOTE_TAR=$(ConvertTo-BashSingleQuoted $RemoteTar)
RESTART_PIZZAD=$(ConvertTo-BashSingleQuoted $restartPizzad)
HEALTH_TIMEOUT_SECONDS=$(ConvertTo-BashSingleQuoted ([string]$HealthTimeoutSeconds))
set -e
work=/tmp/pizzad-direct-deploy
maintenance_start=`$(date -u +%Y-%m-%dT%H:%M:%SZ)
maintenance_token=`$(sudo cat /etc/pizzawave/pizzad.token 2>/dev/null || true)
maintenance_response=""
if [ -n "`$maintenance_token" ]; then
  maintenance_response=`$(curl -fsS -H "Authorization: Bearer `$maintenance_token" -H 'Content-Type: application/json' -d '{"source":"deployment_helper","reason":"PizzaWave backend deployment","excludeFromBaselines":true}' http://127.0.0.1:8080/api/v1/system/maintenance 2>/dev/null || true)
fi
maintenance_id=`$(printf '%s' "`$maintenance_response" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
rm -rf "`$work"
mkdir -p "`$work"
tar -xf "`$REMOTE_TAR" -C "`$work"
if [ "`$RESTART_PIZZAD" = "1" ]; then
  sudo systemctl stop pizzad || true
fi
sudo rsync -a --delete "`$work"/ /opt/pizzawave/pizzad/
sudo chown -R root:root /opt/pizzawave/pizzad
sudo chmod +x /opt/pizzawave/pizzad/pizzad
sudo find /opt/pizzawave/pizzad/scripts -type f -name '*.sh' -exec chmod 0755 {} \; 2>/dev/null || true
if sudo test -f /opt/pizzawave/pizzad/scripts/pizzawave_setup_admin.sh; then
  sudo install -d -m 0755 /usr/lib/pizzawave/scripts
  sudo perl -pi -e 's/\r$//' /opt/pizzawave/pizzad/scripts/pizzawave_setup_admin.sh
  sudo install -m 0755 -o root -g root /opt/pizzawave/pizzad/scripts/pizzawave_setup_admin.sh /usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh
fi
if sudo test -f /opt/pizzawave/pizzad/scripts/tr_tune.sh; then
  sudo install -d -m 0755 /usr/lib/pizzawave/scripts
  sudo perl -pi -e 's/\r$//' /opt/pizzawave/pizzad/scripts/tr_tune.sh
  sudo install -m 0755 -o root -g root /opt/pizzawave/pizzad/scripts/tr_tune.sh /usr/lib/pizzawave/scripts/tr_tune.sh
fi
if sudo test -f /etc/pizzawave/pizzad.json; then
  sudo chown root:pizzawave /etc/pizzawave/pizzad.json
  sudo chmod 0660 /etc/pizzawave/pizzad.json
fi
if sudo test -f /etc/pizzawave/pizzad.token; then
  sudo chown root:pizzawave /etc/pizzawave/pizzad.token
  sudo chmod 0640 /etc/pizzawave/pizzad.token
fi
if [ "`$RESTART_PIZZAD" = "1" ]; then
  sudo systemctl start pizzad
fi
systemctl is-active pizzad
deadline=`$((`$HEALTH_TIMEOUT_SECONDS * 2))
for i in `$(seq 1 "`$deadline"); do
  if curl -fsS http://127.0.0.1:8080/api/v1/health >/dev/null; then
    healthy=1
    break
  fi
  sleep 0.5
done
if [ "`$healthy" = "1" ] && [ -n "`$maintenance_token" ]; then
  if [ -n "`$maintenance_id" ]; then
    curl -fsS -X POST -H "Authorization: Bearer `$maintenance_token" http://127.0.0.1:8080/api/v1/system/maintenance/`$maintenance_id/close >/dev/null
  else
    maintenance_end=`$(date -u +%Y-%m-%dT%H:%M:%SZ)
    curl -fsS -H "Authorization: Bearer `$maintenance_token" -H 'Content-Type: application/json' -d "{\"source\":\"deployment_helper\",\"reason\":\"PizzaWave backend deployment\",\"startUtc\":\"`$maintenance_start\",\"endUtc\":\"`$maintenance_end\",\"excludeFromBaselines\":true}" http://127.0.0.1:8080/api/v1/system/maintenance >/dev/null
  fi
  echo "pizzad direct tar deploy complete"
  exit 0
fi
echo "pizzad health check did not pass within `$HEALTH_TIMEOUT_SECONDS seconds" >&2
exit 1
"@

Invoke-TimedStage "remote backend install" { Invoke-RemoteDeployScript $remoteScript }
$overallTimer.Stop()
Write-Host ("Full deploy complete in {0:n1}s." -f $overallTimer.Elapsed.TotalSeconds)
