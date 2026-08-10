using System.Diagnostics;
using System.Text;
using ExecMcp.Core;
namespace ExecMcp.Tests;

public sealed class JobObjectIntegrationTests
{
    [Fact]
    public async Task TerminatingJobObject_RemovesDescendantProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "execmcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "child.pid");
        var script = $"Start-Sleep -Milliseconds 750; $p=Start-Process powershell.exe -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -PassThru; Set-Content -LiteralPath '{pidFile.Replace("'", "''")}' -Value $p.Id -NoNewline; Wait-Process -Id $p.Id";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var parent = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }
        }) ?? throw new InvalidOperationException("Could not start parent process");
        using var job = WindowsJobObject.Create("test_" + Guid.NewGuid().ToString("N"));
        job.Assign(parent);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(pidFile) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(File.Exists(pidFile), "The parent did not report its child PID");
        var childPid = int.Parse(await File.ReadAllTextAsync(pidFile));

        job.Terminate(1);
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        Assert.False(IsRunning(childPid));
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }
}
