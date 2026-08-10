import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { JobManager } from "./job-manager.js";

const commandInput = {
  shell: z.enum(["none", "powershell", "cmd", "git-bash"]).optional().describe("Use none for direct argv execution; other values run explicit shell text"),
  executable: z.string().optional(),
  args: z.array(z.string()).optional(),
  command: z.string().optional(),
  cwd: z.string().optional(),
  env: z.record(z.string()).optional(),
  timeout_ms: z.number().int().min(0).max(604800000).optional(),
  max_output_bytes: z.number().int().min(1024).max(16777216).optional(),
};

function result(value) { return { content: [{ type: "text", text: JSON.stringify(value, null, 2) }] }; }
function failure(error) { return result({ ok: false, error: error instanceof Error ? error.message : String(error) }); }
function safe(handler) { return async (input) => { try { return result(await handler(input ?? {})); } catch (error) { return failure(error); } }; }

export function createMcpServer(manager = new JobManager()) {
  const server = new McpServer({ name: "windows-exec-mcp", version: "0.1.0" });
  server.registerTool("exec", { description: "Run a short Windows command and wait for completion. Direct argv execution is the default; shell text requires an explicit shell.", inputSchema: commandInput }, safe(async (input) => {
    const job = await manager.start({ ...input, timeout_ms: input.timeout_ms ?? 120000 });
    const status = await manager.wait(job.id, Math.min((input.timeout_ms ?? 120000) + 1000, 600000));
    return { ...status, stdout: status.stdout_tail, stderr: status.stderr_tail };
  }));
  server.registerTool("job_start", { description: "Start a Windows command in the background and return a stable job ID.", inputSchema: commandInput }, safe((input) => manager.start(input)));
  server.registerTool("job_status", { description: "Get status and bounded output tails for a background job.", inputSchema: { id: z.string() } }, safe((input) => manager.status(input.id)));
  server.registerTool("job_output", { description: "Read bounded stdout or stderr from a job using a byte offset.", inputSchema: { id: z.string(), stream: z.enum(["stdout", "stderr"]).optional(), offset: z.number().int().min(0).optional(), max_bytes: z.number().int().min(1).max(1048576).optional() } }, safe((input) => manager.output(input.id, input)));
  server.registerTool("job_wait", { description: "Wait for a bounded interval and return current job status.", inputSchema: { id: z.string(), timeout_ms: z.number().int().min(0).max(600000).optional() } }, safe((input) => manager.wait(input.id, input.timeout_ms ?? 30000)));
  server.registerTool("job_list", { description: "List active and recently completed jobs.", inputSchema: {} }, safe(() => manager.list()));
  server.registerTool("job_kill", { description: "Terminate a Windows process tree for a background job.", inputSchema: { id: z.string() } }, safe((input) => manager.kill(input.id)));
  server.registerTool("command_resolve", { description: "Resolve a command using the MCP server's actual Windows PATH.", inputSchema: { executable: z.string() } }, safe((input) => manager.resolve(input.executable)));
  server.registerTool("path_info", { description: "Inspect a local Windows path without modifying it.", inputSchema: { path: z.string() } }, safe((input) => manager.pathInfo(input.path)));
  return server;
}

export async function runMcpServer() {
  const manager = new JobManager();
  await manager.ready;
  const server = createMcpServer(manager);
  const transport = new StdioServerTransport();
  await server.connect(transport);
  let closing = false;
  const close = async () => {
    if (closing) return;
    closing = true;
    await manager.close();
    process.exit(0);
  };
  process.stdin.once("end", close);
  process.stdin.once("close", close);
  process.once("SIGINT", close);
  process.once("SIGTERM", close);
}
