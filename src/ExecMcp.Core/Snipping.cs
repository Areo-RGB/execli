using System.Text.Json;

namespace ExecMcp.Core;

public static class SnippingUriBuilder
{
    public static Uri Build(SnipMode mode, Guid correlationId)
    {
        var path = mode == SnipMode.Video ? "video" : "image";
        var parts = new List<string>();
        if (mode != SnipMode.Video) parts.Add(mode switch
        {
            SnipMode.Rectangle => "rectangle",
            SnipMode.Freeform => "freeform",
            SnipMode.Window => "window",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        });
        parts.Add("api-version=1.2");
        parts.Add("auto-save");
        parts.Add("user-agent=execmcp");
        parts.Add("x-request-correlation-id=" + Uri.EscapeDataString(correlationId.ToString()));
        parts.Add("redirect-uri=" + Uri.EscapeDataString("execmcp-snip://complete"));
        return new Uri($"ms-screenclip://capture/{path}?{string.Join('&', parts)}");
    }
}

public sealed record SnippingCallbackData(int Code, string? Reason, Guid CorrelationId, string? FileAccessToken);

public static class SnippingCallbackValidator
{
    public static SnippingCallbackData Parse(Uri uri)
    {
        if (!uri.Scheme.Equals("execmcp-snip", StringComparison.OrdinalIgnoreCase) || !uri.Host.Equals("complete", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unexpected Snipping Tool callback URI");
        var query = QueryString.Parse(uri.Query);
        if (!query.TryGetValue("code", out var codeText) || !int.TryParse(codeText, out var code))
            throw new ArgumentException("Callback is missing a valid code");
        if (!query.TryGetValue("x-request-correlation-id", out var correlationText) || !Guid.TryParse(correlationText, out var correlation))
            throw new ArgumentException("Callback is missing a valid correlation ID");
        query.TryGetValue("reason", out var reason);
        query.TryGetValue("file-access-token", out var token);
        if (code == 200 && string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Successful callback is missing file-access-token");
        return new SnippingCallbackData(code, reason, correlation, token);
    }
}

public sealed class SnippingCorrelationStore
{
    private readonly string _path;
    private readonly string _mutexName;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    public SnippingCorrelationStore(string? path = null)
    {
        _path = path ?? StatePaths.CorrelationsFile;
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var stable = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid)))[..16];
        _mutexName = $@"Local\ExecMcp.Snip.{stable}";
    }

    public Task AddAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var gate = Acquire();
        var data = Read();
        Prune(data);
        data.Items.RemoveAll(item => item.Id == id);
        data.Items.Add(new Correlation(id, DateTimeOffset.UtcNow));
        Write(data);
        return Task.CompletedTask;
    }

    public Task<bool> ConsumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var gate = Acquire();
        var data = Read();
        Prune(data);
        var index = data.Items.FindIndex(item => item.Id == id);
        if (index < 0) { Write(data); return Task.FromResult(false); }
        data.Items.RemoveAt(index);
        Write(data);
        return Task.FromResult(true);
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var gate = Acquire();
        var data = Read();
        data.Items.RemoveAll(item => item.Id == id);
        Write(data);
        return Task.CompletedTask;
    }

    private CorrelationDocument Read()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path)) return new CorrelationDocument();
        try { return JsonSerializer.Deserialize<CorrelationDocument>(File.ReadAllText(_path), JsonSupport.Options) ?? new CorrelationDocument(); }
        catch { return new CorrelationDocument(); }
    }

    private void Write(CorrelationDocument data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonSupport.Options), new System.Text.UTF8Encoding(false));
        File.Move(temp, _path, true);
    }

    private static void Prune(CorrelationDocument data) => data.Items.RemoveAll(item => DateTimeOffset.UtcNow - item.CreatedAt > Lifetime);

    private MutexLease Acquire()
    {
        var mutex = new Mutex(false, _mutexName);
        try { try { mutex.WaitOne(); } catch (AbandonedMutexException) { } return new MutexLease(mutex); }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable { public void Dispose() { try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); } } }
    private sealed record Correlation(Guid Id, DateTimeOffset CreatedAt);
    private sealed class CorrelationDocument { public List<Correlation> Items { get; set; } = []; }
}

internal static class QueryString
{
    public static Dictionary<string, string> Parse(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            var key = Uri.UnescapeDataString(index >= 0 ? part[..index] : part);
            var encodedValue = (index >= 0 ? part[(index + 1)..] : "").Replace('+', ' ');
            var value = Uri.UnescapeDataString(encodedValue);
            result[key] = value;
        }
        return result;
    }
}
