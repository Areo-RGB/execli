using ExecMcp.Core;
namespace ExecMcp.Tests;

public sealed class ProfileTests
{
    [Fact]
    public void Resolve_RejectsAppendedArgumentsWhenDisabled()
    {
        var profile = new CommandProfile("dotnet.exe", ["build"], Environment.CurrentDirectory, new Dictionary<string,string>(), 300000, 262144, 67108864, null, "Build", false);
        Assert.Throws<InvalidOperationException>(() => ProfileResolver.Resolve(profile, ["--no-restore"]));
    }

    [Fact]
    public void Resolve_AppendsArgumentsWhenEnabled()
    {
        var profile = new CommandProfile("dotnet.exe", ["build"], Environment.CurrentDirectory, new Dictionary<string,string>(), 300000, 262144, 67108864, null, "Build", true);
        Assert.Equal(["build", "--no-restore"], ProfileResolver.Resolve(profile, ["--no-restore"]).Args);
    }
    [Fact]
    public void ProfileStore_ReadsVersionOneConfigWithExactFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "execmcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.json");
        File.WriteAllText(path, """
        {
          "version": 1,
          "commands": {
            "build": {
              "executable": "dotnet.exe",
              "args": ["build"],
              "cwd": ".",
              "env": {},
              "timeout_ms": 300000,
              "max_output_bytes": 262144,
              "max_log_bytes": 67108864,
              "ready_pattern": null,
              "title": "Build",
              "allow_appended_args": false
            }
          }
        }
        """);
        var profile = new ProfileStore(path).Get("build");
        Assert.Equal("dotnet.exe", profile.Executable);
        Assert.Equal(["build"], profile.Args);
        Assert.False(profile.AllowAppendedArgs);
    }

}
