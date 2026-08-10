[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$CliPublishPath,
  [Parameter(Mandatory)][string]$CallbackPublishPath,
  [Parameter(Mandatory)][string]$CertificateThumbprint,
  [string]$OutputPath = 'artifacts\msix\ExecMcp.msix',
  [string]$StagePath = 'artifacts\package'
)
$ErrorActionPreference = 'Stop'

$cli = (Resolve-Path $CliPublishPath).Path
$callback = (Resolve-Path $CallbackPublishPath).Path
$manifest = (Resolve-Path (Join-Path $PSScriptRoot '..\src\ExecMcp.Package\Package.appxmanifest')).Path
$assets = (Resolve-Path (Join-Path $PSScriptRoot '..\src\ExecMcp.Package\Assets')).Path
$stage = [IO.Path]::GetFullPath($StagePath)
$output = [IO.Path]::GetFullPath($OutputPath)

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, (Split-Path -Parent $output) | Out-Null
Copy-Item (Join-Path $cli '*') $stage -Recurse -Force
Copy-Item (Join-Path $callback '*') $stage -Recurse -Force
Copy-Item $manifest (Join-Path $stage 'AppxManifest.xml') -Force
Copy-Item $assets (Join-Path $stage 'Assets') -Recurse -Force

foreach ($required in @('execmcp.exe','ExecMcp.SnippingCallback.exe','AppxManifest.xml')) {
  if (-not (Test-Path (Join-Path $stage $required))) { throw "Package staging is missing $required" }
}

$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem $kits -Recurse -File -Filter makeappx.exe |
  Where-Object FullName -Match '\\x64\\makeappx\.exe$' |
  Sort-Object { [version]($_.Directory.Parent.Name) } -Descending |
  Select-Object -First 1
$signTool = Get-ChildItem $kits -Recurse -File -Filter signtool.exe |
  Where-Object FullName -Match '\\x64\\signtool\.exe$' |
  Sort-Object { [version]($_.Directory.Parent.Name) } -Descending |
  Select-Object -First 1
if (-not $makeAppx) { throw 'Could not locate x64 MakeAppx.exe in the Windows SDK' }
if (-not $signTool) { throw 'Could not locate x64 SignTool.exe in the Windows SDK' }

& $makeAppx.FullName pack /v /o /h SHA256 /d $stage /p $output
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE" }

& $signTool.FullName sign /fd SHA256 /s My /sha1 $CertificateThumbprint $output
if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE" }

& $signTool.FullName verify /pa /v $output
if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed with exit code $LASTEXITCODE" }

Get-Item $output
