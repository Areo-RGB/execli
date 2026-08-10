using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ExecMcp.Core;

public sealed class ProcessExecutor
{
    public async Task<RunResult> RunAsync(NormalizedCommand command, CancellationToken cancellationToken = default)
    {
        var launch = ShellBuilder.Build(command);
        using var job = WindowsJobObject.Create("fg_" + Guid.NewGuid().ToString("N"));
        using var process = StartProcess(launch);
        job.Assign(process);

        var stdout = new TailBuffer(command.MaxOutputBytes);
        var stderr = new TailBuffer(command.MaxOutputBytes);
        var tracker = new ReadinessTracker(command.ReadyPattern);
        var outTask = ProcessStreams.PumpAsync(process.StandardOutput.BaseStream, data => { stdout.Append(data.Span); tracker.Append(data.Span); }, cancellationToken);
        var errTask = ProcessStreams.PumpAsync(process.StandardError.BaseStream, data => { stderr.Append(data.Span); tracker.Append(data.Span); }, cancellationToken);

        var timedOut = false;
        using var timeoutCts = command.TimeoutMs > 0 ? new CancellationTokenSource(command.TimeoutMs) : null;
        using var linked = timeoutCts is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts?.IsCancellationRequested == true)
        {
            timedOut = true;
            try { job.Terminate(1); } catch { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { job.Terminate(1); } catch { }
            throw;
        }
        finally
        {
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        }

        var state = timedOut ? "timed_out" : process.ExitCode == 0 ? "completed" : "failed";
        return new RunResult(state, process.ExitCode, null, timedOut ? $"Timed out after {command.TimeoutMs} ms" : null,
            Redactor.Redact(stdout.GetText()), Redactor.Redact(stderr.GetText()), stdout.TotalBytes, stderr.TotalBytes, tracker.IsReady);
    }

    internal static Process StartProcess(LaunchSpec launch)
    {
        var info = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = launch.Cwd,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in launch.Arguments) info.ArgumentList.Add(arg);
        info.Environment.Clear();
        foreach (var pair in launch.Environment) info.Environment[pair.Key] = pair.Value;
        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {launch.Executable}");
    }
}

internal static class ProcessStreams
{
    public static async Task PumpAsync(Stream stream, Action<ReadOnlyMemory<byte>> onData, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            onData(buffer.AsMemory(0, read));
        }
    }
}

internal sealed class TailBuffer(int maxBytes)
{
    private byte[] _bytes = [];
    public long TotalBytes { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        TotalBytes += data.Length;
        if (data.Length >= maxBytes)
        {
            _bytes = data[^maxBytes..].ToArray();
            return;
        }
        var keep = Math.Min(_bytes.Length, maxBytes - data.Length);
        var next = new byte[keep + data.Length];
        if (keep > 0) _bytes.AsSpan(_bytes.Length - keep, keep).CopyTo(next);
        data.CopyTo(next.AsSpan(keep));
        _bytes = next;
    }

    public string GetText()
    {
        for (var skip = 0; skip <= Math.Min(3, _bytes.Length); skip++)
        {
            try { return new UTF8Encoding(false, true).GetString(_bytes.AsSpan(skip)); }
            catch (DecoderFallbackException) { }
        }
        return Encoding.UTF8.GetString(_bytes);
    }
}

internal sealed class ReadinessTracker
{
    private readonly Regex? _regex;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _gate = new();
    private string _rolling = "";
    private int _ready;
    public bool IsReady => Volatile.Read(ref _ready) != 0;
    public event Action? Ready;

    public ReadinessTracker(string? pattern)
    {
        if (pattern is not null) _regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_regex is null || IsReady) return;
        lock (_gate)
        {
            if (IsReady) return;
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            _decoder.Convert(bytes, chars, false, out _, out var charsUsed, out _);
            if (charsUsed == 0) return;
            _rolling += new string(chars, 0, charsUsed);
            if (_rolling.Length > 131072) _rolling = _rolling[^131072..];
            if (_regex.IsMatch(_rolling) && Interlocked.Exchange(ref _ready, 1) == 0) Ready?.Invoke();
        }
    }
}
