# ExecMCP CLI

Windows x64 command execution CLI implemented in C#/.NET 10. The final distribution is a framework-dependent MSIX package exposing the `execmcp.exe` app execution alias. MCP is intentionally not part of the C# implementation.

## Requirements

- Windows x64
- .NET 10 Desktop Runtime
- Windows 10 version 2004 / build 19041 or newer
- Snipping Tool with the packaged capture protocol for `snip`

## Commands

```text
run, start, list, status, output, wait, kill
shell, resolve, path-info, doctor, port-info
capture-window, events
run-config, start-config
snip
```

Direct execution preserves the supplied argument array instead of rebuilding it through a shell:

```powershell
execmcp run --json -- dotnet.exe --info
execmcp start --ready 'Listening on' -- dotnet.exe run
execmcp output <job-id> --offset 0 --json
execmcp kill <job-id> --json
```

Explicit shell execution is available for PowerShell, cmd, and Git Bash:

```powershell
execmcp shell powershell --command "Write-Output '雪'"
execmcp shell cmd -- echo hello
execmcp shell git-bash -- "printf '%s\n' hello"
```

## Named command profiles

Profiles are read from `%LOCALAPPDATA%\windows-exec-mcp\config.json` and use schema version 1:

```json
{
  "version": 1,
  "commands": {
    "build": {
      "executable": "dotnet.exe",
      "args": ["build"],
      "cwd": "C:\\projects\\app",
      "env": {},
      "timeout_ms": 300000,
      "max_output_bytes": 262144,
      "max_log_bytes": 67108864,
      "ready_pattern": null,
      "title": "Build",
      "allow_appended_args": false
    }
  }
}
```

Run a profile with `execmcp run-config build --json` or start it in the background with `execmcp start-config build --json`. Profile executable, base arguments, working directory, and environment are fixed. Tokens after `--` are accepted only when `allow_appended_args` is true.

## State and process lifetime

C# state is isolated under `%LOCALAPPDATA%\windows-exec-mcp\v2`. Existing JavaScript state in the root is never imported, modified, or deleted. Cross-process state mutation uses a per-user named mutex and atomic file replacement. Background commands run under an internal detached runner; each command tree is assigned to a Windows Job Object so timeout and `kill` terminate descendants and runner failure closes the job.

Output logs are bounded and expose byte cursors plus full/tail byte metadata. Readiness regexes are evaluated on the live byte stream before retained-log truncation. Event sequence numbers are global and monotonic within v2 state.

## Window and Snipping Tool capture

`capture-window` is the unattended capture path and can resolve a window by job, PID, title, or HWND. Minimized windows are temporarily restored and can be captured without foreground activation using `--no-foreground`.

Interactive Snipping Tool capture is fire-and-forget and requires the installed MSIX:

```powershell
execmcp snip --mode rectangle
execmcp snip --mode freeform
execmcp snip --mode window
execmcp snip --mode video
```

The package registers `execmcp-snip://complete`. Its silent callback validates the correlation ID and redeems the returned one-use file token, but never copies, moves, or deletes the autosaved capture. Autosave location remains a Snipping Tool setting; the acceptance environment uses `C:\Users\paul\Pictures\screenshots`.

## Build and install

GitHub Actions is the canonical Windows build. `.github/workflows/windows-build.yml` runs the legacy JavaScript reference tests, the complete C# xUnit suite, publishes `execmcp-cs` for parity, builds a signed MSIX with a runner-generated development certificate, installs the package for current-user verification, runs compatibility checks, then uploads three artifacts:

- `execmcp-cs-unpackaged`
- `execmcp-msix`
- `compatibility-results`

Only the public `.cer` is distributed for trust. Install it into `CurrentUser\TrustedPeople` and install the MSIX with:

```powershell
.\Install-ExecMcp.ps1 -MsixPath .\ExecMcp.msix -CertificatePath .\ExecMcp.Development.cer
```

Uninstall removes the package and intentionally leaves `%LOCALAPPDATA%\windows-exec-mcp` untouched:

```powershell
.\Uninstall-ExecMcp.ps1
```

The repository keeps `legacy-js` only during the parity gate. It is removed after the complete C# and installed-MSIX verification passes.
