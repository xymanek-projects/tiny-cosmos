using System.Diagnostics;
using System.Runtime.InteropServices;
using TinyCosmos.Core;

namespace TinyCosmos.Linux;

public sealed record PeerCredentials(int Pid, int Uid, int Gid);

public static partial class LinuxInterop
{
    private const int SolSocket = 1;
    private const int SoPeercred = 17;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getsockopt(int sockfd, int level, int optname, out UCred optval, ref uint optlen);

    [StructLayout(LayoutKind.Sequential)]
    private struct UCred
    {
        public int Pid;
        public int Uid;
        public int Gid;
    }

    public static PeerCredentials GetPeerCredentials(int socketFd)
    {
        var length = (uint)Marshal.SizeOf<UCred>();
        if (getsockopt(socketFd, SolSocket, SoPeercred, out var cred, ref length) != 0)
        {
            throw new InvalidOperationException("SO_PEERCRED failed with errno " + Marshal.GetLastPInvokeError());
        }

        return new PeerCredentials(cred.Pid, cred.Uid, cred.Gid);
    }
}

public sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public static class ProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            cts.CancelAfter(timeout.Value);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new ProcessRunResult(-1, string.Empty, "Timed out.", true);
        }
    }
}

public sealed record FirecrackerVmConfig(
    string FirecrackerPath,
    string JailerPath,
    string KernelImagePath,
    string? InitrdImagePath,
    string RootDrivePath,
    string WorkspaceDrivePath,
    string ApiSocketPath,
    string VsockSocketPath,
    int Vcpu,
    long MemoryMiB);

public static class BrokerRequestValidator
{
    public static TinyCosmosError? ValidateFirecrackerConfig(FirecrackerVmConfig config)
    {
        if (!IsApprovedAbsolutePath(config.FirecrackerPath, "/usr/lib/tiny-cosmos/bin/firecracker") ||
            !IsApprovedAbsolutePath(config.JailerPath, "/usr/lib/tiny-cosmos/bin/jailer"))
        {
            return Reject("Firecracker and jailer paths must be pinned Tiny Cosmos installation paths.");
        }

        if (!IsUnder(config.KernelImagePath, "/usr/lib/tiny-cosmos/images") ||
            (config.InitrdImagePath is not null && !IsUnder(config.InitrdImagePath, "/usr/lib/tiny-cosmos/images")) ||
            !IsUnder(config.RootDrivePath, "/var/lib/tiny-cosmos") ||
            !IsUnder(config.WorkspaceDrivePath, "/var/lib/tiny-cosmos") ||
            !IsUnder(config.ApiSocketPath, "/srv/jailer") ||
            !IsUnder(config.VsockSocketPath, "/srv/jailer"))
        {
            return Reject("Broker paths must stay under approved Tiny Cosmos directories.");
        }

        if (config.Vcpu is < 1 or > 16 || config.MemoryMiB is < 512 or > 65536)
        {
            return Reject("Requested VM resources are outside the approved MVP bounds.");
        }

        return null;
    }

    private static bool IsApprovedAbsolutePath(string path, string approved) =>
        string.Equals(Path.GetFullPath(path), approved, StringComparison.Ordinal);

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(fullRoot, StringComparison.Ordinal);
    }

    private static TinyCosmosError Reject(string message) => new(TinyCosmosErrorCode.PlatformRejected, message);
}
