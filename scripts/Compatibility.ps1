[CmdletBinding()]
param([string]$Exe = 'execmcp.exe', [string]$Output = 'artifacts\compatibility.json')
$ErrorActionPreference = 'Stop'
$expected = Get-Content (Join-Path $PSScriptRoot '..\compat\expected-json-fields.json') -Raw | ConvertFrom-Json
$results = [ordered]@{}
$run = & $Exe run --json -- cmd.exe /d /s /c "echo compatibility-ok"
if ($LASTEXITCODE -ne 0) { throw "run compatibility command failed: $LASTEXITCODE" }
$runJson = $run | ConvertFrom-Json
$missing = @($expected.run | Where-Object { $_ -notin $runJson.PSObject.Properties.Name })
$results.run = [ordered]@{ missing_fields = $missing; state = $runJson.state; exit_code = $runJson.exit_code }
$startRaw = & $Exe start --json -- cmd.exe /d /s /c "ping 127.0.0.1 -n 2 >nul & echo background-ok"
$start = $startRaw | ConvertFrom-Json
$missingStart = @($expected.start | Where-Object { $_ -notin $start.PSObject.Properties.Name })
$wait = & $Exe wait $start.id --timeout 15s --json | ConvertFrom-Json
$output = & $Exe output $start.id --json | ConvertFrom-Json
$missingOutput = @($expected.output | Where-Object { $_ -notin $output.PSObject.Properties.Name })
$results.start = [ordered]@{ missing_fields = $missingStart; final_state = $wait.state; exit_code = $wait.exit_code }
$results.output = [ordered]@{ missing_fields = $missingOutput; text = $output.text; next_offset = $output.next_offset }
$results.mcp_removed = $true
try { & $Exe mcp 2>$null | Out-Null; if ($LASTEXITCODE -eq 0) { $results.mcp_removed = $false } } catch { }
$results.pass = ($missing.Count -eq 0 -and $missingStart.Count -eq 0 -and $missingOutput.Count -eq 0 -and $wait.state -eq 'completed' -and $results.mcp_removed)
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
$results | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $Output
if (-not $results.pass) { throw 'Compatibility checks failed' }
