using ExecMcp.Cli;
namespace ExecMcp.Tests;

public sealed class CliTests
{
    [Fact]
    public async Task Help_DoesNotAdvertiseMcp()
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["--help"], stdout, stderr);
        Assert.Equal(0, code);
        Assert.DoesNotContain("execmcp mcp", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run-config", stdout.ToString());
        Assert.Contains("snip", stdout.ToString());
    }

    [Fact]
    public async Task Mcp_IsRejected()
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        await Assert.ThrowsAsync<ArgumentException>(() => CliApp.RunAsync(["mcp"], stdout, stderr));
    }
}
