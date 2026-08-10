using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ExecMcp.Core;

public static partial class CommandValidator
{
    public const int DefaultExecTimeoutMs = 120_000;
    public const int MaxTimeoutMs = 7 * 24 * 60 * 60 * 1000;
    public const int DefaultMaxOutputBytes = 256 * 1024;
    public const int MaxOutputBytes = 16 * 1024 * 1024;
    public const long DefaultMaxLogBytes = 64L * 1024 * 1024;
    public const long MaxLogBytes = 1024L * 1024 * 1024;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNameRegex();

    public static NormalizedCommand Normalize(CommandRequest request, CommandKind kind)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cwd = Path.GetFullPath(request.Cwd ?? Environment.CurrentDirectory);
        if (!Directory.Exists(cwd))
            throw new ArgumentException($"Working directory does not exist: {cwd}");

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Env is not null)
        {
            foreach (var pair in request.Env)
            {
                if (!EnvironmentNameRegex().IsMatch(pair.Key))
                    throw new ArgumentException($"Invalid environment variable name: {pair.Key}");
                EnsureNoNul(pair.Value, $"Environment value for {pair.Key}");
                env[pair.Key] = pair.Value;
            }
        }

        var timeout = request.TimeoutMs ?? (kind == CommandKind.Foreground ? DefaultExecTimeoutMs : 0);
        if (timeout < 0 || timeout > MaxTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(request.TimeoutMs), $"timeout_ms must be 0..{MaxTimeoutMs}");

        var maxOutput = request.MaxOutputBytes ?? DefaultMaxOutputBytes;
        if (maxOutput < 1024 || maxOutput > MaxOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(request.MaxOutputBytes), $"max_output_bytes must be 1024..{MaxOutputBytes}");

        var maxLog = request.MaxLogBytes ?? DefaultMaxLogBytes;
        if (maxLog < 1024 || maxLog > MaxLogBytes)
            throw new ArgumentOutOfRangeException(nameof(request.MaxLogBytes), $"max_log_bytes must be 1024..{MaxLogBytes}");

        string? ready = request.ReadyPattern;
        if (ready is not null)
        {
            EnsureNoNul(ready, "ready_pattern");
            _ = new Regex(ready, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        if (request.Title is not null) EnsureNoNul(request.Title, "title");

        if (request.Shell == ShellKind.None)
        {
            var executable = EnsureNonEmpty(request.Executable, "executable");
            var args = (request.Args ?? []).Select((value, index) =>
            {
                if (value is null) throw new ArgumentException($"args[{index}] cannot be null");
                EnsureNoNul(value, $"args[{index}]");
                return value;
            }).ToArray();
            return new NormalizedCommand(request.Shell, executable, args, null, cwd, env, timeout, maxOutput, maxLog, ready, request.Title);
        }

        var command = EnsureNonEmpty(request.Command, "command");
        return new NormalizedCommand(request.Shell, null, [], command, cwd, env, timeout, maxOutput, maxLog, ready, request.Title);
    }

    private static string EnsureNonEmpty(string? value, string label)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException($"{label} must be a non-empty string");
        EnsureNoNul(value, label);
        return value;
    }

    private static void EnsureNoNul(string value, string label)
    {
        if (value.Contains('\0')) throw new ArgumentException($"{label} cannot contain a NUL character");
    }
}

public static class ExecutableResolver
{
    public static string Resolve(string executable, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrEmpty(executable) || executable.Contains('\0'))
            throw new ArgumentException("executable must be a non-empty NUL-free string", nameof(executable));

        if (Path.IsPathRooted(executable) || executable.Contains('\\') || executable.Contains('/'))
        {
            var full = Path.GetFullPath(executable);
            foreach (var candidate in ExpandCandidates(full))
                if (File.Exists(candidate)) return candidate;
            throw new FileNotFoundException($"Executable does not exist: {full}", full);
        }

        var effectivePath = GetEnvironmentValue(environment, "Path") ?? Environment.GetEnvironmentVariable("Path") ?? "";
        foreach (var directory in effectivePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in ExpandCandidates(Path.Combine(directory, executable)))
                if (File.Exists(candidate)) return candidate;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { executable }
            });
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first) && File.Exists(first)) return first;
            }
        }
        catch { }

        throw new FileNotFoundException($"Executable was not found on PATH: {executable}", executable);
    }

    private static IEnumerable<string> ExpandCandidates(string value)
    {
        yield return value;
        if (Path.HasExtension(value)) yield break;
        yield return value + ".exe";
        yield return value + ".cmd";
        yield return value + ".bat";
    }

    private static string? GetEnvironmentValue(IReadOnlyDictionary<string, string>? environment, string name)
    {
        if (environment is null) return null;
        foreach (var pair in environment)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return null;
    }
}

public static class ShellBuilder
{
    public static LaunchSpec Build(NormalizedCommand command)
    {
        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string && entry.Value is string)
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in command.Env) env[pair.Key] = pair.Value;

        if (command.Shell == ShellKind.None)
            return new LaunchSpec(ExecutableResolver.Resolve(command.Executable!, env), command.Args, command.Cwd, env);

        if (command.Shell == ShellKind.Cmd)
            return new LaunchSpec(ExecutableResolver.Resolve("cmd.exe", env), ["/d", "/s", "/c", command.Command!], command.Cwd, env);

        if (command.Shell == ShellKind.PowerShell)
        {
            string executable;
            try { executable = ExecutableResolver.Resolve("pwsh.exe", env); }
            catch (FileNotFoundException) { executable = ExecutableResolver.Resolve("powershell.exe", env); }
            var utf8Command = "$utf8=[System.Text.UTF8Encoding]::new($false);[Console]::InputEncoding=$utf8;[Console]::OutputEncoding=$utf8;$OutputEncoding=$utf8;" + command.Command;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(utf8Command));
            return new LaunchSpec(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], command.Cwd, env);
        }

        if (command.Shell == ShellKind.GitBash)
        {
            var executable = ResolveGitBash(env);
            if (!env.ContainsKey("HOME"))
                env["HOME"] = env.GetValueOrDefault("USERPROFILE") ?? ((env.GetValueOrDefault("HOMEDRIVE") ?? "C:") + (env.GetValueOrDefault("HOMEPATH") ?? "\\"));
            return new LaunchSpec(executable, ["--noprofile", "--norc", "-c", command.Command!], command.Cwd, env);
        }

        throw new ArgumentOutOfRangeException(nameof(command.Shell));
    }

    private static string ResolveGitBash(IReadOnlyDictionary<string, string> env)
    {
        foreach (var candidate in new[]
        {
            @"C:\Program Files\Git\usr\bin\sh.exe",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\usr\bin\sh.exe"
        }) if (File.Exists(candidate)) return candidate;

        var path = env.FirstOrDefault(pair => string.Equals(pair.Key, "Path", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!directory.Contains("\\Git\\", StringComparison.OrdinalIgnoreCase)) continue;
            var root = Directory.GetParent(directory)?.FullName;
            if (root is null) continue;
            var candidate = Path.Combine(root, "usr", "bin", "sh.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Git Bash was not found; refusing to use WSL bash.exe as a substitute");
    }
}

public static partial class Redactor
{
    [GeneratedRegex("(api[_-]?key|access[_-]?token|auth[_-]?token|password|secret|authorization)\\s*[:=]\\s*([^\\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretRegex();
    [GeneratedRegex("(Bearer\\s+)[A-Za-z0-9._~+\\-/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    public static string Redact(string value) => BearerRegex().Replace(SecretRegex().Replace(value, "$1=[REDACTED]"), "$1[REDACTED]");
}
