using System.Security.Principal;
using System.Text.Json;

namespace ExecMcp.Core;

public static class StatePaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "windows-exec-mcp");
    public static string V2 => Path.Combine(Root, "v2");
    public static string Jobs => Path.Combine(V2, "jobs");
    public static string StateFile => Path.Combine(V2, "jobs.json");
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string CorrelationsFile => Path.Combine(V2, "snip-correlations.json");
}

public sealed class StateStore
{
    private readonly string _path;
    private readonly string _mutexName;
    private readonly int _maxRetained;

    public StateStore(string? path = null, int maxRetained = 128)
    {
        _path = path ?? StatePaths.StateFile;
        _maxRetained = maxRetained;
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var stable = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid)))[..16];
        _mutexName = $@"Local\ExecMcp.State.{stable}";
    }

    public Task<StateDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var gate = Acquire(cancellationToken);
        return Task.FromResult(ReadUnlocked());
    }

    public Task<T> UpdateAsync<T>(Func<StateDocument, (StateDocument State, T Result)> update, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var gate = Acquire(cancellationToken);
        var state = ReadUnlocked();
        var result = update(state);
        if (result.State.Jobs.Count > _maxRetained)
            result.State.Jobs = result.State.Jobs.TakeLast(_maxRetained).ToList();
        WriteUnlocked(result.State);
        return Task.FromResult(result.Result);
    }

    public async Task UpdateAsync(Func<StateDocument, StateDocument> update, CancellationToken cancellationToken = default)
    {
        _ = await UpdateAsync<object?>(state => (update(state), null), cancellationToken).ConfigureAwait(false);
    }

    private StateDocument ReadUnlocked()
    {
        if (!File.Exists(_path)) return new StateDocument();
        var text = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<StateDocument>(text, JsonSupport.Options) ?? new StateDocument();
    }

    private void WriteUnlocked(StateDocument state)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(state, JsonSupport.Options));
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(_path))
            {
                try { File.Replace(temp, _path, null, true); }
                catch (PlatformNotSupportedException) { File.Move(temp, _path, true); }
                catch (IOException) { File.Move(temp, _path, true); }
            }
            else File.Move(temp, _path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private MutexLease Acquire(CancellationToken cancellationToken)
    {
        var mutex = new Mutex(false, _mutexName);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(200)) return new MutexLease(mutex);
                }
                catch (AbandonedMutexException) { return new MutexLease(mutex); }
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); }
        }
    }
}
