# C# ExecMCP CLI Port Design

## Goal

Replace the JavaScript/Bun implementation with a Windows x64 .NET 10 CLI while preserving the written CLI/JSON contract, adding named command profiles and packaged Snipping Tool integration, and removing MCP from the final product. The written specification is authoritative; the attached JavaScript snapshot is a behavioral reference only where it does not conflict with the specification.

## Delivery shape

The solution contains `ExecMcp.Core`, `ExecMcp.Cli`, `ExecMcp.SnippingCallback`, `ExecMcp.Tests`, and `ExecMcp.Package` (Windows Application Packaging Project). All .NET projects target `net10.0-windows10.0.19041.0`, x64, framework-dependent.

During parity, GitHub Actions produces both an unpackaged `execmcp-cs` publish artifact and a signed MSIX. The final supported distribution is MSIX only, exposing `execmcp.exe` via an app execution alias.

## Core boundaries

`ExecMcp.Core` owns validation, duration parsing, executable resolution, explicit shell construction, profiles, state persistence, bounded logs, event sequences, job lifecycle, Windows Job Objects, process inspection, port inspection, and window capture. The CLI owns argument parsing, text/JSON rendering, exit-code propagation, and the internal detached runner entry point. The callback executable owns only protocol activation validation and one-use token redemption.

State lives under `%LOCALAPPDATA%\windows-exec-mcp\v2`. Legacy root files and JavaScript state are never imported, modified, or deleted. Config remains `%LOCALAPPDATA%\windows-exec-mcp\config.json` as specified.

## State and concurrency

Each state mutation takes a per-user named mutex and rewrites the JSON state atomically through a temporary file plus `File.Replace`/rename fallback. Job records contain stable JSON field names, monotonic event sequence numbers, timestamps, process identity, exit state, readiness state, log metadata, and termination metadata.

Logs are append-only files bounded independently by `max_log_bytes`. Output APIs expose byte cursors and UTF-8-safe reads. Readiness regex scanning operates on the live stream before log truncation, so readiness can be detected even if old log bytes are later discarded.

## Process model

Foreground `run` uses the same normalized command specification and process-launch machinery as background jobs. `start` writes a runner specification and launches the same CLI executable with an internal runner command in a detached process. The runner creates a Windows Job Object with kill-on-close semantics, starts the child suspended when necessary to assign it before useful work, resumes it, streams output, emits events, handles timeout/readiness, and persists final state.

Termination is idempotent. `kill` terminates the Job Object and verifies the tracked process tree is gone. Runner failure closes its Job Object handle so descendants are cleaned up automatically.

Direct mode uses `ProcessStartInfo.ArgumentList` and never reparses argv through a shell. Shell modes are explicit: PowerShell, cmd, and Git Bash. PowerShell command text is transported as UTF-8 via `-EncodedCommand` using UTF-16LE base64 expected by PowerShell, avoiding locale/quoting corruption. Git Bash lookup refuses WSL bash.

## CLI contract

Preserve commands and JSON names for `run`, `start`, `list`, `status`, `output`, `wait`, `kill`, `shell`, `resolve`, `path-info`, `doctor`, `port-info`, `capture-window`, and `events`. `execmcp mcp` is removed.

Add `run-config <name>` and `start-config <name>`. Profiles fix executable, base args, cwd, environment, timeout, output/log limits, readiness pattern, and title. Callers cannot override them. Tokens after `--` are appended only when `allow_appended_args` is true.

## Window capture

`capture-window` resolves a target by job, PID, title, or HWND. It uses Win32 window enumeration and DWM/PrintWindow capture paths, restores minimized windows when required, optionally avoids foreground activation, and writes a valid PNG. Selection and capture logic are isolated behind interfaces so deterministic tests can cover resolution independently of live desktop integration.

## Snipping Tool integration

`execmcp snip --mode rectangle|freeform|window|video` is available only meaningfully from the packaged app. It creates a correlation GUID and invokes `Windows.System.Launcher.LaunchUriAsync` for `ms-screenclip://capture/...` with `api-version=1.2`, `auto-save`, `user-agent=execmcp`, the correlation id, and `redirect-uri=execmcp-snip://complete`. Success means Windows accepted the launch; the CLI does not wait for capture completion.

The package registers a second silent full-trust application for `execmcp-snip:` protocol activation. The callback validates scheme/host and correlation shape, redeems any returned one-use file token through `SharedStorageAccessManager.RedeemTokenForFileAsync`, and exits without moving, copying, deleting, or opening the autosaved artifact. Cancellation and malformed callbacks are harmless.

## Packaging

`ExecMcp.Package` is an MSIX packaging project containing the framework-dependent CLI and callback outputs. The manifest registers `execmcp.exe` as the execution alias and `execmcp-snip` as the callback protocol. A development certificate script creates a self-signed certificate whose subject matches the manifest publisher, exports a PFX for signing and a CER for user trust, and never installs the private key into TrustedPeople.

Current-user install imports only the public CER into `Cert:\CurrentUser\TrustedPeople`, then installs the MSIX. Uninstall removes the package but leaves legacy `%LOCALAPPDATA%\windows-exec-mcp` root state intact.

## GitHub Actions

A Windows GitHub Actions workflow installs .NET 10, restores/builds/tests the solution, publishes the unpackaged x64 CLI artifact as `execmcp-cs`, generates an ephemeral development certificate, builds/signs the MSIX, installs it for live smoke tests, verifies the execution alias, exercises compatible JSON commands and all four `snip` launch shapes where the runner environment permits protocol launch, then uploads the MSIX, CER, install/uninstall scripts, and parity results.

PR/push CI runs unit and non-interactive Windows integration tests. A packaging job runs after tests. Live interactive Snipping completion/autosave is represented as a manual Windows acceptance step because hosted runners cannot complete human capture UI; launch construction and callback parsing/token redemption are automated separately.

## Migration

The repository initially keeps the JavaScript snapshot alongside the C# implementation for fixture/parity reference. C# state starts fresh in `v2`. After C# unit/integration tests, unpackaged parity, packaged alias verification, and the final compatibility script pass, JavaScript/Bun/MCP files are removed in a dedicated migration commit. No code path deletes existing user state or old root files.

## Testing

Unit tests cover validation, durations, profiles, redaction, executable resolution, shell construction, UTF-8 byte tails, readiness/event sequencing, atomic state updates, and callback validation. Windows integration tests cover direct argv quoting, Unicode PowerShell, exact exit codes, timeouts, concurrent writers/readers, readiness beyond truncation, Job Object descendant cleanup, port/process inspection, and PNG capture where a desktop session is available.

The final acceptance script compares the JavaScript reference fixtures and C# JSON field names for every preserved command, then validates the installed MSIX alias. Features absent from the attached JavaScript snapshot are validated against the written specification rather than treated as missing parity requirements.
