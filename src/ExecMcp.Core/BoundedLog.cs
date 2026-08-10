using System.Text;
using System.Text.Json;

namespace ExecMcp.Core;

public sealed class BoundedLog
{
    private readonly string _path;
    private readonly string _metaPath;
    private readonly long _maxBytes;
    private readonly string _mutexName;

    public BoundedLog(string path, long maxBytes)
    {
        _path = path;
        _metaPath = path + ".meta.json";
        _maxBytes = maxBytes;
        var stable = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16];
        _mutexName = $@"Local\ExecMcp.Log.{stable}";
    }

    public Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var lease = Acquire();
        var meta = ReadMeta();
        var existing = File.Exists(_path) ? File.ReadAllBytes(_path) : [];
        var combined = new byte[existing.Length + data.Length];
        Buffer.BlockCopy(existing, 0, combined, 0, existing.Length);
        data.Span.CopyTo(combined.AsSpan(existing.Length));
        var fullBytes = checked(meta.FullBytes + data.Length);
        var baseOffset = meta.BaseOffset;
        byte[] retained;
        if (combined.LongLength > _maxBytes)
        {
            var discard = checked((int)(combined.LongLength - _maxBytes));
            baseOffset += discard;
            retained = combined.AsSpan(discard).ToArray();
        }
        else retained = combined;

        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, retained);
        File.Move(temp, _path, true);
        WriteMeta(new LogMeta(fullBytes, baseOffset));
        return Task.CompletedTask;
    }

    public Task<LogReadResult> ReadAsync(long offset, int maxBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (maxBytes < 1 || maxBytes > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var lease = Acquire();
        var meta = ReadMeta();
        var bytes = File.Exists(_path) ? File.ReadAllBytes(_path) : [];
        var requested = Math.Clamp(offset, meta.BaseOffset, meta.FullBytes);
        var relative = checked((int)Math.Min(bytes.LongLength, requested - meta.BaseOffset));
        var available = Math.Min(bytes.Length - relative, maxBytes + 4);
        var segment = bytes.AsSpan(relative, Math.Max(0, available));
        var decoded = Utf8ByteReader.Decode(segment, maxBytes);
        var actualOffset = requested + decoded.LeadingSkipped;
        var nextOffset = actualOffset + decoded.DecodedBytes;
        var result = new LogReadResult(actualOffset, nextOffset, decoded.DecodedBytes, nextOffset >= meta.FullBytes, Redactor.Redact(decoded.Text), meta.FullBytes, bytes.LongLength, meta.BaseOffset);
        return Task.FromResult(result);
    }

    public (long FullBytes, long TailBytes, long TailStartOffset) GetMetadata()
    {
        using var lease = Acquire();
        var meta = ReadMeta();
        var tail = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        return (meta.FullBytes, tail, meta.BaseOffset);
    }

    public async Task<string> ReadTailTextAsync(int maxBytes, CancellationToken cancellationToken = default)
    {
        var meta = GetMetadata();
        var start = Math.Max(meta.TailStartOffset, meta.FullBytes - maxBytes);
        return (await ReadAsync(start, maxBytes, cancellationToken).ConfigureAwait(false)).Text;
    }

    private LogMeta ReadMeta()
    {
        if (!File.Exists(_metaPath))
        {
            var length = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            return new LogMeta(length, 0);
        }
        try { return JsonSerializer.Deserialize<LogMeta>(File.ReadAllText(_metaPath), JsonSupport.Options) ?? new LogMeta(0, 0); }
        catch { return new LogMeta(File.Exists(_path) ? new FileInfo(_path).Length : 0, 0); }
    }

    private void WriteMeta(LogMeta meta)
    {
        var temp = _metaPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(meta, JsonSupport.Options), new UTF8Encoding(false));
        File.Move(temp, _metaPath, true);
    }

    private MutexLease Acquire()
    {
        var mutex = new Mutex(false, _mutexName);
        try
        {
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed record LogMeta(long FullBytes, long BaseOffset);
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); }
        }
    }
}

public static class Utf8ByteReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static (string Text, int LeadingSkipped, int DecodedBytes) Decode(ReadOnlySpan<byte> bytes, int maxBytes)
    {
        var limit = Math.Min(bytes.Length, maxBytes);
        for (var leading = 0; leading <= Math.Min(3, limit); leading++)
        {
            for (var trailing = 0; trailing <= 3 && limit - trailing >= leading; trailing++)
            {
                var length = limit - leading - trailing;
                if (length == 0) return ("", leading, 0);
                try
                {
                    var text = StrictUtf8.GetString(bytes.Slice(leading, length));
                    return (text, leading, length);
                }
                catch (DecoderFallbackException) { }
            }
        }
        return ("", limit, 0);
    }
}
