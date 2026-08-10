[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$MsixPath,
  [Parameter(Mandatory)][string]$CertificatePath
)
$ErrorActionPreference = 'Stop'
$msix = (Resolve-Path $MsixPath).Path
$cer = (Resolve-Path $CertificatePath).Path
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
Add-AppxPackage -Path $msix
$command = Get-Command execmcp.exe -ErrorAction Stop
Write-Host "Installed ExecMCP. Alias: $($command.Source)"
