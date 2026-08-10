# Windows Exec MCP Design

## Goal

Build a local Windows command-execution service optimized for Codex-style agent work. It will expose a standard MCP server and a self-contained `execmcp` CLI, both backed by the same process manager, so short commands, long-running work, polling, cancellation, and shell-specific commands behave consistently.

## Decisions

- Runtime: Node.js from the existing Vite+ installation; no additional Node installation.
- Location: `C:\Users\paul\projects\windows-exec-mcp`.
- Transport: MCP stdio first, with no network listener or authentication surface by default.
- CLI: a first-party client that starts the local MCP server internally and invokes the same MCP tools; no per-client configuration is required.
- Default execution: direct process spawning with `executable` plus `args[]`; shell parsing is opt-in.
- Shells: explicit modes for PowerShell, `cmd.exe`, and Git Bash. The server will not silently reinterpret direct arguments as shell text.
- Environment: inherit the normal user environment, with optional additions; never return environment values in tool results or logs.
- Secrets: no API-key files, environment variables, or existing MCP configuration entries will be modified.
- Persistence: jobs and bounded output logs are retained in an application state directory so a request disconnect does not terminate work. Recovery after a server restart will be explicit and conservative; processes that cannot be proven alive are marked unknown rather than claimed complete.

## User-facing MCP tools

### `exec`

Run a short-lived direct process and wait for completion. Inputs include `executable`, `args`, optional `cwd`, optional timeout, and optional environment additions. Results include exit code, timeout state, duration, and bounded stdout/stderr.

### `job_start`

Start a detached job without waiting. It uses the same command specification as `exec`, returns a stable job ID, and captures stdout/stderr independently.

### `job_status`

Return lifecycle state, PID when known, exit code when finished, timing, output sizes, and bounded output tails.

### `job_output`

Read stdout or stderr by byte offset with a bounded maximum. The response includes the next offset so polling is restartable and does not duplicate large logs.

### `job_wait`

Wait for completion for a bounded interval, then return the current status and output tail. It never creates an unbounded MCP request.

### `job_list`

List active and recently completed jobs with compact metadata.

### `job_kill`

Stop a process tree. Windows uses `taskkill /PID /T`, attempting graceful termination first and force termination after a short bounded grace period.

### `command_resolve`

Resolve an executable using the server's actual Windows environment and return the chosen path. This makes PATH problems observable instead of silently relying on a different shell's PATH.

### `path_info`

Report normalized path, existence, type, and accessibility for a requested path. It is read-only and intended for preflight checks.

## CLI

The CLI will route commands through the MCP client API, not a second implementation:

```text
execmcp run -- git status
execmcp run --timeout 30s -- pnpm install
execmcp start --cwd C:\Users\paul\projects -- robocopy C:\source D:\target /E
execmcp list
execmcp status <job-id>
execmcp output <job-id> --stream stdout --follow
execmcp wait <job-id> --timeout 60s
execmcp kill <job-id>
execmcp resolve pnpm
execmcp path-info C:\Users\paul\projects
execmcp shell powershell -- -NoProfile -Command Get-Location
execmcp mcp
```

Human-readable output is the default. `--json` is available for scripting. Exit codes mirror command failures for `run`, and CLI validation or transport errors use distinct nonzero codes.

## Architecture

The implementation will have four focused units:

1. `process-spec`: validates direct and shell command requests, normalizes paths, timeouts, environment additions, and output limits.
2. `job-manager`: owns process creation, output capture, lifecycle transitions, persistence, polling, and Windows tree termination.
3. `mcp-server`: registers the tools and converts manager results to MCP responses without exposing secrets.
4. `cli`: parses arguments, starts an in-process or child stdio MCP server, calls MCP tools, formats results, and maps failures to exit codes.

Direct mode uses `child_process.spawn(executable, args, { shell: false, windowsHide: true })`. Shell mode constructs the minimum required invocation for the selected shell and is visibly labeled in results. All output is bounded in memory and spilled to per-job files after the configured threshold.

## Safety and operational limits

- No shell by default.
- No command text interpolation for direct mode.
- CWD must exist and be a directory before launch.
- Executable resolution is performed using the same environment as execution.
- Concurrent jobs, retained jobs, output memory, spill size, and request wait time have finite defaults.
- Environment additions are allowlisted by shape and are never echoed.
- Tool errors include actionable diagnostics but not full environment dumps or secret-looking values.
- The server handles broken MCP clients and disconnects without killing detached jobs.
- Shutdown attempts to terminate owned jobs and flush state cleanly.

## Verification

Tests will cover:

- direct arguments containing spaces, quotes, ampersands, and Unicode;
- PATH resolution for `node`, `pnpm`, and a known Windows executable;
- missing executable and missing CWD errors;
- timeout and nonzero exit behavior;
- stdout/stderr separation and offset polling;
- background completion, repeated status calls, and job restart metadata;
- process-tree termination;
- explicit PowerShell, `cmd.exe`, and Git Bash modes;
- CLI-to-MCP parity for run/start/status/output/wait/kill;
- MCP initialize and tool discovery over stdio;
- redaction/non-disclosure of environment values.

## Non-goals for the first version

- A network-accessible HTTP server.
- A global Windows service or scheduled task.
- Elevation/UAC automation.
- Interactive terminal emulation or arbitrary stdin streaming.
- Automatic allowlisting or automatic destructive-command approval. The caller remains responsible for requested command semantics; the server focuses on predictable execution and bounded lifecycle control.
