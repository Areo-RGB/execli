import test from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { fileURLToPath } from "node:url";

test("stdio MCP server advertises the execution tools", async () => {
  const entry = fileURLToPath(new URL("../src/main.js", import.meta.url));
  const client = new Client({ name: "test-client", version: "0.1.0" });
  const transport = new StdioClientTransport({ command: process.execPath, args: [entry, "mcp"], env: process.env, stderr: "pipe" });
  await client.connect(transport);
  const tools = await client.listTools();
  const names = tools.tools.map((tool) => tool.name).sort();
  assert.deepEqual(names, ["command_resolve", "exec", "job_kill", "job_list", "job_output", "job_start", "job_status", "job_wait", "path_info"]);
  await client.close();
});
