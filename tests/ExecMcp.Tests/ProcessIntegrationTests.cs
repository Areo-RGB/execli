using ExecMcp.Core;
namespace ExecMcp.Tests;

public sealed class ProcessIntegrationTests
{
    [Fact]
    public async Task NativeExitCode_IsPreserved()
    {
        var command = CommandValidator.Normalize(new CommandRequest { Executable = "cmd.exe", Args = ["/d", "/s", "/c", "exit 7"], TimeoutMs = 10000 }, CommandKind.Foreground);
        var result = await new ProcessExecutor().RunAsync(command, TestContext.Current.CancellationToken);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("failed", result.State);
    }

    [Fact]
    public async Task Readiness_IsDetectedBeforeOutputTailTruncation()
    {
        var command = CommandValidator.Normalize(new CommandRequest
        {
            Shell = ShellKind.PowerShell,
            Command = "Write-Output 'READY'; [Console]::Out.Write(('x' * 5000))",
            ReadyPattern = "READY",
            MaxOutputBytes = 1024,
            TimeoutMs = 10000
        }, CommandKind.Foreground);
        var result = await new ProcessExecutor().RunAsync(command, TestContext.Current.CancellationToken);
        Assert.True(result.Ready);
        Assert.DoesNotContain("READY", result.Stdout);
        Assert.True(result.StdoutBytes > 1024);
    }

    [Fact]
    public async Task UnicodePowerShell_IsCaptured()
    {
        var command = CommandValidator.Normalize(new CommandRequest { Shell = ShellKind.PowerShell, Command = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); Write-Output '雪'", TimeoutMs = 10000 }, CommandKind.Foreground);
        var result = await new ProcessExecutor().RunAsync(command, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("雪", result.Stdout);
    }
}
