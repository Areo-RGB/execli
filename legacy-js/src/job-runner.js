import fs from "node:fs";
import fsp from "node:fs/promises";
import { spawn } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { buildLaunch } from "./process-spec.js";

const now = () => new Date().toISOString();
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function readState(file) { return JSON.parse(await fsp.readFile(file, "utf8")); }
async function writeState(file, state) {
  const temp = `${file}.runner.tmp`;
  await fsp.writeFile(temp, JSON.stringify(state, null, 2), "utf8");
  await fsp.rename(temp, file);
}
async function updateJob(stateFile, id, patch) {
  const state = await readState(stateFile);
  const record = state.jobs.find((item) => item.id === id);
  if (!record) throw new Error(`Unknown job: ${id}`);
  Object.assign(record, patch);
  await writeState(stateFile, state);
  return record;
}
async function killTree(pid) {
  if (!pid) return;
  await new Promise((resolve) => {
    const killer = spawn("taskkill.exe", ["/PID", String(pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" });
    const timer = setTimeout(resolve, 5_000);
    const done = () => { clearTimeout(timer); resolve(); };
    killer.once("close", done); killer.once("error", done);
  });
}
async function stats(stdoutPath, stderrPath) {
  const value = { stdoutBytes: 0, stderrBytes: 0 };
  try { value.stdoutBytes = (await fsp.stat(stdoutPath)).size; } catch {}
  try { value.stderrBytes = (await fsp.stat(stderrPath)).size; } catch {}
  return value;
}

async function run(specPath) {
  const spec = JSON.parse(await fsp.readFile(specPath, "utf8"));
  const launch = buildLaunch(spec);
  await fsp.rm(specPath, { force: true });
  const stdoutFile = fs.createWriteStream(spec.stdoutPath, { flags: "a" });
  const stderrFile = fs.createWriteStream(spec.stderrPath, { flags: "a" });
  let stdoutBytes = 0;
  let stderrBytes = 0;
  const capture = (stream, file, limit, key) => {
    stream.on("data", (chunk) => {
      const used = key === "stdout" ? stdoutBytes : stderrBytes;
      const remaining = Math.max(0, limit - used);
      if (remaining <= 0) return;
      const value = chunk.subarray(0, remaining);
      file.write(value);
      if (key === "stdout") stdoutBytes += value.length; else stderrBytes += value.length;
    });
  };
  let child;
  try {
    child = spawn(launch.executable, launch.args, { cwd: spec.cwd, env: launch.env, windowsHide: true, shell: false, stdio: ["ignore", "pipe", "pipe"] });
    capture(child.stdout, stdoutFile, spec.max_output_bytes, "stdout");
    capture(child.stderr, stderrFile, spec.max_output_bytes, "stderr");
  } catch (error) {
    stdoutFile.end(); stderrFile.end(); throw error;
  }
  const stateReady = updateJob(spec.stateFile, spec.id, { state: "running", pid: child.pid ?? null, runner_pid: process.pid, startedAt: now() });
  let timedOut = false;
  let timer;
  const outputReady = new Promise((resolve) => {
    child.once("close", () => {
      stdoutFile.end(); stderrFile.end();
      Promise.all([new Promise((done) => stdoutFile.once("close", done)), new Promise((done) => stderrFile.once("close", done))]).then(resolve);
    });
  });
  const finish = async (code, signal, error = null) => {
    await stateReady;
    await outputReady;
    if (timer) clearTimeout(timer);
    const state = await readState(spec.stateFile);
    const record = state.jobs.find((item) => item.id === spec.id);
    if (!record) return;
    const sizes = await stats(spec.stdoutPath, spec.stderrPath);
    if (record.state === "killed" || timedOut) {
      Object.assign(record, { state: timedOut ? "timed_out" : "killed", exitCode: code, signal, finishedAt: now(), stdoutBytes: sizes.stdoutBytes, stderrBytes: sizes.stderrBytes });
    } else {
      Object.assign(record, { state: code === 0 ? "completed" : "failed", exitCode: code, signal, error, finishedAt: now(), stdoutBytes: sizes.stdoutBytes, stderrBytes: sizes.stderrBytes });
    }
    await writeState(spec.stateFile, state);
  };
  child.once("error", (error) => { void finish(null, null, error.message); });
  child.once("close", (code, signal) => { void finish(code, signal); });
  await stateReady;
  if (spec.timeout_ms > 0) {
    timer = setTimeout(async () => {
      timedOut = true;
      await updateJob(spec.stateFile, spec.id, { state: "timed_out", error: `Timed out after ${spec.timeout_ms} ms` });
      await delay(100);
      await killTree(child.pid);
    }, spec.timeout_ms);
  }
}

export async function runJobRunner(specPath) {
  if (!specPath) throw new Error("Missing job specification path");
  try {
    await run(specPath);
  } catch (error) {
    try {
      const spec = JSON.parse(await fsp.readFile(specPath, "utf8"));
      await updateJob(spec.stateFile, spec.id, { state: "failed", error: error.message, finishedAt: now() });
    } catch {}
    throw error;
  }
}

if (process.env.EXECMCP_COMPILED !== "1" && process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  runJobRunner(process.argv[2]).catch((error) => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  });
}
