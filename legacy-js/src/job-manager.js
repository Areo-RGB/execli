import crypto from "node:crypto";
import fs from "node:fs";
import fsp from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync, spawn } from "node:child_process";
import { buildLaunch, defaultStateDir, normalizeSpec, redactText, resolveExecutable } from "./process-spec.js";

const TERMINAL = new Set(["completed", "failed", "timed_out", "killed", "orphaned"]);
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const now = () => new Date().toISOString();
const idFor = () => `job_${Date.now().toString(36)}_${crypto.randomBytes(4).toString("hex")}`;

async function readJson(file) { return JSON.parse(await fsp.readFile(file, "utf8")); }

export class JobManager {
  constructor({ stateDir = defaultStateDir(), maxConcurrent = 8, maxRetained = 128 } = {}) {
    this.stateDir = path.resolve(stateDir);
    this.jobsDir = path.join(this.stateDir, "jobs");
    this.stateFile = path.join(this.stateDir, "jobs.json");
    this.runnerEntry = fileURLToPath(new URL("./job-runner.js", import.meta.url));
    this.compiled = process.env.EXECMCP_COMPILED === "1";
    this.maxConcurrent = maxConcurrent;
    this.maxRetained = maxRetained;
    this.jobs = new Map();
    this.ready = this.initialize();
  }

  async initialize() {
    await fsp.mkdir(this.jobsDir, { recursive: true });
    await this.reload();
    await this.persist();
  }

  async reload() {
    try {
      const parsed = await readJson(this.stateFile);
      this.jobs.clear();
      for (const record of Array.isArray(parsed.jobs) ? parsed.jobs : []) this.jobs.set(record.id, record);
    } catch (error) {
      if (error.code !== "ENOENT") throw new Error(`Could not read job state: ${error.message}`);
    }
  }

  async persist() {
    const records = [...this.jobs.values()].slice(-this.maxRetained);
    const temp = `${this.stateFile}.tmp`;
    await fsp.writeFile(temp, JSON.stringify({ version: 1, jobs: records }, null, 2), "utf8");
    await fsp.rename(temp, this.stateFile);
  }

  runningCount() {
    return [...this.jobs.values()].filter((record) => record.state === "running").length;
  }

  async start(input) {
    await this.ready;
    await this.reload();
    if (this.runningCount() >= this.maxConcurrent) throw new Error(`Maximum concurrent jobs reached (${this.maxConcurrent})`);
    const spec = normalizeSpec(input, { kind: "job" });
    const id = idFor();
    const stdoutPath = path.join(this.jobsDir, `${id}.stdout.log`);
    const stderrPath = path.join(this.jobsDir, `${id}.stderr.log`);
    const specPath = path.join(this.jobsDir, `${id}.spec.json`);
    const runnerLogPath = path.join(this.jobsDir, `${id}.runner.log`);
    const record = {
      id, state: "queued", createdAt: now(), startedAt: null, finishedAt: null,
      pid: null, runner_pid: null, exitCode: null, signal: null, error: null,
      shell: spec.shell, executable: spec.shell === "none" ? spec.executable : null,
      args: spec.shell === "none" ? spec.args : null, command: spec.shell === "none" ? null : spec.command,
      cwd: spec.cwd, stdoutPath, stderrPath, runnerLogPath, stdoutBytes: 0, stderrBytes: 0,
      timeoutMs: spec.timeoutMs, maxOutputBytes: spec.maxOutputBytes,
    };
    this.jobs.set(id, record);
    await fsp.writeFile(stdoutPath, "");
    await fsp.writeFile(stderrPath, "");
    await fsp.writeFile(specPath, JSON.stringify({
      id, stateFile: this.stateFile, stdoutPath, stderrPath, runnerLogPath,
      shell: spec.shell, executable: spec.executable, args: spec.args, command: spec.command,
      cwd: spec.cwd, env: spec.env, timeout_ms: spec.timeoutMs, max_output_bytes: spec.maxOutputBytes,
    }), "utf8");
    record.state = "running";
    record.startedAt = now();
    await this.persist();

    try {
      const runnerLogFd = fs.openSync(runnerLogPath, "a");
      const runnerArgs = this.compiled ? ["__job-runner", specPath] : [this.runnerEntry, specPath];
      const runner = spawn(process.execPath, runnerArgs, {
        cwd: this.stateDir,
        detached: true,
        windowsHide: true,
        shell: false,
        stdio: ["ignore", runnerLogFd, runnerLogFd],
        env: process.env,
      });
      fs.closeSync(runnerLogFd);
      record.runner_pid = runner.pid ?? null;
      runner.unref();
      return {
        id: record.id, state: record.state, pid: record.pid, runner_pid: record.runner_pid,
        exit_code: null, signal: null, error: null, shell: record.shell,
        executable: record.executable, args: record.args, command: record.command, cwd: record.cwd,
        created_at: record.createdAt, started_at: record.startedAt, finished_at: null,
        timeout_ms: record.timeoutMs, stdout_bytes: 0, stderr_bytes: 0, stdout_tail: "", stderr_tail: "",
      };
    } catch (error) {
      record.state = "failed";
      record.error = error instanceof Error ? error.message : String(error);
      record.finishedAt = now();
      await this.persist();
      throw new Error(record.error);
    }
  }

  async refresh(record) {
    for (const stream of ["stdout", "stderr"]) {
      try { record[`${stream}Bytes`] = (await fsp.stat(record[`${stream}Path`])).size; } catch {}
    }
    return record;
  }

  runnerAlive(pid) {
    if (!pid) return false;
    try {
      const output = execFileSync("tasklist.exe", ["/FI", `PID eq ${pid}`, "/FO", "CSV", "/NH"], { encoding: "utf8", windowsHide: true, stdio: ["ignore", "pipe", "ignore"] });
      return output.includes(`"${pid}"`);
    } catch { return false; }
  }

  async status(id) {
    await this.ready;
    await this.reload();
    const record = this.jobs.get(id);
    if (!record) throw new Error(`Unknown job: ${id}`);
    const startedAgeMs = record.startedAt ? Date.now() - Date.parse(record.startedAt) : 0;
    if (record.state === "running" && record.runner_pid && startedAgeMs > 5_000 && !this.runnerAlive(record.runner_pid)) {
      record.state = "orphaned";
      record.error = "The detached supervisor is no longer running";
      record.finishedAt = now();
      await this.persist();
    }
    if (TERMINAL.has(record.state)) await this.refresh(record);
    const [stdoutTail, stderrTail] = TERMINAL.has(record.state)
      ? await Promise.all([this.readTail(record, "stdout", 8_192), this.readTail(record, "stderr", 8_192)])
      : ["", ""];
    return {
      id: record.id, state: record.state, pid: record.pid, runner_pid: record.runner_pid,
      exit_code: record.exitCode, signal: record.signal, error: record.error, shell: record.shell,
      executable: record.executable, args: record.args, command: record.command, cwd: record.cwd,
      created_at: record.createdAt, started_at: record.startedAt, finished_at: record.finishedAt,
      timeout_ms: record.timeoutMs, stdout_bytes: record.stdoutBytes, stderr_bytes: record.stderrBytes,
      stdout_tail: stdoutTail, stderr_tail: stderrTail,
    };
  }

  async list() {
    await this.ready;
    await this.reload();
    const records = [...this.jobs.values()].slice(-this.maxRetained).reverse();
    await Promise.all(records.filter((record) => TERMINAL.has(record.state)).map((record) => this.refresh(record)));
    return records.map((record) => ({ id: record.id, state: record.state, pid: record.pid, runner_pid: record.runner_pid, exit_code: record.exitCode, shell: record.shell, executable: record.executable, cwd: record.cwd, created_at: record.createdAt, started_at: record.startedAt, finished_at: record.finishedAt, stdout_bytes: record.stdoutBytes, stderr_bytes: record.stderrBytes }));
  }

  async readTail(record, stream, maxBytes) {
    try {
      const file = record[`${stream}Path`];
      const stat = await fsp.stat(file);
      const start = Math.max(0, stat.size - maxBytes);
      const handle = await fsp.open(file, "r");
      const buffer = Buffer.alloc(stat.size - start);
      await handle.read(buffer, 0, buffer.length, start);
      await handle.close();
      return redactText(buffer.toString("utf8"));
    } catch { return ""; }
  }

  async output(id, { stream = "stdout", offset = 0, max_bytes = 64 * 1024 } = {}) {
    await this.ready;
    await this.reload();
    const record = this.jobs.get(id);
    if (!record) throw new Error(`Unknown job: ${id}`);
    if (!["stdout", "stderr"].includes(stream)) throw new Error("stream must be stdout or stderr");
    if (!Number.isInteger(offset) || offset < 0) throw new Error("offset must be a non-negative integer");
    if (!Number.isInteger(max_bytes) || max_bytes < 1 || max_bytes > 1024 * 1024) throw new Error("max_bytes must be 1..1048576");
    try {
      const stat = await fsp.stat(record[`${stream}Path`]);
      const start = Math.min(offset, stat.size);
      const length = Math.min(max_bytes, stat.size - start);
      const handle = await fsp.open(record[`${stream}Path`], "r");
      const buffer = Buffer.alloc(length);
      await handle.read(buffer, 0, length, start);
      await handle.close();
      return { id, stream, offset: start, next_offset: start + length, bytes: length, eof: start + length >= stat.size, text: redactText(buffer.toString("utf8")) };
    } catch (error) { throw new Error(`Could not read ${stream} output: ${error.message}`); }
  }

  async wait(id, timeoutMs = 30_000) {
    await this.ready;
    if (!Number.isInteger(timeoutMs) || timeoutMs < 0 || timeoutMs > 10 * 60 * 1000) throw new Error("wait timeout must be 0..600000 ms");
    const deadline = Date.now() + timeoutMs;
    while (true) {
      const current = await this.status(id);
      if (TERMINAL.has(current.state)) { await delay(50); return this.status(id); }
      if (Date.now() >= deadline) return current;
      await delay(Math.min(200, Math.max(1, deadline - Date.now())));
    }
  }

  async killTree(pid) {
    if (!pid) return;
    await new Promise((resolve) => {
      const killer = spawn("taskkill.exe", ["/PID", String(pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" });
      const timer = setTimeout(resolve, 5_000);
      const done = () => { clearTimeout(timer); resolve(); };
      killer.once("close", done); killer.once("error", done);
    });
  }

  async kill(id) {
    await this.ready;
    const record = this.jobs.get(id);
    if (!record) throw new Error(`Unknown job: ${id}`);
    if (!TERMINAL.has(record.state)) {
      record.state = "killed";
      record.error = "Killed by request";
      record.finishedAt = now();
      await this.persist();
      await this.killTree(record.runner_pid || record.pid);
    }
    return this.status(id);
  }

  async resolve(executable) { return { executable, resolved_path: resolveExecutable(executable) }; }

  async pathInfo(inputPath) {
    const normalized = path.resolve(inputPath);
    try {
      const stat = await fsp.stat(normalized);
      return { path: inputPath, normalized_path: normalized, exists: true, type: stat.isDirectory() ? "directory" : stat.isFile() ? "file" : "other", size: stat.isFile() ? stat.size : null, accessible: true };
    } catch (error) { return { path: inputPath, normalized_path: normalized, exists: false, type: null, size: null, accessible: false, error: error.code }; }
  }

  async close() { await this.ready; await this.persist(); }
}
