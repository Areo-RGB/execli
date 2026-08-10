using System.Text.Json;

namespace ExecMcp.Core;

public sealed class ProfileConfig
{
    public int Version { get; set; } = 1;
    public Dictionary<string, CommandProfile> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProfileStore
{
    private readonly string _path;
    public ProfileStore(string? path = null) => _path = path ?? StatePaths.ConfigFile;

    public CommandProfile Get(string name)
    {
        if (!File.Exists(_path)) throw new FileNotFoundException($"Profile config does not exist: {_path}", _path);
        var config = JsonSerializer.Deserialize<ProfileConfig>(File.ReadAllText(_path), JsonSupport.Options) ?? throw new InvalidOperationException("Invalid profile config");
        if (config.Version != 1) throw new InvalidOperationException($"Unsupported profile config version: {config.Version}");
        if (!config.Commands.TryGetValue(name, out var profile)) throw new KeyNotFoundException($"Unknown command profile: {name}");
        return profile;
    }
}

public static class ProfileResolver
{
    public static NormalizedCommand Resolve(CommandProfile profile, IReadOnlyList<string>? appendedArgs = null, CommandKind kind = CommandKind.Foreground)
    {
        var extra = appendedArgs ?? [];
        if (extra.Count > 0 && !profile.AllowAppendedArgs)
            throw new InvalidOperationException("This profile does not allow appended arguments");
        var request = new CommandRequest
        {
            Shell = ShellKind.None,
            Executable = profile.Executable,
            Args = profile.Args.Concat(extra).ToArray(),
            Cwd = profile.Cwd,
            Env = profile.Env,
            TimeoutMs = profile.TimeoutMs,
            MaxOutputBytes = profile.MaxOutputBytes,
            MaxLogBytes = profile.MaxLogBytes,
            ReadyPattern = profile.ReadyPattern,
            Title = profile.Title
        };
        return CommandValidator.Normalize(request, kind);
    }
}
