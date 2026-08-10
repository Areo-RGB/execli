using System.Globalization;
using System.Text.Json;
using ExecMcp.Core;
using Windows.System;

namespace ExecMcp.Cli;

public static partial class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await stdout.WriteAsync(Usage()).ConfigureAwait(false);
            return 0;
        }

        var command = args[0];
        var rest = args[1..];
        if (command == "mcp") throw new ArgumentException("Unknown command: mcp");
        if (command == "__runner")
        {
            var specIndex = Array.IndexOf(rest, "--spec");
            if (specIndex < 0 || specIndex + 1 >= rest.Length) throw new ArgumentException("Missing --spec for runner");
            return await RunnerEngine.RunAsync(rest[specIndex + 1], cancellationToken).ConfigureAwait(false);
        }

        if (command is "run" or "start")
        {
            var parsed = ParseDirect(rest, command == "run" ? CommandKind.Foreground : CommandKind.Job);
            if (command == "run")
            {
                var started = DateTimeOffset.UtcNow;
                var result = await new ProcessExecutor().RunAsync(parsed.Command, cancellationToken).ConfigureAwait(false);
                var finished = DateTimeOffset.UtcNow;
                if (parsed.Json) await WriteJsonAsync(stdout, RunJson(parsed.Command, result, started, finished)).ConfigureAwait(false);
                else
                {
                    if (result.Stdout.Length > 0) await stdout.WriteAsync(result.Stdout).ConfigureAwait(false);
                    if (result.Stderr.Length > 0) await stderr.WriteAsync(result.Stderr).ConfigureAwait(false);
                }
                return result.State == "completed" ? 0 : result.ExitCode is > 0 ? result.ExitCode.Value : 1;
            }
            var value = await new JobService().StartAsync(parsed.Command, cancellationToken).ConfigureAwait(false);
            await WriteValueAsync(stdout, value, parsed.Json).ConfigureAwait(false);
            return 0;
        }

        if (command == "shell") return await RunShellAsync(rest, stdout, stderr, cancellationToken).ConfigureAwait(false);
        if (command is "run-config" or "start-config") return await RunProfileAsync(command, rest, stdout, stderr, cancellationToken).ConfigureAwait(false);

        var service = new JobService();
        switch (command)
        {
            case "list":
                await WriteValueAsync(stdout, await service.ListAsync(cancellationToken).ConfigureAwait(false), Has(rest, "--json")).ConfigureAwait(false); return 0;
            case "status":
                RequireArg(rest, 0, "job-id");
                await WriteValueAsync(stdout, await service.StatusAsync(rest[0], cancellationToken).ConfigureAwait(false), Has(rest, "--json")).ConfigureAwait(false); return 0;
            case "wait":
            {
                RequireArg(rest, 0, "job-id");
                var timeout = Option(rest, "--timeout") is { } raw ? DurationParser.Parse(raw) : 30_000;
                await WriteValueAsync(stdout, await service.WaitAsync(rest[0], timeout, cancellationToken).ConfigureAwait(false), Has(rest, "--json")).ConfigureAwait(false); return 0;
            }
            case "kill":
                RequireArg(rest, 0, "job-id");
                await WriteValueAsync(stdout, await service.KillAsync(rest[0], cancellationToken).ConfigureAwait(false), Has(rest, "--json")).ConfigureAwait(false); return 0;
            case "output":
                return await RunOutputAsync(rest, service, stdout, cancellationToken).ConfigureAwait(false);
            case "events":
            {
                var id = rest.Length > 0 && !rest[0].StartsWith('-') ? rest[0] : null;
                var after = Option(rest, "--after") is { } raw ? long.Parse(raw, CultureInfo.InvariantCulture) : 0;
                await WriteValueAsync(stdout, await service.EventsAsync(id, after, cancellationToken).ConfigureAwait(false), true).ConfigureAwait(false); return 0;
            }
            case "resolve":
                RequireArg(rest, 0, "executable"); await WriteValueAsync(stdout, DiagnosticsService.Resolve(rest[0]), Has(rest, "--json")).ConfigureAwait(false); return 0;
            case "path-info":
                RequireArg(rest, 0, "path"); await WriteValueAsync(stdout, DiagnosticsService.PathInfo(rest[0]), Has(rest, "--json")).ConfigureAwait(false); return 0;
            case "doctor":
                await WriteValueAsync(stdout, DiagnosticsService.Doctor(), Has(rest, "--json")).ConfigureAwait(false); return 0;
            case "port-info":
                RequireArg(rest, 0, "port");
                await WriteValueAsync(stdout, await PortInspector.InspectAsync(int.Parse(rest[0], CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false), Has(rest, "--json")).ConfigureAwait(false); return 0;
            case "capture-window":
                return await RunCaptureAsync(rest, stdout, cancellationToken).ConfigureAwait(false);
            case "snip":
                return await RunSnipAsync(rest, stdout, cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentException($"Unknown command: {command}");
        }
    }
}
