import test from "node:test";
import assert from "node:assert/strict";
import os from "node:os";
import path from "node:path";
import fs from "node:fs/promises";
import { JobManager } from "../src/job-manager.js";

async function manager() {
  const stateDir = await fs.mkdtemp(path.join(os.tmpdir(), "windows-exec-mcp-"));
  return new JobManager({ stateDir });
}

test("direct argv preserves shell metacharacters", async () => {
  const jobs = await manager();
  const record = await jobs.start({ executable: process.execPath, args: ["-e", "process.stdout.write(process.argv.slice(1).join('|'))", "--", "a & b", "quo\"te"] });
  const result = await jobs.wait(record.id, 10000);
  assert.equal(result.state, "completed");
  assert.match(result.stdout_tail, /a & b\|quo"te/);
});

test("nonzero exit is reported", async () => {
  const jobs = await manager();
  const record = await jobs.start({ executable: process.execPath, args: ["-e", "process.exit(7)"] });
  const result = await jobs.wait(record.id, 10000);
  assert.equal(result.state, "failed");
  assert.equal(result.exit_code, 7);
});

test("timeout stops a job", async () => {
  const jobs = await manager();
  const record = await jobs.start({ executable: process.execPath, args: ["-e", "setTimeout(()=>{}, 60000)"], timeout_ms: 300 });
  const result = await jobs.wait(record.id, 10000);
  assert.equal(result.state, "timed_out");
});

test("output supports offsets", async () => {
  const jobs = await manager();
  const record = await jobs.start({ executable: process.execPath, args: ["-e", "process.stdout.write('abcdef')"] });
  await jobs.wait(record.id, 10000);
  const first = await jobs.output(record.id, { offset: 0, max_bytes: 3 });
  const second = await jobs.output(record.id, { offset: first.next_offset, max_bytes: 3 });
  assert.equal(first.text, "abc");
  assert.equal(second.text, "def");
});

test("command resolution finds the active Node runtime", async () => {
  const jobs = await manager();
  const result = await jobs.resolve("node");
  assert.match(result.resolved_path.toLowerCase(), /node(\.exe)?$/);
});
