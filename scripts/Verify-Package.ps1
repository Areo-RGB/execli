[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$cmd = Get-Command execmcp.exe -ErrorAction Stop
$doctor = & execmcp.exe doctor --json | ConvertFrom-Json
if (-not $doctor.windows) { throw 'doctor did not report Windows' }
if (-not $doctor.packaged) { throw 'execution alias did not launch with package identity' }
$resolved = & execmcp.exe resolve cmd.exe --json | ConvertFrom-Json
if (-not $resolved.exists) { throw 'cmd.exe resolution failed' }
Write-Host "Package verification passed using $($cmd.Source)"
