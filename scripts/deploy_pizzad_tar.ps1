param(
    [Parameter(Mandatory = $true)]
    [string]$HostName,

    [string]$SshKey = "",

    [string]$Rid = "linux-x64",

    [string]$Configuration = "Release",

    [string]$RemoteTar = "/tmp/pizzad-direct-deploy.tar.gz"
)

$ErrorActionPreference = "Stop"

if (($HostName -match "(^|@)(192\.168\.1\.173|omicrontheta)(:|$)") -and $Rid -ne "linux-x64") {
    throw "Refusing to deploy RID '$Rid' to OT ($HostName). OT is x86_64; use -Rid linux-x64."
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$publishDir = Join-Path $root "artifacts\pizzad-direct-$Rid"
$tarPath = Join-Path $root "artifacts\pizzad-direct-$Rid.tar.gz"
$webDir = Join-Path $root "pizzad\web"

Push-Location $webDir
try {
    npm ci
    npm run build
}
finally {
    Pop-Location
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

$sshArgs = @("-o", "BatchMode=yes")
if ($SshKey) {
    $sshArgs += @("-i", $SshKey, "-o", "IdentitiesOnly=yes")
}

scp @sshArgs $tarPath "${HostName}:$RemoteTar"

$remoteScript = @'
set -e
work=/tmp/pizzad-direct-deploy
rm -rf "$work"
mkdir -p "$work"
tar -xzf "$REMOTE_TAR" -C "$work"
sudo systemctl stop pizzad || true
sudo rsync -a --delete "$work"/ /opt/pizzawave/pizzad/
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
sudo systemctl start pizzad
sleep 8
systemctl is-active pizzad
curl -fsS http://127.0.0.1:8080/api/v1/health >/dev/null
echo "pizzad direct tar deploy complete"
'@

$remoteScriptPath = Join-Path ([System.IO.Path]::GetTempPath()) "pizzad-direct-deploy-$([Guid]::NewGuid().ToString('N')).sh"
try {
    $remoteScript = "REMOTE_TAR='$RemoteTar'`n$remoteScript"
    [System.IO.File]::WriteAllText($remoteScriptPath, $remoteScript.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
    scp @sshArgs $remoteScriptPath "${HostName}:/tmp/pizzad-direct-deploy.sh"
    ssh @sshArgs $HostName "bash /tmp/pizzad-direct-deploy.sh"
}
finally {
    if (Test-Path $remoteScriptPath) {
        Remove-Item -LiteralPath $remoteScriptPath -Force
    }
}
