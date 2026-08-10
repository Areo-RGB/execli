import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createMcpServer } from "./mcp-server.js";
import { JobManager } from "./job-manager.js";
import { runMcpServer } from "./mcp-server.js";

function duration(value) {
  if (/^\d+$/.test(value)) return Number(value);
  const match = /^(\d+(?:\.\d+)?)(ms|s|m|h|d)$/.exec(value);
  if (!match) throw new Error(`Invalid duration: ${value}`);
  const factor = { ms: 1, s: 1000, m: 60000, h: 3600000, d: 86400000 }[match[2]];
  return Math.round(Number(match[1]) * factor);
}

function usage() {
  console.log(`execmcp - Windows command execution through MCP

Usage:
  execmcp run [options] -- executable [args...]
  execmcp start [options] -- executable [args...]
  execmcp shell <powershell|cmd|git-bash> [--command text]
  execmcp list [--json]
  execmcp status <job-id> [--json]
  execmcp output <job-id> [--stream stdout|stderr] [--follow]
  execmcp wait <job-id> [--timeout 30s]
  execmcp kill <job-id>
  execmcp resolve <executable>
  execmcp path-info <path>
  execmcp mcp
`);
}

function parseCommand(tokens) {
  const separator = tokens.indexOf("--");
  const options = separator >= 0 ? tokens.slice(0, separator) : tokens;
  let command = separator >= 0 ? tokens.slice(separator + 1) : [];
  const spec = {};
  let json = false;
  for (let i = 0; i < options.length; i += 1) {
    const token = options[i];
    if (token === "--json") json = true;
    else if (token === "--cwd") spec.cwd = options[++i];
    else if (token === "--timeout") spec.timeout_ms = duration(options[++i]);
    else if (token === "--max-output") spec.max_output_bytes = Number(options[++i]);
    else if (separator < 0 && !token.startsWith("-")) {
      command = options.slice(i);
      break;
    } else throw new Error(`Unknown option: ${token}`);
  }
  if (command.length === 0) throw new Error("Add a command after --");
  spec.shell = "none";
  spec.executable = command[0];
  spec.args = command.slice(1);
  return { spec, json };
}

async function callTool(name, args) {
  const manager = new JobManager();
  await manager.ready;
  const server = createMcpServer(manager);
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "execmcp-cli", version: "0.1.0" });
  await server.connect(serverTransport);
  await client.connect(clientTransport);
  try {
    const response = await client.callTool({ name, arguments: args });
    const text = response.content?.find((part) => part.type === "text")?.text;
    if (!text) throw new Error("MCP tool returned no text result");
    const parsed = JSON.parse(text);
    if (parsed?.ok === false) throw new Error(parsed.error);
    return parsed;
  } finally {
    void client.close().catch(() => {});
    void server.close().catch(() => {});
    void manager.close().catch(() => {});
  }
}

function print(value, json = false) {
  if (json) console.log(JSON.stringify(value, null, 2));
  else if (typeof value === "string") process.stdout.write(value);
  else console.log(JSON.stringify(value, null, 2));
}

async function followOutput(id, stream) {
  let offset = 0;
  while (true) {
    const chunk = await callTool("job_output", { id, stream, offset, max_bytes: 65536 });
    if (chunk.text) process.stdout.write(chunk.text);
    offset = chunk.next_offset;
    const status = await callTool("job_status", { id });
    if (status.state !== "running" && status.state !== "queued" && chunk.eof) break;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
}

export async function main(argv = process.argv.slice(2)) {
  const [command, ...rest] = argv;
  if (!command || command === "help" || command === "--help" || command === "-h") return usage();
  if (command === "mcp") return runMcpServer();
  if (command === "run" || command === "start") {
    const { spec, json } = parseCommand(rest);
    const value = await callTool(command === "run" ? "exec" : "job_start", spec);
    if (command === "run") {
      if (json) print(value, true);
      else {
        if (value.stdout) process.stdout.write(value.stdout);
        if (value.stderr) process.stderr.write(value.stderr);
        if (value.state !== "completed") process.exitCode = value.exit_code || 1;
      }
    } else {
      print(value, json);
      if (command === "start") process.exit(0);
    }
    return;
  }
  if (command === "shell") {
    const shell = rest.shift();
    if (!["powershell", "cmd", "git-bash"].includes(shell)) throw new Error("shell must be powershell, cmd, or git-bash");
    const json = rest.includes("--json");
    const index = rest.indexOf("--command");
    let text;
    if (index >= 0) text = rest[index + 1];
    else {
      const separator = rest.indexOf("--");
      text = (separator >= 0 ? rest.slice(separator + 1) : rest).filter((item) => item !== "--json").join(" ");
    }
    if (!text) throw new Error("Add shell text with --command or after --");
    const value = await callTool("exec", { shell, command: text, timeout_ms: 120000 });
    if (json) print(value, true); else { if (value.stdout) process.stdout.write(value.stdout); if (value.stderr) process.stderr.write(value.stderr); }
    return;
  }
  if (command === "list") return print(await callTool("job_list", {}), rest.includes("--json"));
  if (command === "status") return print(await callTool("job_status", { id: rest[0] }), rest.includes("--json"));
  if (command === "wait") {
    const timeoutIndex = rest.indexOf("--timeout");
    const timeout = timeoutIndex >= 0 ? duration(rest[timeoutIndex + 1]) : 30000;
    return print(await callTool("job_wait", { id: rest[0], timeout_ms: timeout }), rest.includes("--json"));
  }
  if (command === "output") {
    const id = rest[0];
    const streamIndex = rest.indexOf("--stream");
    const stream = streamIndex >= 0 ? rest[streamIndex + 1] : "stdout";
    if (rest.includes("--follow")) return followOutput(id, stream);
    const value = await callTool("job_output", { id, stream, offset: 0, max_bytes: 1048576 });
    return print(value.text, false);
  }
  if (command === "kill") {
    print(await callTool("job_kill", { id: rest[0] }), rest.includes("--json"));
    process.exit(0);
  }
  if (command === "resolve") return print(await callTool("command_resolve", { executable: rest[0] }), rest.includes("--json"));
  if (command === "path-info") return print(await callTool("path_info", { path: rest[0] }), rest.includes("--json"));
  throw new Error(`Unknown command: ${command}`);
}
