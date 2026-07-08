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

    [switch]$NoRestart,

    [int]$HealthTimeoutSeconds = 20
)

$ErrorActionPreference = "Stop"

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

function ConvertTo-BashSingleQuoted([string]$Value) {
    return "'" + $Value.Replace("'", "'""'""'") + "'"
}

function Invoke-RemoteDeployScript([string]$ScriptBody) {
    $remoteScriptPath = Join-Path ([System.IO.Path]::GetTempPath()) "pizzad-direct-deploy-$([Guid]::NewGuid().ToString('N')).sh"
    $sshArgs = Get-SshArgs
    try {
        [System.IO.File]::WriteAllText($remoteScriptPath, $ScriptBody.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
        scp @sshArgs $remoteScriptPath "${HostName}:/tmp/pizzad-direct-deploy.sh"
        ssh @sshArgs $HostName "bash /tmp/pizzad-direct-deploy.sh"
    }
    finally {
        if (Test-Path $remoteScriptPath) {
            Remove-Item -LiteralPath $remoteScriptPath -Force
        }
    }
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$artifactsDir = Join-Path $root "artifacts"
$publishDir = Join-Path $artifactsDir "pizzad-direct-$Rid"
$tarPath = Join-Path $artifactsDir "pizzad-direct-$Rid.tar.gz"
$webTarPath = Join-Path $artifactsDir "pizzad-web.tar.gz"
$webDir = Join-Path $root "pizzad\web"

New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

if (-not $BackendOnly) {
    Push-Location $webDir
    try {
        if (-not $SkipNpmCi) {
            npm ci
        }
        npm run build
    }
    finally {
        Pop-Location
    }
}

$sshArgs = Get-SshArgs

if (-not $Rid) {
    $remoteMachine = (ssh @sshArgs $HostName "uname -m").Trim()
    $Rid = switch -Regex ($remoteMachine) {
        "^(aarch64|arm64)$" { "linux-arm64"; break }
        "^(x86_64|amd64)$" { "linux-x64"; break }
        default { throw "Unable to infer .NET RID from remote architecture '$remoteMachine'. Pass -Rid explicitly." }
    }
    Write-Host "Inferred RID '$Rid' from remote architecture '$remoteMachine'."
}

if ($WebOnly) {
    if (Test-Path $webTarPath) {
        Remove-Item -LiteralPath $webTarPath -Force
    }

    tar -czf $webTarPath -C (Join-Path $root "pizzad") wwwroot
    scp @sshArgs $webTarPath "${HostName}:$RemoteTar"

    $remoteScript = @"
REMOTE_TAR=$(ConvertTo-BashSingleQuoted $RemoteTar)
HEALTH_TIMEOUT_SECONDS=$(ConvertTo-BashSingleQuoted ([string]$HealthTimeoutSeconds))
set -e
work=/tmp/pizzad-web-deploy
rm -rf "`$work"
mkdir -p "`$work"
tar -xzf "`$REMOTE_TAR" -C "`$work"
sudo install -d -m 0755 /opt/pizzawave/pizzad/wwwroot
sudo rsync -a --delete "`$work"/wwwroot/ /opt/pizzawave/pizzad/wwwroot/
sudo chown -R root:root /opt/pizzawave/pizzad/wwwroot
systemctl is-active pizzad
deadline=`$((`$HEALTH_TIMEOUT_SECONDS * 2))
for i in `$(seq 1 "`$deadline"); do
  if curl -fsS http://127.0.0.1:8080/api/v1/health >/dev/null; then
    echo "pizzad web deploy complete"
    exit 0
  fi
  sleep 0.5
done
echo "pizzad health check did not pass within `$HEALTH_TIMEOUT_SECONDS seconds" >&2
exit 1
"@
    Invoke-RemoteDeployScript $remoteScript
    return
}

dotnet publish (Join-Path $root "pizzad\pizzad.csproj") `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -p:SelfContained=true `
    -p:PublishSingleFile=false `
    -o $publishDir

if (Test-Path $tarPath) {
    Remove-Item -LiteralPath $tarPath -Force
}

tar -czf $tarPath -C $publishDir .
scp @sshArgs $tarPath "${HostName}:$RemoteTar"

$restartPizzad = if ($NoRestart) { "0" } else { "1" }
$remoteScript = @"
REMOTE_TAR=$(ConvertTo-BashSingleQuoted $RemoteTar)
RESTART_PIZZAD=$(ConvertTo-BashSingleQuoted $restartPizzad)
HEALTH_TIMEOUT_SECONDS=$(ConvertTo-BashSingleQuoted ([string]$HealthTimeoutSeconds))
set -e
work=/tmp/pizzad-direct-deploy
rm -rf "`$work"
mkdir -p "`$work"
tar -xzf "`$REMOTE_TAR" -C "`$work"
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
    echo "pizzad direct tar deploy complete"
    exit 0
  fi
  sleep 0.5
done
echo "pizzad health check did not pass within `$HEALTH_TIMEOUT_SECONDS seconds" >&2
exit 1
"@

Invoke-RemoteDeployScript $remoteScript
