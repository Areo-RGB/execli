using System.Diagnostics;
using System.Text.Json;

namespace ExecMcp.Core;

public sealed record RunnerSpec(string Id, NormalizedCommand Command, string StdoutPath, string StderrPath);

public sealed partial class JobService
{
    private static readonly HashSet<string> Terminal = ["completed", "failed", "timed_out", "killed", "orphaned"];
    private readonly StateStore _store;
    private readonly string _runnerExecutable;

    public JobService(StateStore? store = null, string? runnerExecutable = null)
    {
        _store = store ?? new StateStore();
        _runnerExecutable = runnerExecutable ?? Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine execmcp executable path");
    }

    public async Task<Dictionary<string, object?>> StartAsync(NormalizedCommand command, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(StatePaths.Jobs);
        var id = $"job_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}_{Guid.NewGuid().ToString("N")[..8]}";
        var stdout = Path.Combine(StatePaths.Jobs, id + ".stdout.log");
        var stderr = Path.Combine(StatePaths.Jobs, id + ".stderr.log");
        var specPath = Path.Combine(StatePaths.Jobs, id + ".spec.json");
        File.WriteAllBytes(stdout, []);
        File.WriteAllBytes(stderr, []);
        var record = new JobRecord
        {
            Id = id,
            State = "running",
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
            Shell = ShellText(command.Shell),
            Executable = command.Shell == ShellKind.None ? command.Executable : null,
            Args = command.Shell == ShellKind.None ? command.Args.ToList() : null,
            Command = command.Shell == ShellKind.None ? null : command.Command,
            Cwd = command.Cwd,
            TimeoutMs = command.TimeoutMs,
            MaxOutputBytes = command.MaxOutputBytes,
            MaxLogBytes = command.MaxLogBytes,
            ReadyPattern = command.ReadyPattern,
            Ready = command.ReadyPattern is null,
            ReadyAt = command.ReadyPattern is null ? DateTimeOffset.UtcNow : null,
            Title = command.Title,
            StdoutPath = stdout,
            StderrPath = stderr
        };
        await _store.UpdateAsync(state =>
        {
            state.Jobs.Add(record);
            AppendEvent(state, record, "started", new { shell = record.Shell, executable = record.Executable, cwd = record.Cwd });
            return state;
        }, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(new RunnerSpec(id, command, stdout, stderr), JsonSupport.Options), cancellationToken).ConfigureAwait(false);

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = _runnerExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = StatePaths.V2
            };
            info.ArgumentList.Add("__runner");
            info.ArgumentList.Add("--spec");
            info.ArgumentList.Add(specPath);
            using var runner = Process.Start(info) ?? throw new InvalidOperationException("Could not start detached runner");
            await _store.UpdateAsync(state =>
            {
                var current = Find(state, id);
                current.RunnerPid = runner.Id;
                return state;
            }, cancellationToken).ConfigureAwait(false);
            return await StatusAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _store.UpdateAsync(state =>
            {
                var current = Find(state, id);
                current.State = "failed";
                current.Error = ex.Message;
                current.FinishedAt = DateTimeOffset.UtcNow;
                AppendEvent(state, current, "failed", new { error = ex.Message });
                return state;
            }, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Dictionary<string, object?>> StatusAsync(string id, CancellationToken cancellationToken = default)
    {
        var state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var record = Find(state, id);
        if (record.State == "running" && record.RunnerPid is int runnerPid && record.StartedAt is { } started && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(5) && !ProcessExists(runnerPid))
        {
            await _store.UpdateAsync(document =>
            {
                var current = Find(document, id);
                if (current.State == "running")
                {
                    current.State = "orphaned";
                    current.Error = "The detached supervisor is no longer running";
                    current.FinishedAt = DateTimeOffset.UtcNow;
                    AppendEvent(document, current, "orphaned", new { error = current.Error });
                }
                return document;
            }, cancellationToken).ConfigureAwait(false);
            state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            record = Find(state, id);
        }
        RefreshLogMetadata(record);
        return await BuildStatusAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<Dictionary<string, object?>>();
        foreach (var record in state.Jobs.AsEnumerable().Reverse())
        {
            RefreshLogMetadata(record);
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = record.Id, ["state"] = record.State, ["pid"] = record.Pid, ["runner_pid"] = record.RunnerPid,
                ["exit_code"] = record.ExitCode, ["shell"] = record.Shell, ["executable"] = record.Executable, ["cwd"] = record.Cwd,
                ["created_at"] = record.CreatedAt, ["started_at"] = record.StartedAt, ["finished_at"] = record.FinishedAt,
                ["stdout_bytes"] = record.StdoutBytes, ["stderr_bytes"] = record.StderrBytes, ["ready"] = record.Ready, ["title"] = record.Title
            });
        }
        return result;
    }

}
