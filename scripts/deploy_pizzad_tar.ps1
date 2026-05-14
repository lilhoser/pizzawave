param(
    [Parameter(Mandatory = $true)]
    [string]$HostName,

    [string]$SshKey = "",

    [string]$Rid = "linux-arm64",

    [string]$Configuration = "Release",

    [string]$RemoteTar = "/tmp/pizzad-direct-deploy.tar.gz"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$publishDir = Join-Path $root "artifacts\pizzad-direct-$Rid"
$tarPath = Join-Path $root "artifacts\pizzad-direct-$Rid.tar.gz"

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
