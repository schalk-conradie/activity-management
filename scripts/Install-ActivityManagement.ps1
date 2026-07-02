Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$certificatePath = Join-Path $scriptDirectory "ActivityManagement-Signing.cer"
$appInstallerPath = Join-Path $scriptDirectory "ActivityManagement.appinstaller"

if (-not (Test-Path -LiteralPath $certificatePath)) {
    throw "Signing certificate was not found at $certificatePath."
}

if (-not (Test-Path -LiteralPath $appInstallerPath)) {
    throw "App Installer file was not found at $appInstallerPath."
}

Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null

Start-Process -FilePath $appInstallerPath
