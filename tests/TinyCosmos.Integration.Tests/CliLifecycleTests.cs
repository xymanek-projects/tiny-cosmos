using System.Diagnostics;

namespace TinyCosmos.Integration.Tests;

public sealed class CliLifecycleTests
{
    [Fact]
    public async Task SetupAndCleanupDryRunPrintPackageLifecycleCommands()
    {
        var setup = await RunCliAsync("setup", "--dry-run");
        var cleanup = await RunCliAsync("cleanup", "--purge", "--dry-run");

        Assert.Equal(0, setup.ExitCode);
        Assert.Contains("/usr/bin/systemctl enable --now tinycosmos-broker.socket", setup.Stdout);
        Assert.Contains("/usr/bin/systemctl --global enable tinycosmos-manager.service", setup.Stdout);
        Assert.Equal(0, cleanup.ExitCode);
        Assert.Contains("/usr/bin/systemctl disable --now tinycosmos-broker.socket", cleanup.Stdout);
        Assert.Contains("/usr/bin/rm -rf /var/lib/tiny-cosmos", cleanup.Stdout);
    }

    private static async Task<ProcessResult> RunCliAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(FindCliAssembly());
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string FindCliAssembly()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "TinyCosmos.Cli", "bin", "Debug", "net10.0", "linux-x64", "TinyCosmos.Cli.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate TinyCosmos.Cli.dll from the test output directory.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
