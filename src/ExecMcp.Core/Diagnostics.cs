using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExecMcp.Core;

public static class DiagnosticsService
{
    public static Dictionary<string, object?> Resolve(string executable)
    {
        var path = ExecutableResolver.Resolve(executable);
        return new Dictionary<string, object?>
        {
            ["executable"] = executable,
            ["resolved_path"] = path,
            ["exists"] = File.Exists(path)
        };
    }

    public static Dictionary<string, object?> PathInfo(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("path must be a non-empty string");
        var full = Path.GetFullPath(value);
        try
        {
            var attributes = File.GetAttributes(full);
            var directory = attributes.HasFlag(FileAttributes.Directory);
            var size = directory ? (long?)null : new FileInfo(full).Length;
            return new Dictionary<string, object?>
            {
                ["path"] = value, ["normalized_path"] = full, ["exists"] = true,
                ["type"] = directory ? "directory" : "file", ["size"] = size, ["accessible"] = true
            };
        }
        catch (FileNotFoundException) { return MissingPath(value, full, "ENOENT"); }
        catch (DirectoryNotFoundException) { return MissingPath(value, full, "ENOENT"); }
        catch (UnauthorizedAccessException) { return MissingPath(value, full, "EACCES"); }
        catch (IOException ex)
        {
            var result = MissingPath(value, full, "EIO");
            result["error_message"] = ex.Message;
            return result;
        }
    }

    private static Dictionary<string, object?> MissingPath(string input, string full, string error) => new()
    {
        ["path"] = input, ["normalized_path"] = full, ["exists"] = false,
        ["type"] = null, ["size"] = null, ["accessible"] = false, ["error"] = error
    };

    public static Dictionary<string, object?> Doctor()
    {
        var checks = new Dictionary<string, object?>
        {
            ["windows"] = OperatingSystem.IsWindows(),
            ["x64_process"] = Environment.Is64BitProcess,
            ["os_version"] = Environment.OSVersion.VersionString,
            ["process_path"] = Environment.ProcessPath,
            ["state_dir"] = StatePaths.V2,
            ["config_path"] = StatePaths.ConfigFile,
            ["packaged"] = PackageIdentity.IsPackaged
        };
        foreach (var shell in new[] { "cmd.exe", "powershell.exe", "pwsh.exe" })
        {
            try { checks[shell] = ExecutableResolver.Resolve(shell); }
            catch { checks[shell] = null; }
        }
        return checks;
    }
}

public static class PortInspector
{
    public static async Task<Dictionary<string, object?>> InspectAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var entries = new List<Dictionary<string, object?>>();
        var info = new ProcessStartInfo { FileName = "netstat.exe", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("-ano"); info.ArgumentList.Add("-p"); info.ArgumentList.Add("TCP");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start netstat.exe");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryEndpointPort(parts[1], out var localPort) || localPort != port) continue;
            _ = int.TryParse(parts[^1], out var pid);
            string? name = null;
            if (pid > 0) try { using var owner = Process.GetProcessById(pid); name = owner.ProcessName; } catch { }
            entries.Add(new Dictionary<string, object?>
            {
                ["protocol"] = "tcp", ["local_address"] = parts[1], ["remote_address"] = parts[2], ["state"] = parts[3].ToLowerInvariant(), ["pid"] = pid, ["process_name"] = name
            });
        }
        return new Dictionary<string, object?> { ["port"] = port, ["listeners"] = entries };
    }

    private static bool TryEndpointPort(string endpoint, out int port)
    {
        port = 0;
        var index = endpoint.LastIndexOf(':');
        return index >= 0 && int.TryParse(endpoint[(index + 1)..], out port);
    }
}

public static class PackageIdentity
{
    private const int AppmodelErrorNoPackage = 15700;
    public static bool IsPackaged
    {
        get
        {
            uint length = 0;
            var result = Native.GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return result != AppmodelErrorNoPackage;
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
        internal static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
    }
}
