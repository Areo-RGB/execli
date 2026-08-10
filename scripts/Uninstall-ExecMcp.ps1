[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Get-AppxPackage -Name 'AreoRGB.ExecMcp' | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
Write-Host 'ExecMCP package removed. Existing %LOCALAPPDATA%\windows-exec-mcp state was left intact.'
