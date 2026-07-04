param(
    [Parameter(Mandatory = $true)]
    [string]$HostName,

    [string]$SshKey = "",

    [string]$RemoteTar = "/tmp/pizzad-web-deploy.tar.gz",

    [int]$HealthTimeoutSeconds = 20,

    [switch]$NpmCi
)

$ErrorActionPreference = "Stop"

$deploy = Join-Path $PSScriptRoot "deploy_pizzad_tar.ps1"
$deployParams = @{
    HostName = $HostName
    RemoteTar = $RemoteTar
    HealthTimeoutSeconds = $HealthTimeoutSeconds
    WebOnly = $true
}

if ($SshKey) {
    $deployParams.SshKey = $SshKey
}

if (-not $NpmCi) {
    $deployParams.SkipNpmCi = $true
}

& $deploy @deployParams
