# Bun Standalone Executable Implementation Plan

> **For agentic workers:** Execute the checked steps in this plan in order.

**Goal:** Build `windows-exec-mcp` as one Windows x64 `.exe` with Bun's runtime and application bundle embedded, while keeping background jobs operational from that same executable.

**Architecture:** Add a compiled entrypoint that marks the process as compiled and dispatches both normal CLI/MCP commands and an internal `__job-runner` command. The job manager will spawn the same executable for background supervision when compiled, while source-mode development will continue to spawn the existing JavaScript runner. Bun's `--compile` output will be written to `dist/execmcp.exe`.

**Tech Stack:** Bun 1.3.x compile target `bun-windows-x64`, existing Node-compatible MCP SDK, Node-compatible source runtime for development/tests.

## Global Constraints

- Keep existing API-key-related environment variables and inherited command environments unchanged.
- Preserve direct argv execution and explicit PowerShell, cmd, and Git Bash shell modes.
- Preserve the source-mode `pnpm test` and `pnpm run check` workflow.
- The standalone artifact must not require Node, pnpm, or Bun to launch the MCP server or CLI.
- Child commands requested by the user remain external programs and must still resolve through PATH.

### Task 1: Add compiled entrypoint and same-executable job runner

**Files:**
- Create: `src/compiled-entry.js`
- Modify: `src/main.js`
- Modify: `src/job-runner.js`
- Modify: `src/job-manager.js`
- Test: `test/job-manager.test.js`

- [ ] Add an exported `runJobRunner(specPath)` function and remove job-runner execution at import time.
- [ ] Dispatch `__job-runner <spec-path>` in `src/main.js`.
- [ ] Set `EXECMCP_COMPILED=1` before dynamically importing `src/main.js` from the compiled entrypoint.
- [ ] Make `JobManager.start()` spawn `["__job-runner", specPath]` from the compiled executable and retain `[runnerEntry, specPath]` for source mode.
- [ ] Add a regression test that starts a short command through the compiled artifact after it exists.

### Task 2: Add Bun build metadata and documentation

**Files:**
- Modify: `package.json`
- Modify: `.gitignore`
- Modify: `README.md`

- [ ] Add `build:exe` using `bun build --compile --target=bun-windows-x64 src/compiled-entry.js --outfile dist/execmcp.exe`.
- [ ] Ignore generated `dist/` output while leaving the local executable available.
- [ ] Document standalone build, CLI, and MCP invocation examples and state that the embedded runtime is Bun rather than a separate Node executable.

### Task 3: Verify source and compiled workflows

- [ ] Run `pnpm run check`.
- [ ] Run `pnpm test`.
- [ ] Build `dist/execmcp.exe` with Bun.
- [ ] Run the compiled executable for `resolve`, direct `run`, `start`/`wait`/`output`, and `mcp` handshake smoke tests.
- [ ] Run `git diff --check` and inspect the final diff.
