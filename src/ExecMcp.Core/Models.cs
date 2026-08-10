using System.Text.Json.Serialization;

namespace ExecMcp.Core;

public enum ShellKind { None, PowerShell, Cmd, GitBash }
public enum CommandKind { Foreground, Job }
public enum SnipMode { Rectangle, Freeform, Window, Video }

public sealed class CommandRequest
{
    public ShellKind Shell { get; init; } = ShellKind.None;
    public string? Executable { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public string? Command { get; init; }
    public string? Cwd { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public int? TimeoutMs { get; init; }
    public int? MaxOutputBytes { get; init; }
    public long? MaxLogBytes { get; init; }
    public string? ReadyPattern { get; init; }
    public string? Title { get; init; }
}

public sealed record NormalizedCommand(
    ShellKind Shell,
    string? Executable,
    IReadOnlyList<string> Args,
    string? Command,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    int TimeoutMs,
    int MaxOutputBytes,
    long MaxLogBytes,
    string? ReadyPattern,
    string? Title);

public sealed record LaunchSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Cwd,
    IReadOnlyDictionary<string, string> Environment);

public sealed record CommandProfile(
    [property: JsonPropertyName("executable")] string Executable,
    [property: JsonPropertyName("args")] IReadOnlyList<string> Args,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string> Env,
    [property: JsonPropertyName("timeout_ms")] int TimeoutMs,
    [property: JsonPropertyName("max_output_bytes")] int MaxOutputBytes,
    [property: JsonPropertyName("max_log_bytes")] long MaxLogBytes,
    [property: JsonPropertyName("ready_pattern")] string? ReadyPattern,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("allow_appended_args")] bool AllowAppendedArgs);

public sealed record RunResult(
    string State,
    int? ExitCode,
    string? Signal,
    string? Error,
    string Stdout,
    string Stderr,
    long StdoutBytes,
    long StderrBytes,
    bool Ready);

public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? Pid { get; set; }
    public int? RunnerPid { get; set; }
    public int? ExitCode { get; set; }
    public string? Signal { get; set; }
    public string? Error { get; set; }
    public string Shell { get; set; } = "none";
    public string? Executable { get; set; }
    public List<string>? Args { get; set; }
    public string? Command { get; set; }
    public string Cwd { get; set; } = "";
    public int TimeoutMs { get; set; }
    public int MaxOutputBytes { get; set; }
    public long MaxLogBytes { get; set; }
    public string? ReadyPattern { get; set; }
    public bool Ready { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public string? Title { get; set; }
    public string StdoutPath { get; set; } = "";
    public string StderrPath { get; set; } = "";
    public long StdoutBytes { get; set; }
    public long StderrBytes { get; set; }
    public long StdoutTailBytes { get; set; }
    public long StderrTailBytes { get; set; }
    public List<EventRecord> Events { get; set; } = [];
}

public sealed record EventRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    string Type,
    string JobId,
    object? Data);

public sealed class StateDocument
{
    public int Version { get; set; } = 2;
    public long NextEventSequence { get; set; } = 1;
    public List<JobRecord> Jobs { get; set; } = [];
}

public sealed record LogReadResult(
    long Offset,
    long NextOffset,
    int Bytes,
    bool Eof,
    string Text,
    long FullBytes,
    long TailBytes,
    long TailStartOffset);

public sealed record WindowInfo(nint Hwnd, int Pid, string Title, bool Visible, bool Minimized);
