namespace ExecMcp.Core;

public sealed partial class JobService
{
    public async Task<Dictionary<string, object?>> OutputAsync(string id, string stream = "stdout", long offset = 0, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default)
    {
        if (stream is not ("stdout" or "stderr")) throw new ArgumentException("stream must be stdout or stderr");
        var state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var record = Find(state, id);
        var log = new BoundedLog(stream == "stdout" ? record.StdoutPath : record.StderrPath, record.MaxLogBytes);
        var read = await log.ReadAsync(offset, maxBytes, cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, object?>
        {
            ["id"] = id, ["stream"] = stream, ["offset"] = read.Offset, ["next_offset"] = read.NextOffset,
            ["bytes"] = read.Bytes, ["eof"] = read.Eof, ["text"] = read.Text,
            ["full_bytes"] = read.FullBytes, ["tail_bytes"] = read.TailBytes, ["tail_start_offset"] = read.TailStartOffset
        };
    }

    public async Task<Dictionary<string, object?>> WaitAsync(string id, int timeoutMs = 30_000, CancellationToken cancellationToken = default)
    {
        if (timeoutMs < 0 || timeoutMs > 600_000) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "wait timeout must be 0..600000 ms");
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var status = await StatusAsync(id, cancellationToken).ConfigureAwait(false);
            if (Terminal.Contains((string)status["state"]!)) return status;
            if (DateTimeOffset.UtcNow >= deadline) return status;
            var delay = Math.Min(200, Math.Max(1, (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Dictionary<string, object?>> KillAsync(string id, CancellationToken cancellationToken = default)
    {
        var state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var record = Find(state, id);
        if (Terminal.Contains(record.State)) return await StatusAsync(id, cancellationToken).ConfigureAwait(false);

        await _store.UpdateAsync(document =>
        {
            var current = Find(document, id);
            current.State = "killed";
            current.Error = null;
            current.FinishedAt = DateTimeOffset.UtcNow;
            AppendEvent(document, current, "killed", null);
            return document;
        }, cancellationToken).ConfigureAwait(false);

        using (var job = WindowsJobObject.Open(id))
        {
            if (job is not null)
            {
                try { job.Terminate(1); } catch { }
            }
            else if (record.Pid is int pid && ProcessExists(pid))
            {
                throw new InvalidOperationException($"Windows Job Object for {id} is unavailable while PID {pid} is still running");
            }
        }

        if (record.Pid is int verifyPid)
        {
            for (var i = 0; i < 25 && ProcessExists(verifyPid); i++) await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            if (ProcessExists(verifyPid)) throw new InvalidOperationException($"Process tree for {id} is still running after termination");
        }
        return await StatusAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EventRecord>> EventsAsync(string? id = null, long after = 0, CancellationToken cancellationToken = default)
    {
        var state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return state.Jobs
            .Where(job => id is null || job.Id == id)
            .SelectMany(job => job.Events)
            .Where(evt => evt.Sequence > after)
            .OrderBy(evt => evt.Sequence)
            .ToArray();
    }

}
