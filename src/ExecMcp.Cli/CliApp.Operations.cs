using System.Globalization;
using ExecMcp.Core;
using Windows.System;

namespace ExecMcp.Cli;

public static partial class CliApp
{
    private static async Task<int> RunShellAsync(string[] rest, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        RequireArg(rest, 0, "shell");
        var shell = rest[0] switch { "powershell" => ShellKind.PowerShell, "cmd" => ShellKind.Cmd, "git-bash" => ShellKind.GitBash, _ => throw new ArgumentException("shell must be powershell, cmd, or git-bash") };
        var tokens = rest[1..];
        var separator = Array.IndexOf(tokens, "--");
        var options = separator >= 0 ? tokens[..separator] : tokens;
        var text = Option(options, "--command") ?? (separator >= 0 ? string.Join(' ', tokens[(separator + 1)..]) : null);
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Add shell text with --command or after --");
        var request = CommonRequest(options) with { Shell = shell, Command = text };
        var normalized = CommandValidator.Normalize(ToRequest(request), CommandKind.Foreground);
        var started = DateTimeOffset.UtcNow;
        var result = await new ProcessExecutor().RunAsync(normalized, cancellationToken).ConfigureAwait(false);
        var finished = DateTimeOffset.UtcNow;
        if (Has(options, "--json")) await WriteJsonAsync(stdout, RunJson(normalized, result, started, finished)).ConfigureAwait(false);
        else { if (result.Stdout.Length > 0) await stdout.WriteAsync(result.Stdout).ConfigureAwait(false); if (result.Stderr.Length > 0) await stderr.WriteAsync(result.Stderr).ConfigureAwait(false); }
        return result.State == "completed" ? 0 : result.ExitCode is > 0 ? result.ExitCode.Value : 1;
    }

    private static async Task<int> RunProfileAsync(string command, string[] rest, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        RequireArg(rest, 0, "profile name");
        var separator = Array.IndexOf(rest, "--");
        var optionEnd = separator >= 0 ? separator : rest.Length;
        for (var i = 1; i < optionEnd; i++) if (rest[i] != "--json") throw new ArgumentException($"Profile fields cannot be overridden: {rest[i]}");
        var extra = separator >= 0 ? rest[(separator + 1)..] : [];
        var profile = new ProfileStore().Get(rest[0]);
        var isRun = command == "run-config";
        var normalized = ProfileResolver.Resolve(profile, extra, isRun ? CommandKind.Foreground : CommandKind.Job);
        var json = rest.Take(optionEnd).Contains("--json");
        if (isRun)
        {
            var started = DateTimeOffset.UtcNow;
            var result = await new ProcessExecutor().RunAsync(normalized, cancellationToken).ConfigureAwait(false);
            var finished = DateTimeOffset.UtcNow;
            if (json) await WriteJsonAsync(stdout, RunJson(normalized, result, started, finished)).ConfigureAwait(false);
            else { if (result.Stdout.Length > 0) await stdout.WriteAsync(result.Stdout).ConfigureAwait(false); if (result.Stderr.Length > 0) await stderr.WriteAsync(result.Stderr).ConfigureAwait(false); }
            return result.State == "completed" ? 0 : result.ExitCode is > 0 ? result.ExitCode.Value : 1;
        }
        await WriteValueAsync(stdout, await new JobService().StartAsync(normalized, cancellationToken).ConfigureAwait(false), json).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunOutputAsync(string[] rest, JobService service, TextWriter stdout, CancellationToken cancellationToken)
    {
        RequireArg(rest, 0, "job-id");
        var id = rest[0];
        var stream = Option(rest, "--stream") ?? "stdout";
        var offset = Option(rest, "--offset") is { } rawOffset ? long.Parse(rawOffset, CultureInfo.InvariantCulture) : 0;
        var max = Option(rest, "--max-bytes") is { } rawMax ? int.Parse(rawMax, CultureInfo.InvariantCulture) : 64 * 1024;
        var json = Has(rest, "--json");
        if (!Has(rest, "--follow"))
        {
            var value = await service.OutputAsync(id, stream, offset, max, cancellationToken).ConfigureAwait(false);
            if (json) await WriteJsonAsync(stdout, value).ConfigureAwait(false); else await stdout.WriteAsync((string)value["text"]!).ConfigureAwait(false);
            return 0;
        }
        var current = offset;
        while (true)
        {
            var chunk = await service.OutputAsync(id, stream, current, max, cancellationToken).ConfigureAwait(false);
            var text = (string)chunk["text"]!;
            if (text.Length > 0) await stdout.WriteAsync(text).ConfigureAwait(false);
            current = (long)chunk["next_offset"]!;
            var status = await service.StatusAsync(id, cancellationToken).ConfigureAwait(false);
            if ((string)status["state"]! is not ("running" or "queued") && (bool)chunk["eof"]!) break;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> RunCaptureAsync(string[] rest, TextWriter stdout, CancellationToken cancellationToken)
    {
        var job = Option(rest, "--job");
        var pid = Option(rest, "--pid") is { } pidText ? int.Parse(pidText, CultureInfo.InvariantCulture) : (int?)null;
        var title = Option(rest, "--title");
        var hwnd = Option(rest, "--hwnd") is { } hwndText ? ParseHwnd(hwndText) : (nint?)null;
        var selectors = new[] { job is not null, pid is not null, title is not null, hwnd is not null }.Count(value => value);
        if (selectors != 1) throw new ArgumentException("Specify exactly one of --job, --pid, --title, or --hwnd");
        var output = Option(rest, "--output") ?? Path.Combine(Environment.CurrentDirectory, $"execmcp-capture-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        var window = await WindowInspector.ResolveAsync(job, pid, title, hwnd, cancellationToken).ConfigureAwait(false);
        var result = await WindowCapture.CaptureAsync(window, output, !Has(rest, "--no-foreground"), cancellationToken).ConfigureAwait(false);
        await WriteValueAsync(stdout, result, Has(rest, "--json")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunSnipAsync(string[] rest, TextWriter stdout, CancellationToken cancellationToken)
    {
        var modeText = Option(rest, "--mode") ?? throw new ArgumentException("snip requires --mode rectangle|freeform|window|video");
        var mode = modeText switch { "rectangle" => SnipMode.Rectangle, "freeform" => SnipMode.Freeform, "window" => SnipMode.Window, "video" => SnipMode.Video, _ => throw new ArgumentException("snip mode must be rectangle, freeform, window, or video") };
        if (!PackageIdentity.IsPackaged) throw new InvalidOperationException("snip requires the installed MSIX package");
        var correlation = Guid.NewGuid();
        var correlations = new SnippingCorrelationStore();
        await correlations.AddAsync(correlation, cancellationToken).ConfigureAwait(false);
        var uri = SnippingUriBuilder.Build(mode, correlation);
        bool accepted;
        try { accepted = await Launcher.LaunchUriAsync(uri); }
        catch { await correlations.RemoveAsync(correlation, CancellationToken.None).ConfigureAwait(false); throw; }
        if (!accepted)
        {
            await correlations.RemoveAsync(correlation, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Windows did not accept the Snipping Tool launch");
        }
        if (Has(rest, "--json")) await WriteJsonAsync(stdout, new Dictionary<string, object?> { ["launched"] = true, ["mode"] = modeText, ["correlation_id"] = correlation }).ConfigureAwait(false);
        return 0;
    }
}
