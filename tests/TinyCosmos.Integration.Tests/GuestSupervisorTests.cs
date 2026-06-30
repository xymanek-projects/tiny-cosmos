using System.Diagnostics;
using System.Text.Json;
using TinyCosmos.Protocol;

namespace TinyCosmos.Integration.Tests;

public sealed class GuestSupervisorTests
{
    [Fact]
    public async Task GuestSupervisorHelloEmitsGuestControlContract()
    {
        var result = await RunGuestAsync("hello", "op_bootnonce", "sbx_test");

        Assert.Equal(0, result.ExitCode);
        var hello = JsonSerializer.Deserialize(result.Stdout, TinyCosmosJsonContext.Default.GuestHelloResponse);
        Assert.NotNull(hello);
        Assert.Equal("sbx_test", hello!.SandboxId);
        Assert.Equal("op_bootnonce", hello.BootNonce);
        Assert.Contains(GuestControlOperations.InstallSshKey, hello.Capabilities);
    }

    [Fact]
    public async Task GuestSupervisorReadyUsesStructuredReadyReport()
    {
        var result = await RunGuestAsync("ready", "op_bootnonce");

        Assert.Equal(0, result.ExitCode);
        var ready = JsonSerializer.Deserialize(result.Stdout, TinyCosmosJsonContext.Default.GuestReadyReport);
        Assert.NotNull(ready);
        Assert.Equal("op_bootnonce", ready!.BootNonce);
        Assert.Equal("agent", ready.SshUser);
    }

    [Fact]
    public async Task GuestSupervisorFlushShutdownReportsSynchronousFlush()
    {
        var result = await RunGuestAsync("flush-shutdown", "op_bootnonce");

        Assert.Equal(0, result.ExitCode);
        var shutdown = JsonSerializer.Deserialize(result.Stdout, TinyCosmosJsonContext.Default.GuestShutdownResponse);
        Assert.NotNull(shutdown);
        Assert.Equal("op_bootnonce", shutdown!.BootNonce);
        Assert.True(shutdown.FilesystemsFlushed);
        Assert.True(shutdown.ShutdownRequested);
    }

    private static async Task<ProcessResult> RunGuestAsync(params string[] args)
    {
        var start = new ProcessStartInfo(FindGuestExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start guest supervisor.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Guest supervisor command did not exit within 10 seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private static string FindGuestExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "TinyCosmos.Guest", "bin", "Debug", "net10.0", "linux-x64", "TinyCosmos.Guest");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate TinyCosmos.Guest executable from the test output directory.");
    }
}
