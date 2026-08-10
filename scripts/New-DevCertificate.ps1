[CmdletBinding()]
param(
  [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\cert'),
  [string]$Password = 'execmcp-dev'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$subject = 'CN=ExecMcp Development'
$cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'ExecMCP Development' `
  -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
  -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(2) `
  -TextExtension @('2.5.29.19={text}CA=false','2.5.29.37={text}1.3.6.1.5.5.7.3.3')
$secure = ConvertTo-SecureString $Password -AsPlainText -Force
$pfx = Join-Path $OutputDirectory 'ExecMcp.Development.pfx'
$cer = Join-Path $OutputDirectory 'ExecMcp.Development.cer'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
[pscustomobject]@{ Subject = $cert.Subject; Thumbprint = $cert.Thumbprint; Pfx = $pfx; Cer = $cer; Password = $Password } | ConvertTo-Json
