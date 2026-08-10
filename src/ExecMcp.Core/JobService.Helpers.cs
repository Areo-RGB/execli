using System.Diagnostics;

namespace ExecMcp.Core;

public sealed partial class JobService
{
    private static async Task<Dictionary<string, object?>> BuildStatusAsync(JobRecord record, CancellationToken cancellationToken)
    {
        var stdout = new BoundedLog(record.StdoutPath, record.MaxLogBytes);
        var stderr = new BoundedLog(record.StderrPath, record.MaxLogBytes);
        var stdoutTail = Terminal.Contains(record.State) ? await stdout.ReadTailTextAsync(8192, cancellationToken).ConfigureAwait(false) : "";
        var stderrTail = Terminal.Contains(record.State) ? await stderr.ReadTailTextAsync(8192, cancellationToken).ConfigureAwait(false) : "";
        return new Dictionary<string, object?>
        {
            ["id"] = record.Id, ["state"] = record.State, ["pid"] = record.Pid, ["runner_pid"] = record.RunnerPid,
            ["exit_code"] = record.ExitCode, ["signal"] = record.Signal, ["error"] = record.Error, ["shell"] = record.Shell,
            ["executable"] = record.Executable, ["args"] = record.Args, ["command"] = record.Command, ["cwd"] = record.Cwd,
            ["created_at"] = record.CreatedAt, ["started_at"] = record.StartedAt, ["finished_at"] = record.FinishedAt,
            ["timeout_ms"] = record.TimeoutMs, ["stdout_bytes"] = record.StdoutBytes, ["stderr_bytes"] = record.StderrBytes,
            ["stdout_tail_bytes"] = record.StdoutTailBytes, ["stderr_tail_bytes"] = record.StderrTailBytes,
            ["stdout_tail"] = stdoutTail, ["stderr_tail"] = stderrTail, ["ready"] = record.Ready, ["ready_at"] = record.ReadyAt,
            ["ready_pattern"] = record.ReadyPattern, ["title"] = record.Title
        };
    }

    internal static JobRecord Find(StateDocument state, string id) => state.Jobs.FirstOrDefault(job => job.Id == id) ?? throw new KeyNotFoundException($"Unknown job: {id}");

    internal static void AppendEvent(StateDocument state, JobRecord record, string type, object? data)
    {
        record.Events.Add(new EventRecord(state.NextEventSequence++, DateTimeOffset.UtcNow, type, record.Id, data));
    }

    internal static string ShellText(ShellKind shell) => shell switch
    {
        ShellKind.None => "none", ShellKind.PowerShell => "powershell", ShellKind.Cmd => "cmd", ShellKind.GitBash => "git-bash", _ => throw new ArgumentOutOfRangeException(nameof(shell))
    };

    private static void RefreshLogMetadata(JobRecord record)
    {
        var stdout = new BoundedLog(record.StdoutPath, record.MaxLogBytes).GetMetadata();
        var stderr = new BoundedLog(record.StderrPath, record.MaxLogBytes).GetMetadata();
        record.StdoutBytes = stdout.FullBytes; record.StdoutTailBytes = stdout.TailBytes;
        record.StderrBytes = stderr.FullBytes; record.StderrTailBytes = stderr.TailBytes;
    }

    internal static bool ProcessExists(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }
}
