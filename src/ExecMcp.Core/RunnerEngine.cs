using System.Diagnostics;
using System.Text.Json;

namespace ExecMcp.Core;

public static class RunnerEngine
{
    public static async Task<int> RunAsync(string specPath, CancellationToken cancellationToken = default)
    {
        RunnerSpec? spec = null;
        var store = new StateStore();
        try
        {
            spec = JsonSerializer.Deserialize<RunnerSpec>(await File.ReadAllTextAsync(specPath, cancellationToken).ConfigureAwait(false), JsonSupport.Options)
                ?? throw new InvalidOperationException("Invalid runner specification");
            File.Delete(specPath);
            var launch = ShellBuilder.Build(spec.Command);
            using var job = WindowsJobObject.Create(spec.Id);
            using var process = ProcessExecutor.StartProcess(launch);
            job.Assign(process);
            await store.UpdateAsync(state =>
            {
                var current = JobService.Find(state, spec.Id);
                current.Pid = process.Id;
                current.RunnerPid = Environment.ProcessId;
                JobService.AppendEvent(state, current, "process_started", new { pid = process.Id });
                return state;
            }, cancellationToken).ConfigureAwait(false);

            var stdout = new BoundedLog(spec.StdoutPath, spec.Command.MaxLogBytes);
            var stderr = new BoundedLog(spec.StderrPath, spec.Command.MaxLogBytes);
            var tracker = new ReadinessTracker(spec.Command.ReadyPattern);
            var readyPersisted = spec.Command.ReadyPattern is null ? 1 : 0;
            void ObserveReady()
            {
                if (!tracker.IsReady || Interlocked.Exchange(ref readyPersisted, 1) != 0) return;
                store.UpdateAsync(state =>
                {
                    var current = JobService.Find(state, spec.Id);
                    current.Ready = true;
                    current.ReadyAt = DateTimeOffset.UtcNow;
                    JobService.AppendEvent(state, current, "ready", new { pattern = current.ReadyPattern });
                    return state;
                }, CancellationToken.None).GetAwaiter().GetResult();
            }

            var stdoutTask = ProcessStreams.PumpAsync(process.StandardOutput.BaseStream, data =>
            {
                tracker.Append(data.Span); ObserveReady(); stdout.AppendAsync(data, CancellationToken.None).GetAwaiter().GetResult();
            }, cancellationToken);
            var stderrTask = ProcessStreams.PumpAsync(process.StandardError.BaseStream, data =>
            {
                tracker.Append(data.Span); ObserveReady(); stderr.AppendAsync(data, CancellationToken.None).GetAwaiter().GetResult();
            }, cancellationToken);

            var timedOut = false;
            using var timeoutCts = spec.Command.TimeoutMs > 0 ? new CancellationTokenSource(spec.Command.TimeoutMs) : null;
            using var linked = timeoutCts is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts?.IsCancellationRequested == true)
            {
                timedOut = true;
                await store.UpdateAsync(state =>
                {
                    var current = JobService.Find(state, spec.Id);
                    current.State = "timed_out";
                    current.Error = $"Timed out after {spec.Command.TimeoutMs} ms";
                    JobService.AppendEvent(state, current, "timeout", new { timeout_ms = spec.Command.TimeoutMs });
                    return state;
                }, CancellationToken.None).ConfigureAwait(false);
                try { job.Terminate(1); } catch { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var stdoutMeta = stdout.GetMetadata(); var stderrMeta = stderr.GetMetadata();
            await store.UpdateAsync(state =>
            {
                var current = JobService.Find(state, spec.Id);
                current.ExitCode = process.ExitCode;
                current.FinishedAt = DateTimeOffset.UtcNow;
                current.StdoutBytes = stdoutMeta.FullBytes; current.StdoutTailBytes = stdoutMeta.TailBytes;
                current.StderrBytes = stderrMeta.FullBytes; current.StderrTailBytes = stderrMeta.TailBytes;
                if (current.State == "killed") { }
                else if (timedOut || current.State == "timed_out") current.State = "timed_out";
                else current.State = process.ExitCode == 0 ? "completed" : "failed";
                JobService.AppendEvent(state, current, "finished", new { state = current.State, exit_code = current.ExitCode });
                return state;
            }, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            if (spec is not null)
            {
                try
                {
                    await store.UpdateAsync(state =>
                    {
                        var current = JobService.Find(state, spec.Id);
                        if (current.State != "killed") current.State = "failed";
                        current.Error = ex.Message;
                        current.FinishedAt = DateTimeOffset.UtcNow;
                        JobService.AppendEvent(state, current, "runner_failed", new { error = ex.Message });
                        return state;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            return 1;
        }
    }
}
