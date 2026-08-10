using System.Text;
using ExecMcp.Core;
namespace ExecMcp.Tests;

public sealed class CommandTests
{
    [Fact]
    public void Normalize_PreservesDirectArgumentsIncludingEmptyStrings()
    {
        var request = new CommandRequest { Executable = "cmd.exe", Args = ["", "a & b", "quo\"te", "雪"] };
        var result = CommandValidator.Normalize(request, CommandKind.Job);
        Assert.Equal(["", "a & b", "quo\"te", "雪"], result.Args);
    }

    [Fact]
    public void Normalize_RejectsNul()
    {
        var request = new CommandRequest { Executable = "bad\0name" };
        Assert.Throws<ArgumentException>(() => CommandValidator.Normalize(request, CommandKind.Job));
    }

    [Fact]
    public void PowerShell_UsesEncodedCommandForUnicode()
    {
        var command = new NormalizedCommand(ShellKind.PowerShell, null, [], "Write-Output '雪'", Environment.CurrentDirectory, new Dictionary<string,string>(), 1000, 262144, 67108864, null, null);
        var result = ShellBuilder.Build(command);
        var args = result.Arguments.ToList();
        var index = args.IndexOf("-EncodedCommand");
        Assert.True(index >= 0);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(args[index + 1]));
        Assert.Contains("UTF8Encoding", decoded);
        Assert.EndsWith("Write-Output '雪'", decoded);
    }

    [Fact]
    public void Cmd_UsesExplicitFlags()
    {
        var command = new NormalizedCommand(ShellKind.Cmd, null, [], "echo ok", Environment.CurrentDirectory, new Dictionary<string,string>(), 1000, 262144, 67108864, null, null);
        var result = ShellBuilder.Build(command);
        Assert.Equal(["/d", "/s", "/c", "echo ok"], result.Arguments);
    }
}
