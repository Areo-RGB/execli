# C# ExecMCP CLI Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the specification-authoritative .NET 10 Windows ExecMCP CLI, validate unpackaged and MSIX behavior in GitHub Actions, then remove the JavaScript/Bun implementation only after parity gates pass.

**Architecture:** `ExecMcp.Core` contains deterministic command/state/process primitives and thin Win32 adapters. `ExecMcp.Cli` is the public CLI plus hidden detached runner. `ExecMcp.SnippingCallback` is a silent packaged protocol target. `ExecMcp.Package` provides MSIX identity, alias, and protocol registration. JSON fixtures and xUnit tests are the compatibility boundary.

**Tech Stack:** C# 14 / .NET 10, Windows SDK APIs, P/Invoke for Job Objects/window capture/ports, xUnit, Windows Application Packaging Project, PowerShell, GitHub Actions `windows-2025`.

## Global Constraints

- Target `net10.0-windows10.0.19041.0`, x64, framework-dependent deployment.
- C# state is `%LOCALAPPDATA%\windows-exec-mcp\v2`; never import/delete/modify JavaScript state.
- Config is `%LOCALAPPDATA%\windows-exec-mcp\config.json` with `version: 1` and named `commands`.
- Preserve command names and JSON field names from the written specification; remove `mcp` and all MCP SDK dependencies.
- Preserve exact argv, explicit PowerShell/cmd/Git Bash, UTF-8-safe logs/cursors, readiness regexes, event sequences, bounded logs, and idempotent tree termination.
- Snipping Tool launch requires packaged identity and uses `Launcher.LaunchUriAsync`, `api-version=1.2`, `auto-save`, `user-agent=execmcp`, correlation id, and `redirect-uri=execmcp-snip://complete`.
- Final distribution is MSIX only; parity produces both unpackaged `execmcp-cs` and MSIX artifacts.

---

### Task 1: Seed compatibility baseline and .NET solution

**Files:**
- Create: `legacy-js/**` from the attached JavaScript snapshot
- Create: `ExecMcp.slnx`
- Create: `src/ExecMcp.Core/ExecMcp.Core.csproj`
- Create: `src/ExecMcp.Cli/ExecMcp.Cli.csproj`
- Create: `src/ExecMcp.SnippingCallback/ExecMcp.SnippingCallback.csproj`
- Create: `tests/ExecMcp.Tests/ExecMcp.Tests.csproj`
- Create: `tests/ExecMcp.Tests/DurationTests.cs`
- Create: `src/ExecMcp.Core/DurationParser.cs`

**Interfaces:**
- Produces: `DurationParser.Parse(string value) -> int milliseconds`.

- [ ] Write xUnit tests for integer milliseconds and `ms/s/m/h/d` suffixes, including invalid text and overflow.
- [ ] Run `dotnet test tests/ExecMcp.Tests/ExecMcp.Tests.csproj -c Release`; expect failure before implementation.
- [ ] Implement `DurationParser.Parse` with checked arithmetic and `int` millisecond return.
- [ ] Run the focused tests; expect pass.
- [ ] Commit as `feat: scaffold dotnet execmcp solution`.

### Task 2: Command normalization, resolution, and shells

**Files:**
- Create: `src/ExecMcp.Core/CommandSpec.cs`
- Create: `src/ExecMcp.Core/CommandValidator.cs`
- Create: `src/ExecMcp.Core/ExecutableResolver.cs`
- Create: `src/ExecMcp.Core/ShellBuilder.cs`
- Create: `tests/ExecMcp.Tests/CommandValidationTests.cs`
- Create: `tests/ExecMcp.Tests/ShellBuilderTests.cs`
- Create: `tests/ExecMcp.Tests/ExecutableResolverTests.cs`

**Interfaces:**
- Produces: `NormalizedCommand CommandValidator.Normalize(CommandRequest request, CommandKind kind)`.
- Produces: `string ExecutableResolver.Resolve(string executable, IReadOnlyDictionary<string,string?> environment)`.
- Produces: `LaunchSpec ShellBuilder.Build(NormalizedCommand command)` where `LaunchSpec` exposes executable, `IReadOnlyList<string> Arguments`, cwd, and environment.

- [ ] Add failing tests for NUL rejection, cwd validation, env names, timeout/output/log bounds, direct argv metacharacters, PowerShell Unicode encoded transport, cmd flags, and Git Bash WSL refusal.
- [ ] Run focused tests and confirm failures.
- [ ] Implement immutable request/normalized records and validation constants from the specification.
- [ ] Resolve path-like executables plus `.exe/.cmd/.bat`, PATH lookup, and `where.exe` fallback.
- [ ] Build direct argv through `ArgumentList`; build PowerShell `-EncodedCommand` from UTF-16LE base64; use `/d /s /c` for cmd and `--noprofile --norc -c` for Git Bash.
- [ ] Run tests; expect pass.
- [ ] Commit as `feat: add command normalization and shell construction`.

### Task 3: Versioned state, bounded logs, byte cursors, and events

**Files:**
- Create: `src/ExecMcp.Core/StatePaths.cs`
- Create: `src/ExecMcp.Core/StateStore.cs`
- Create: `src/ExecMcp.Core/JobRecord.cs`
- Create: `src/ExecMcp.Core/EventRecord.cs`
- Create: `src/ExecMcp.Core/BoundedLog.cs`
- Create: `src/ExecMcp.Core/Utf8ByteReader.cs`
- Create: `tests/ExecMcp.Tests/StateStoreTests.cs`
- Create: `tests/ExecMcp.Tests/BoundedLogTests.cs`
- Create: `tests/ExecMcp.Tests/EventSequenceTests.cs`

**Interfaces:**
- Produces: `StateStore.ReadAsync`, `StateStore.UpdateAsync(Func<StateDocument,StateDocument>)` guarded by a per-user named mutex.
- Produces: `BoundedLog.AppendAsync(ReadOnlyMemory<byte>)` and `ReadAsync(long offset, int maxBytes)` returning text, `next_offset`, `eof`, full/tail byte metadata.
- Produces: `EventRecord` with monotonically increasing `sequence`.

- [ ] Add failing concurrent-writer tests that perform many increments through independent `StateStore` instances and assert no lost updates or malformed JSON.
- [ ] Add failing UTF-8 tests where read boundaries split multi-byte code points and verify replacement-free text plus byte-accurate cursor advancement.
- [ ] Implement `%LOCALAPPDATA%\windows-exec-mcp\v2`, named mutex acquisition, temp-file write-through, flush, and `File.Replace`/`File.Move` atomic fallback.
- [ ] Implement bounded append/truncation metadata while keeping cursor semantics absolute for the retained logical stream.
- [ ] Implement event append under the same mutation lock and assert strict sequence ordering.
- [ ] Run tests; expect pass.
- [ ] Commit as `feat: add versioned state logs and events`.

### Task 4: Process execution, detached runner, Job Objects, readiness, and termination

**Files:**
- Create: `src/ExecMcp.Core/WindowsJobObject.cs`
- Create: `src/ExecMcp.Core/ProcessExecutor.cs`
- Create: `src/ExecMcp.Core/JobService.cs`
- Create: `src/ExecMcp.Core/RunnerSpec.cs`
- Create: `src/ExecMcp.Cli/InternalRunner.cs`
- Create: `tests/ExecMcp.Tests/ProcessIntegrationTests.cs`
- Create: `tests/ExecMcp.Tests/JobObjectIntegrationTests.cs`
- Create: `tests/ExecMcp.Tests/ReadinessIntegrationTests.cs`

**Interfaces:**
- Produces: `ProcessExecutor.RunAsync(NormalizedCommand, CancellationToken) -> RunResult`.
- Produces: `JobService.StartAsync`, `StatusAsync`, `OutputAsync`, `WaitAsync`, `KillAsync`, `ListAsync`, `EventsAsync`.
- Runner entry: `execmcp-cs __runner --spec <path>`.

- [ ] Add Windows integration tests for exact direct argv, native exit code 7, Unicode PowerShell, timeout, descendant process creation, and readiness text emitted before enough later output to exceed `max_log_bytes`.
- [ ] Confirm failures before runner/Job Object implementation.
- [ ] P/Invoke `CreateJobObject`, `SetInformationJobObject(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)`, `AssignProcessToJobObject`, `TerminateJobObject`, and `QueryInformationJobObject` as needed.
- [ ] Start background child in the runner, assign it to the Job Object before normal execution, stream stdout/stderr as bytes, feed readiness regex before bounded-log truncation, and persist final state on every exit path.
- [ ] Make `kill` idempotent and verify descendants are gone after Job Object termination.
- [ ] Launch the runner detached from `ExecMcp.Cli` with hidden window and inherited framework availability, so jobs outlive the initiating CLI.
- [ ] Run focused integration tests; expect pass on Windows.
- [ ] Commit as `feat: add job runner and process tree lifecycle`.

### Task 5: CLI contract, profiles, diagnostics, ports, and capture-window

**Files:**
- Create: `src/ExecMcp.Cli/Program.cs`
- Create: `src/ExecMcp.Cli/CliParser.cs`
- Create: `src/ExecMcp.Cli/OutputRenderer.cs`
- Create: `src/ExecMcp.Core/ProfileStore.cs`
- Create: `src/ExecMcp.Core/DoctorService.cs`
- Create: `src/ExecMcp.Core/PortInspector.cs`
- Create: `src/ExecMcp.Core/WindowInspector.cs`
- Create: `src/ExecMcp.Core/WindowCapture.cs`
- Create: `tests/ExecMcp.Tests/ProfileTests.cs`
- Create: `tests/ExecMcp.Tests/CliContractTests.cs`
- Create: `tests/ExecMcp.Tests/WindowSelectionTests.cs`

**Interfaces:**
- Public commands: `run`, `start`, `list`, `status`, `output`, `wait`, `kill`, `shell`, `resolve`, `path-info`, `doctor`, `port-info`, `capture-window`, `events`, `run-config`, `start-config`, `snip`.
- Profile JSON maps exact fields `executable`, `args`, `cwd`, `env`, `timeout_ms`, `max_output_bytes`, `max_log_bytes`, `ready_pattern`, `title`, `allow_appended_args`.

- [ ] Add failing parser/contract tests that assert command option ownership, `--json`, command separator behavior, JSON snake_case names, unknown-command errors, and absence of `mcp`.
- [ ] Add failing profile tests proving executable/cwd/env/base args cannot be overridden and extra args are rejected unless allowed.
- [ ] Implement the parser without an MCP/client indirection; call `ExecMcp.Core` directly and propagate native foreground exit codes.
- [ ] Implement `doctor`, `path-info`, and `port-info` as structured diagnostics with stable JSON names.
- [ ] Implement window enumeration/selection by job, PID, title, or HWND; isolate capture so minimized restoration and foreground opt-out can be tested independently.
- [ ] Capture a window to PNG using DWM/PrintWindow/BitBlt fallback and validate PNG signature in integration tests when a desktop session is available.
- [ ] Run tests; expect pass.
- [ ] Commit as `feat: implement execmcp cli contract and profiles`.

### Task 6: Packaged Snipping Tool launch and silent callback

**Files:**
- Create: `src/ExecMcp.Core/SnippingUriBuilder.cs`
- Create: `src/ExecMcp.Core/SnippingCorrelationStore.cs`
- Create: `src/ExecMcp.SnippingCallback/Program.cs`
- Create: `src/ExecMcp.SnippingCallback/CallbackHandler.cs`
- Create: `tests/ExecMcp.Tests/SnippingUriTests.cs`
- Create: `tests/ExecMcp.Tests/SnippingCallbackTests.cs`

**Interfaces:**
- Produces: `Uri SnippingUriBuilder.Build(SnipMode mode, Guid correlationId)`.
- Produces: `CallbackResult CallbackHandler.Validate(Uri activationUri, IReadOnlySet<Guid> expectedCorrelations)` before token redemption.

- [ ] Add failing tests asserting all four modes map to `ms-screenclip://capture/...` with `api-version=1.2`, `auto-save`, `user-agent=execmcp`, correlation id, and encoded redirect URI.
- [ ] Add failing callback tests for wrong scheme/host, missing/malformed/stale correlation, cancellation, missing token, and a valid token path.
- [ ] Implement CLI `snip` using `Windows.System.Launcher.LaunchUriAsync` and return success immediately when Windows accepts the URI.
- [ ] Persist short-lived correlations under `v2` so the callback can validate them without touching artifacts.
- [ ] Make callback `OutputType=WinExe`; redeem a valid returned token exactly once through `SharedStorageAccessManager.RedeemTokenForFileAsync` and discard only the returned object reference.
- [ ] Run tests; expect pass.
- [ ] Commit as `feat: add snipping tool protocol integration`.

### Task 7: MSIX packaging and current-user scripts

**Files:**
- Create: `src/ExecMcp.Package/ExecMcp.Package.wapproj`
- Create: `src/ExecMcp.Package/Package.appxmanifest`
- Create: `src/ExecMcp.Package/Assets/*`
- Create: `scripts/New-DevCertificate.ps1`
- Create: `scripts/Install-ExecMcp.ps1`
- Create: `scripts/Uninstall-ExecMcp.ps1`
- Create: `scripts/Verify-Package.ps1`

**Interfaces:**
- Package identity publisher exactly matches generated certificate subject.
- Manifest exposes `execmcp.exe` alias and `execmcp-snip` protocol targeting silent callback.

- [ ] Configure the WAP project for x64, framework-dependent published outputs, explicit package version, and no bundle.
- [ ] Register CLI application as `Windows.FullTrustApplication` with `windows.appExecutionAlias` and `desktop:ExecutionAlias Alias="execmcp.exe"`.
- [ ] Register callback application/protocol with `Parameters="&quot;%1&quot;"` so the activation URI reaches the callback process.
- [ ] Implement certificate generation that exports PFX + public CER without trusting the PFX.
- [ ] Implement install script that imports only CER into `Cert:\CurrentUser\TrustedPeople`, installs MSIX, and verifies `Get-Command execmcp`.
- [ ] Implement uninstall script that removes only the package and never removes `%LOCALAPPDATA%\windows-exec-mcp` state.
- [ ] Build package on Windows with MSBuild and run `Verify-Package.ps1`.
- [ ] Commit as `build: add signed msix packaging`.

### Task 8: GitHub Actions build, parity, and migration gate

**Files:**
- Create: `.github/workflows/windows-build.yml`
- Create: `scripts/Compatibility.ps1`
- Create: `compat/expected-json-fields.json`
- Modify: `README.md`
- Delete after gates: `legacy-js/**`

**Interfaces:**
- CI artifacts: `execmcp-cs-unpackaged`, `execmcp-msix`, `compatibility-results`.

- [ ] Add `windows-2025` workflow triggered by push, pull request, and `workflow_dispatch`.
- [ ] Use `actions/setup-dotnet` with `10.0.x`, restore, build Release x64, and run all xUnit tests.
- [ ] Publish unpackaged CLI framework-dependent with assembly/executable named `execmcp-cs` for parity.
- [ ] Generate an ephemeral signing cert, build/sign MSIX, upload MSIX/CER/install/uninstall artifacts.
- [ ] Install MSIX current-user in CI, verify execution alias, exercise non-interactive public commands with `--json`, validate URI construction for all snip modes, and run callback validation tests.
- [ ] Run `Compatibility.ps1` to compare required JSON field sets against `compat/expected-json-fields.json` and any behavior available from the JavaScript reference.
- [ ] Upload compatibility results even on failure.
- [ ] After the complete workflow passes, remove `legacy-js`, Bun/pnpm/MCP files, and document that old user state is intentionally retained.
- [ ] Commit as `ci: build and verify execmcp on windows` followed by `chore: remove javascript implementation` only after green CI.

## Plan self-review

- Spec coverage: all preserved commands, profiles, v2 state, concurrency, Job Objects, logs/cursors/events/readiness, window capture, Snipping Tool, MSIX, install/uninstall, GitHub Actions, and migration gates have explicit tasks.
- Placeholder scan: no TBD/TODO/"implement later" placeholders are present.
- Type consistency: `NormalizedCommand`, `LaunchSpec`, `StateStore`, `JobService`, `SnippingUriBuilder`, and callback interfaces are introduced before consumers.
