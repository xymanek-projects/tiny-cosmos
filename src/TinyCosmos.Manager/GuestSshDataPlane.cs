using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

namespace TinyCosmos.Manager;

public sealed record GuestSshOptions(
    string User,
    int Port,
    string IdentityFile,
    string KnownHostsFile,
    string ControlDirectory,
    string TempDirectory)
{
    public static GuestSshOptions CreateDefault(string stateDirectory)
    {
        var sshDirectory = Path.Combine(stateDirectory, "ssh");
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
        return new GuestSshOptions(
            "agent",
            22,
            Path.Combine(sshDirectory, "id_ed25519"),
            Path.Combine(sshDirectory, "known_hosts"),
            Path.Combine(runtimeDirectory, "tiny-cosmos", "ssh-control"),
            Path.Combine(Path.GetTempPath(), "tiny-cosmos-guest-io"));
    }
}

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        TimeSpan? timeout,
        CancellationToken cancellationToken);
}

public sealed class DefaultProcessRunner : IProcessRunner
{
    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        return ProcessRunner.RunAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);
    }
}

public sealed class OpenSshGuestExecutor(GuestSshOptions options, IProcessRunner? processRunner = null) : IGuestExecutor
{
    private readonly IProcessRunner _processRunner = processRunner ?? new DefaultProcessRunner();

    public async Task<ExecResult> ExecuteAsync(GroupBundle target, ExecPayload payload, CancellationToken cancellationToken)
    {
        var plan = SshCommandPlanner.PlanExec(new SshExecRequest(
            ToTarget(options, target),
            payload.WorkingDirectory,
            payload.Command,
            payload.TimeoutSeconds,
            payload.OutputLimitBytes,
            payload.PseudoTerminal));
        var run = await _processRunner.RunAsync(
            plan.FileName,
            plan.Arguments,
            null,
            TimeSpan.FromSeconds(payload.TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        var stdout = TruncateUtf8(run.Stdout, payload.OutputLimitBytes, out var stdoutTruncated);
        var stderr = TruncateUtf8(run.Stderr, payload.OutputLimitBytes, out var stderrTruncated);
        return new ExecResult(run.TimedOut ? 124 : run.ExitCode, stdout, stderr, stdoutTruncated || stderrTruncated);
    }

    internal static SshTarget ToTarget(GuestSshOptions options, GroupBundle bundle)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.IdentityFile))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.KnownHostsFile))!);
        Directory.CreateDirectory(options.ControlDirectory);
        return new SshTarget(
            bundle.PrimarySandbox.Network.PrimaryAddress,
            options.Port,
            options.User,
            options.IdentityFile,
            options.KnownHostsFile,
            Path.Combine(options.ControlDirectory, BuildControlSocketName(bundle, options)));
    }

    private static string BuildControlSocketName(GroupBundle bundle, GuestSshOptions options)
    {
        var material = string.Join('|',
            bundle.PrimarySandbox.SandboxId.Value,
            bundle.PrimarySandbox.Network.PrimaryAddress,
            options.Port.ToString(CultureInfo.InvariantCulture),
            options.User);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return "tc-" + hash[..24];
    }

    internal static string TruncateUtf8(string value, int limitBytes, out bool truncated)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= limitBytes)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        return Encoding.UTF8.GetString(bytes.AsSpan(0, limitBytes));
    }
}

public sealed class OpenSftpGuestFileService(GuestSshOptions options, IProcessRunner? processRunner = null) : IGuestFileService
{
    private readonly IProcessRunner _processRunner = processRunner ?? new DefaultProcessRunner();

    public async Task<FileReadResult> ReadAsync(GroupBundle target, FileReadPayload payload, CancellationToken cancellationToken)
    {
        using var temp = TemporaryGuestIo.Create(options.TempDirectory);
        var localPath = Path.Combine(temp.Path, "read.bin");
        var batchPath = Path.Combine(temp.Path, "read.batch");
        var plan = SftpCommandPlanner.PlanRead(new SftpReadRequest(ToTarget(target), payload.Path, localPath, batchPath));
        await File.WriteAllTextAsync(batchPath, plan.BatchText, cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(plan.FileName, plan.Arguments, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        var truncated = bytes.Length > payload.MaxBytes;
        var returned = truncated ? bytes.AsSpan(0, payload.MaxBytes).ToArray() : bytes;
        return new FileReadResult(payload.Path, Convert.ToBase64String(returned), truncated);
    }

    public async Task<FileWriteResult> WriteAsync(GroupBundle target, FileWritePayload payload, CancellationToken cancellationToken)
    {
        if (!payload.Overwrite)
        {
            var exists = await RunSshAsync(target, ["/usr/bin/test", "!", "-e", payload.Path], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (exists.ExitCode != 0)
            {
                throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Guest file already exists."));
            }
        }

        using var temp = TemporaryGuestIo.Create(options.TempDirectory);
        var bytes = Convert.FromBase64String(payload.ContentBase64);
        var localPath = Path.Combine(temp.Path, "write.bin");
        var batchPath = Path.Combine(temp.Path, "write.batch");
        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken).ConfigureAwait(false);
        var plan = SftpCommandPlanner.PlanWrite(new SftpWriteRequest(ToTarget(target), localPath, payload.Path, batchPath, payload.Executable));
        await File.WriteAllTextAsync(batchPath, plan.BatchText, cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(plan.FileName, plan.Arguments, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return new FileWriteResult(payload.Path, bytes.Length);
    }

    public async Task<FileListResult> ListAsync(GroupBundle target, FileListPayload payload, CancellationToken cancellationToken)
    {
        if (payload.Recursive)
        {
            return await ListRecursiveAsync(target, payload, cancellationToken).ConfigureAwait(false);
        }

        using var temp = TemporaryGuestIo.Create(options.TempDirectory);
        var batchPath = Path.Combine(temp.Path, "list.batch");
        var plan = SftpCommandPlanner.PlanList(new SftpListRequest(ToTarget(target), payload.Path, batchPath));
        await File.WriteAllTextAsync(batchPath, plan.BatchText, cancellationToken).ConfigureAwait(false);
        var run = await RunCheckedAsync(plan.FileName, plan.Arguments, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var entries = ParseSftpList(payload.Path, run.Stdout)
            .Take(payload.MaxEntries + 1)
            .ToArray();
        return new FileListResult(payload.Path, entries.Take(payload.MaxEntries).ToArray(), entries.Length > payload.MaxEntries);
    }

    public async Task<FileSearchResult> SearchAsync(GroupBundle target, FileSearchPayload payload, CancellationToken cancellationToken)
    {
        var run = await RunSshAsync(
            target,
            ["/usr/bin/grep", "-RInH", "--", payload.Pattern, payload.Path],
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (run.ExitCode is not (0 or 1))
        {
            throw ToGuestDataPlaneError(run);
        }

        var matches = run.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseGrepLine)
            .OfType<FileSearchMatch>()
            .Take(payload.MaxMatches + 1)
            .ToArray();
        return new FileSearchResult(matches.Take(payload.MaxMatches).ToArray(), matches.Length > payload.MaxMatches);
    }

    private SshTarget ToTarget(GroupBundle bundle) => OpenSshGuestExecutor.ToTarget(options, bundle);

    private async Task<FileListResult> ListRecursiveAsync(GroupBundle target, FileListPayload payload, CancellationToken cancellationToken)
    {
        var run = await RunSshAsync(
            target,
            ["/usr/bin/find", payload.Path, "-printf", "%y\t%s\t%p\n"],
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw ToGuestDataPlaneError(run);
        }

        var entries = run.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseFindLine)
            .OfType<FileEntry>()
            .Take(payload.MaxEntries + 1)
            .ToArray();
        return new FileListResult(payload.Path, entries.Take(payload.MaxEntries).ToArray(), entries.Length > payload.MaxEntries);
    }

    private async Task<ProcessRunResult> RunSshAsync(GroupBundle target, IReadOnlyList<string> command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var plan = SshCommandPlanner.PlanExec(new SshExecRequest(
            ToTarget(target),
            "/",
            command,
            (int)timeout.TotalSeconds,
            1024 * 1024));
        return await _processRunner.RunAsync(plan.FileName, plan.Arguments, null, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessRunResult> RunCheckedAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var run = await _processRunner.RunAsync(fileName, arguments, null, timeout, cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw ToGuestDataPlaneError(run);
        }

        return run;
    }

    private static TinyCosmosException ToGuestDataPlaneError(ProcessRunResult run)
    {
        return new TinyCosmosException(new TinyCosmosError(
            run.TimedOut ? TinyCosmosErrorCode.NotReady : TinyCosmosErrorCode.PlatformRejected,
            run.TimedOut ? "Guest data plane command timed out." : "Guest data plane command failed.",
            string.IsNullOrWhiteSpace(run.Stderr) ? null : OutputSanitizer.SanitizeStructuredText(run.Stderr)));
    }

    private static IEnumerable<FileEntry> ParseSftpList(string root, string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 2 || line[0] is not ('-' or 'd' or 'l'))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9 || !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            {
                continue;
            }

            var name = string.Join(' ', parts.Skip(8));
            if (name is "." or "..")
            {
                continue;
            }

            var path = name.StartsWith("/", StringComparison.Ordinal) ? name : root.TrimEnd('/') + "/" + name;
            yield return new FileEntry(path, line[0] == 'd', length, line[3] == 'x', null);
        }
    }

    private static FileEntry? ParseFindLine(string line)
    {
        var parts = line.Split('\t', 3);
        if (parts.Length != 3 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            return null;
        }

        return new FileEntry(parts[2], parts[0] == "d", length, Executable: false, ModifiedAt: null);
    }

    private static FileSearchMatch? ParseGrepLine(string line)
    {
        var first = line.IndexOf(':', StringComparison.Ordinal);
        if (first <= 0)
        {
            return null;
        }

        var second = line.IndexOf(':', first + 1);
        if (second <= first || !int.TryParse(line.AsSpan(first + 1, second - first - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
        {
            return null;
        }

        return new FileSearchMatch(line[..first], lineNumber, line[(second + 1)..]);
    }
}

internal sealed class TemporaryGuestIo : IDisposable
{
    private TemporaryGuestIo(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
    }

    public string Path { get; }

    public static TemporaryGuestIo Create(string root)
    {
        Directory.CreateDirectory(root);
        return new TemporaryGuestIo(System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")));
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
