using System.Globalization;
using System.Text.Json;
using ExecMcp.Core;

namespace ExecMcp.Cli;

public static partial class CliApp
{
    private static (NormalizedCommand Command, bool Json) ParseDirect(string[] tokens, CommandKind kind)
    {
        var separator = Array.IndexOf(tokens, "--");
        string[] options;
        string[] command;
        if (separator >= 0) { options = tokens[..separator]; command = tokens[(separator + 1)..]; }
        else
        {
            var firstCommand = Array.FindIndex(tokens, token => !token.StartsWith('-'));
            if (firstCommand < 0) throw new ArgumentException("Add a command after --");
            var knownValueOptions = new HashSet<string> { "--cwd", "--timeout", "--max-output", "--max-log", "--ready", "--title", "--env" };
            var index = 0;
            while (index < tokens.Length)
            {
                if (tokens[index] == "--json") { index++; continue; }
                if (knownValueOptions.Contains(tokens[index])) { index += 2; continue; }
                break;
            }
            options = tokens[..index]; command = tokens[index..];
        }
        if (command.Length == 0) throw new ArgumentException("Add a command after --");
        var common = CommonRequest(options);
        common = common with { Shell = ShellKind.None, Executable = command[0], Args = command[1..] };
        return (CommandValidator.Normalize(ToRequest(common), kind), Has(options, "--json"));
    }

    private sealed record RequestBuilder(ShellKind Shell = ShellKind.None, string? Executable = null, IReadOnlyList<string>? Args = null, string? Command = null, string? Cwd = null, IReadOnlyDictionary<string,string>? Env = null, int? TimeoutMs = null, int? MaxOutputBytes = null, long? MaxLogBytes = null, string? ReadyPattern = null, string? Title = null);

    private static RequestBuilder CommonRequest(string[] options)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? cwd = null, ready = null, title = null; int? timeout = null, maxOutput = null; long? maxLog = null;
        for (var i = 0; i < options.Length; i++)
        {
            var token = options[i];
            if (token == "--json") continue;
            if (token == "--command") { i++; continue; }
            string Value() { if (++i >= options.Length) throw new ArgumentException($"Missing value for {token}"); return options[i]; }
            switch (token)
            {
                case "--cwd": cwd = Value(); break;
                case "--timeout": timeout = DurationParser.Parse(Value()); break;
                case "--max-output": maxOutput = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--max-log": maxLog = long.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--ready": ready = Value(); break;
                case "--title": title = Value(); break;
                case "--env":
                {
                    var value = Value(); var equals = value.IndexOf('='); if (equals < 1) throw new ArgumentException("--env must be NAME=VALUE"); env[value[..equals]] = value[(equals + 1)..]; break;
                }
                default: throw new ArgumentException($"Unknown option: {token}");
            }
        }
        return new RequestBuilder(Cwd: cwd, Env: env, TimeoutMs: timeout, MaxOutputBytes: maxOutput, MaxLogBytes: maxLog, ReadyPattern: ready, Title: title);
    }

    private static CommandRequest ToRequest(RequestBuilder value) => new()
    {
        Shell = value.Shell, Executable = value.Executable, Args = value.Args, Command = value.Command, Cwd = value.Cwd, Env = value.Env,
        TimeoutMs = value.TimeoutMs, MaxOutputBytes = value.MaxOutputBytes, MaxLogBytes = value.MaxLogBytes, ReadyPattern = value.ReadyPattern, Title = value.Title
    };

    private static Dictionary<string, object?> RunJson(NormalizedCommand command, RunResult result, DateTimeOffset started, DateTimeOffset finished) => new()
    {
        ["id"] = null, ["state"] = result.State, ["pid"] = null, ["runner_pid"] = null,
        ["exit_code"] = result.ExitCode, ["signal"] = result.Signal, ["error"] = result.Error,
        ["shell"] = command.Shell switch { ShellKind.None => "none", ShellKind.PowerShell => "powershell", ShellKind.Cmd => "cmd", ShellKind.GitBash => "git-bash", _ => "none" },
        ["executable"] = command.Shell == ShellKind.None ? command.Executable : null,
        ["args"] = command.Shell == ShellKind.None ? command.Args : null,
        ["command"] = command.Shell == ShellKind.None ? null : command.Command, ["cwd"] = command.Cwd,
        ["created_at"] = started, ["started_at"] = started, ["finished_at"] = finished, ["timeout_ms"] = command.TimeoutMs,
        ["stdout_bytes"] = result.StdoutBytes, ["stderr_bytes"] = result.StderrBytes,
        ["stdout_tail"] = result.Stdout, ["stderr_tail"] = result.Stderr,
        ["stdout"] = result.Stdout, ["stderr"] = result.Stderr, ["ready"] = result.Ready
    };

    private static nint ParseHwnd(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return (nint)long.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (nint)long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool Has(IEnumerable<string> tokens, string option) => tokens.Contains(option, StringComparer.Ordinal);
    private static string? Option(string[] tokens, string option)
    {
        var index = Array.IndexOf(tokens, option);
        if (index < 0) return null;
        if (index + 1 >= tokens.Length) throw new ArgumentException($"Missing value for {option}");
        return tokens[index + 1];
    }
    private static void RequireArg(string[] rest, int index, string label) { if (rest.Length <= index || rest[index].StartsWith('-')) throw new ArgumentException($"Missing {label}"); }

    private static Task WriteValueAsync(TextWriter writer, object value, bool json) => WriteJsonAsync(writer, value);
    private static async Task WriteJsonAsync(TextWriter writer, object value) => await writer.WriteLineAsync(JsonSerializer.Serialize(value, JsonSupport.Options)).ConfigureAwait(false);

    private static string Usage() => """
execmcp - Windows command execution CLI

Usage:
  execmcp run [options] -- executable [args...]
  execmcp start [options] -- executable [args...]
  execmcp run-config <name> [--json] [-- extra-args]
  execmcp start-config <name> [--json] [-- extra-args]
  execmcp shell <powershell|cmd|git-bash> [--command text]
  execmcp list [--json]
  execmcp status <job-id> [--json]
  execmcp output <job-id> [--stream stdout|stderr] [--offset bytes] [--follow] [--json]
  execmcp wait <job-id> [--timeout 30s] [--json]
  execmcp kill <job-id> [--json]
  execmcp resolve <executable> [--json]
  execmcp path-info <path> [--json]
  execmcp doctor [--json]
  execmcp port-info <port> [--json]
  execmcp capture-window (--job id|--pid pid|--title text|--hwnd hwnd) [--output file] [--no-foreground] [--json]
  execmcp events [job-id] [--after sequence] [--json]
  execmcp snip --mode <rectangle|freeform|window|video> [--json]

Common run/start options: --cwd, --timeout, --max-output, --max-log, --ready, --title, --env NAME=VALUE, --json
""";
}
