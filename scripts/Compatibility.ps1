[CmdletBinding()]
param([string]$Exe = 'execmcp.exe', [string]$Output = 'artifacts\compatibility.json')
$ErrorActionPreference = 'Stop'
$expectedPath = Join-Path $PSScriptRoot '..\compat\expected-json-fields.json'
$expected = Get-Content $expectedPath -Raw | ConvertFrom-Json
$results = [ordered]@{}

function Invoke-ExecMcpJson {
  param([string]$Label, [string[]]$Arguments)
  Write-Host "compat: $Label -> $Exe $($Arguments -join ' ')"
  $raw = & $Exe @Arguments 2>&1
  $exit = $LASTEXITCODE
  if ($exit -ne 0) { throw "compat: $Label failed with exit code $exit`n$($raw -join [Environment]::NewLine)" }
  try { return ($raw -join [Environment]::NewLine) | ConvertFrom-Json }
  catch { throw "compat: $Label returned invalid JSON`n$($raw -join [Environment]::NewLine)" }
}

$runJson = Invoke-ExecMcpJson 'run' @('run','--json','--','cmd.exe','/d','/s','/c','echo compatibility-ok')
$missing = @($expected.run | Where-Object { $_ -notin $runJson.PSObject.Properties.Name })
$results.run = [ordered]@{ missing_fields = $missing; state = $runJson.state; exit_code = $runJson.exit_code }

$start = Invoke-ExecMcpJson 'start' @('start','--json','--','cmd.exe','/d','/s','/c','ping 127.0.0.1 -n 2 >nul & echo background-ok')
$missingStart = @($expected.start | Where-Object { $_ -notin $start.PSObject.Properties.Name })
$wait = Invoke-ExecMcpJson 'wait' @('wait',[string]$start.id,'--timeout','15s','--json')
$outputJson = Invoke-ExecMcpJson 'output' @('output',[string]$start.id,'--json')
$missingOutput = @($expected.output | Where-Object { $_ -notin $outputJson.PSObject.Properties.Name })
$results.start = [ordered]@{ missing_fields = $missingStart; final_state = $wait.state; exit_code = $wait.exit_code }
$results.output = [ordered]@{ missing_fields = $missingOutput; text = $outputJson.text; next_offset = $outputJson.next_offset }

Write-Host 'compat: confirming mcp command is removed'
$results.mcp_removed = $true
try {
  $mcpRaw = & $Exe mcp 2>&1
  if ($LASTEXITCODE -eq 0) { $results.mcp_removed = $false }
} catch { }

$results.pass = ($missing.Count -eq 0 -and $missingStart.Count -eq 0 -and $missingOutput.Count -eq 0 -and $wait.state -eq 'completed' -and $results.mcp_removed)
$outputDirectory = Split-Path -Parent $Output
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) { New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null }
$results | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 -Path $Output
if (-not $results.pass) { throw "Compatibility checks failed. Results: $($results | ConvertTo-Json -Depth 8 -Compress)" }
