import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

export const SHELLS = ["none", "powershell", "cmd", "git-bash"];
export const DEFAULT_EXEC_TIMEOUT_MS = 120_000;
export const MAX_TIMEOUT_MS = 7 * 24 * 60 * 60 * 1000;
export const DEFAULT_MAX_OUTPUT_BYTES = 256 * 1024;
export const MAX_OUTPUT_BYTES = 16 * 1024 * 1024;

function ensureString(value, label) {
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`${label} must be a non-empty string`);
  }
  if (value.includes("\0")) {
    throw new Error(`${label} cannot contain a NUL character`);
  }
  return value;
}

function normalizeTimeout(value, defaultValue) {
  if (value === undefined || value === null) return defaultValue;
  if (!Number.isInteger(value) || value < 0 || value > MAX_TIMEOUT_MS) {
    throw new Error(`timeout_ms must be an integer from 0 to ${MAX_TIMEOUT_MS}`);
  }
  return value;
}

function normalizeEnvironment(value) {
  if (value === undefined) return {};
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("env must be an object of string values");
  }
  const env = {};
  for (const [key, rawValue] of Object.entries(value)) {
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) {
      throw new Error(`Invalid environment variable name: ${key}`);
    }
    if (typeof rawValue !== "string" || rawValue.includes("\0")) {
      throw new Error(`Environment value for ${key} must be a NUL-free string`);
    }
    env[key] = rawValue;
  }
  return env;
}

function validateCwd(cwd) {
  const normalized = path.resolve(cwd ?? process.cwd());
  let stat;
  try {
    stat = fs.statSync(normalized);
  } catch {
    throw new Error(`Working directory does not exist: ${normalized}`);
  }
  if (!stat.isDirectory()) throw new Error(`Working directory is not a directory: ${normalized}`);
  return normalized;
}

export function normalizeSpec(input, { kind = "job" } = {}) {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    throw new Error("Command specification must be an object");
  }
  const shell = input.shell ?? "none";
  if (!SHELLS.includes(shell)) throw new Error(`shell must be one of: ${SHELLS.join(", ")}`);

  const cwd = validateCwd(input.cwd);
  const env = normalizeEnvironment(input.env);
  const timeoutMs = normalizeTimeout(input.timeout_ms, kind === "exec" ? DEFAULT_EXEC_TIMEOUT_MS : 0);
  const maxOutputBytes = input.max_output_bytes === undefined
    ? DEFAULT_MAX_OUTPUT_BYTES
    : input.max_output_bytes;
  if (!Number.isInteger(maxOutputBytes) || maxOutputBytes < 1024 || maxOutputBytes > MAX_OUTPUT_BYTES) {
    throw new Error(`max_output_bytes must be an integer from 1024 to ${MAX_OUTPUT_BYTES}`);
  }

  if (shell === "none") {
    const executable = ensureString(input.executable, "executable");
    if (!Array.isArray(input.args ?? [])) throw new Error("args must be an array of strings");
    const args = (input.args ?? []).map((arg, index) => ensureString(String(arg), `args[${index}]`));
    return { shell, executable, args, cwd, env, timeoutMs, maxOutputBytes };
  }

  const command = ensureString(input.command, "command");
  return { shell, command, cwd, env, timeoutMs, maxOutputBytes };
}

function firstPathLine(output) {
  return output.split(/\r?\n/).map((line) => line.trim()).find(Boolean);
}

export function resolveExecutable(executable, env = process.env) {
  ensureString(executable, "executable");
  const hasPath = executable.includes("\\") || executable.includes("/") || path.isAbsolute(executable);
  if (hasPath) {
    const candidate = path.resolve(executable);
    if (fs.existsSync(candidate)) return candidate;
    for (const extension of [".exe", ".cmd", ".bat"]) {
      if (fs.existsSync(`${candidate}${extension}`)) return `${candidate}${extension}`;
    }
    throw new Error(`Executable does not exist: ${candidate}`);
  }

  const effectivePath = env.Path ?? env.PATH ?? process.env.Path ?? process.env.PATH ?? "";
  const candidates = [executable, `${executable}.exe`, `${executable}.cmd`, `${executable}.bat`];
  for (const directory of effectivePath.split(path.delimiter)) {
    if (!directory) continue;
    for (const candidate of candidates) {
      const resolved = path.join(directory, candidate);
      if (fs.existsSync(resolved)) return resolved;
    }
  }
  try {
    const output = execFileSync("where.exe", [executable], {
      encoding: "utf8", env: { ...process.env, ...env }, windowsHide: true,
      timeout: 3_000, stdio: ["ignore", "pipe", "ignore"],
    });
    const resolved = firstPathLine(output);
    if (resolved) return resolved;
  } catch {
    // Fall through to a clear error rather than letting spawn report a vague ENOENT.
  }
  throw new Error(`Executable was not found on PATH: ${executable}`);
}

function shellExecutable(shell, env) {
  if (shell === "cmd") return resolveExecutable("cmd.exe", env);
  if (shell === "powershell") {
    try { return resolveExecutable("pwsh.exe", env); } catch { return resolveExecutable("powershell.exe", env); }
  }
  if (shell === "git-bash") {
    for (const candidate of [
      "C:\\Program Files\\Git\\usr\\bin\\sh.exe",
      "C:\\Program Files\\Git\\bin\\bash.exe",
      "C:\\Program Files (x86)\\Git\\usr\\bin\\sh.exe",
    ]) {
      if (fs.existsSync(candidate)) return candidate;
    }
    const effectivePath = env.Path ?? env.PATH ?? "";
    for (const directory of effectivePath.split(path.delimiter)) {
      if (!directory.toLowerCase().includes("\\git\\")) continue;
      const candidate = path.join(path.dirname(directory), "usr", "bin", "sh.exe");
      if (fs.existsSync(candidate)) return candidate;
    }
    throw new Error("Git Bash was not found; refusing to use WSL bash.exe as a substitute");
  }
  throw new Error(`Unsupported shell: ${shell}`);
}

export function buildLaunch(spec) {
  const env = { ...process.env, ...spec.env };
  if (spec.shell === "none") {
    return { executable: resolveExecutable(spec.executable, env), args: spec.args, env, shell: spec.shell };
  }
  const executable = shellExecutable(spec.shell, env);
  if (spec.shell === "cmd") return { executable, args: ["/d", "/s", "/c", spec.command], env, shell: spec.shell };
  if (spec.shell === "git-bash") {
    if (!env.HOME) env.HOME = env.USERPROFILE || `${env.HOMEDRIVE || "C:"}${env.HOMEPATH || "\\"}`;
    return { executable, args: ["-c", spec.command], env, shell: spec.shell };
  }
  return { executable, args: ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", spec.command], env, shell: spec.shell };
}

export function defaultStateDir() {
  return path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local"), "windows-exec-mcp");
}

export function redactText(value) {
  return String(value)
    .replace(/(api[_-]?key|access[_-]?token|auth[_-]?token|password|secret|authorization)\s*[:=]\s*([^\s,;]+)/gi, "$1=[REDACTED]")
    .replace(/(Bearer\s+)[A-Za-z0-9._~+\-/]+=*/gi, "$1[REDACTED]");
}
