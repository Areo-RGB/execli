# windows-exec-mcp

Windows-first command execution for MCP clients, plus a CLI that routes through the same MCP tools without requiring client configuration.

## Install

From this project:

```powershell
pnpm install
pnpm add --global C:\Users\paul\projects\windows-exec-mcp
```

If pnpm reports that its global bin directory is missing from PATH, add:

```text
C:\Users\paul\AppData\Local\pnpm\bin
```

## CLI

```text
execmcp run -- git status
execmcp run -- pnpm install
execmcp start -- node -e "setTimeout(()=>console.log('done'), 60000)"
execmcp list
execmcp status <job-id>
execmcp output <job-id> --follow
execmcp wait <job-id> --timeout 30s
execmcp kill <job-id>
execmcp resolve pnpm
execmcp path-info C:\Users\paul\projects
execmcp shell powershell --command "Get-Location"
execmcp shell git-bash --command "printf hello"
```

Direct commands use `executable` plus `args[]`, so spaces, quotes, ampersands, and Unicode are not re-parsed by a shell. Shell mode is explicit and Git Bash runs without loading the user's shell profiles.

## Standalone Windows executable

Bun can package the CLI and MCP server, including the Bun runtime, into one Windows x64 executable:

```powershell
pnpm run build:exe
.\dist\execmcp.exe resolve pnpm
.\dist\execmcp.exe run -- git status
```

The compiled executable also launches its background job supervisor from the same file:

```powershell
$job = .\dist\execmcp.exe start -- node -e "setTimeout(()=>console.log('done'), 60000)"
.\dist\execmcp.exe wait <job-id> --timeout 70s
```

The executable embeds Bun rather than a separate Node installation. Commands that you ask it to run, such as `node`, `pnpm`, PowerShell, or Git Bash, still need to exist on the machine and resolve through its PATH.

## MCP server

For an MCP client that supports stdio, use:

```text
command: execmcp
args: mcp
```

The server exposes `exec`, `job_start`, `job_status`, `job_output`, `job_wait`, `job_list`, `job_kill`, `command_resolve`, and `path_info`.

Background state and bounded logs are stored under `%LOCALAPPDATA%\windows-exec-mcp`. Existing environment variables are inherited for commands but are not returned by the tools; no API-key values are changed.
