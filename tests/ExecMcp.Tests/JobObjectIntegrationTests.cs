using System.Diagnostics;
using ExecMcp.Core;
namespace ExecMcp.Tests;

public sealed class JobObjectIntegrationTests
{
    [Fact]
    public async Task TerminatingJobObject_RemovesDescendantProcess()
    {
        using var parent = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "/d", "/s", "/c",
                "ping 127.0.0.1 -n 2 >nul & powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\""
            }
        }) ?? throw new InvalidOperationException("Could not start parent process");
        using var job = WindowsJobObject.Create("test_" + Guid.NewGuid().ToString("N"));
        job.Assign(parent);

        IReadOnlyList<int> members = [];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            members = job.GetProcessIds();
            if (members.Count >= 2) break;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Contains(parent.Id, members);
        Assert.True(members.Count >= 2, $"Expected parent plus descendant in Job Object; found: {string.Join(',', members)}");

        job.Terminate(1);
        await parent.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        foreach (var pid in members)
        {
            var goneBy = DateTime.UtcNow.AddSeconds(5);
            while (IsRunning(pid) && DateTime.UtcNow < goneBy)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(IsRunning(pid), $"PID {pid} survived Job Object termination");
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }
}
