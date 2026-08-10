import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

test("detached runner can execute Git Bash", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "execmcp-runner-"));
  const stateFile = path.join(root, "jobs.json");
  const stdoutPath = path.join(root, "stdout.log");
  const stderrPath = path.join(root, "stderr.log");
  const runnerLogPath = path.join(root, "runner.log");
  const specPath = path.join(root, "spec.json");
  const id = "debug-job";
  await fs.writeFile(stateFile, JSON.stringify({ version: 1, jobs: [{ id, state: "queued", stdoutPath, stderrPath, runnerLogPath }] }));
  await fs.writeFile(stdoutPath, ""); await fs.writeFile(stderrPath, "");
  await fs.writeFile(specPath, JSON.stringify({ id, stateFile, stdoutPath, stderrPath, runnerLogPath, shell: "git-bash", command: "printf bash-ok", cwd: process.cwd(), env: {}, timeout_ms: 10000, max_output_bytes: 262144 }));
  const runnerEntry = fileURLToPath(new URL("../src/job-runner.js", import.meta.url));
  const exit = await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [runnerEntry, specPath], { cwd: root, env: process.env, windowsHide: true, stdio: "inherit" });
    child.once("error", reject); child.once("close", (code) => resolve(code));
  });
  const state = JSON.parse(await fs.readFile(stateFile, "utf8"));
  assert.equal(exit, 0);
  assert.equal(state.jobs[0].state, "completed");
  assert.equal(await fs.readFile(stdoutPath, "utf8"), "bash-ok");
});
