using System.Globalization;
using System.Text;
using TinyCosmos.Core;
using TinyCosmos.Linux;

namespace TinyCosmos.Manager;

public interface IGuestWorkspaceSeeder
{
    Task SeedAsync(GroupBundle target, CancellationToken cancellationToken);
}

public sealed class NoopGuestWorkspaceSeeder : IGuestWorkspaceSeeder
{
    public Task SeedAsync(GroupBundle target, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class OpenSshGuestWorkspaceSeeder(GuestSshOptions options, IProcessRunner? processRunner = null) : IGuestWorkspaceSeeder
{
    private readonly IProcessRunner _processRunner = processRunner ?? new DefaultProcessRunner();

    public async Task SeedAsync(GroupBundle target, CancellationToken cancellationToken)
    {
        var sourceRoot = target.Group.Metadata.HostWorkspacePath;
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return;
        }

        var guestProjectPath = target.Group.Metadata.GuestProjectPath ?? "/workspace/project";
        ValidateGuestPath(guestProjectPath);

        var manifest = await WorkspaceSeeder.CreateManifestAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        using var temp = TemporaryGuestIo.Create(options.TempDirectory);
        var listPath = Path.Combine(temp.Path, "seed.files");
        var archivePath = Path.Combine(temp.Path, "seed.tar");
        var batchPath = Path.Combine(temp.Path, "seed.batch");
        await WriteNullDelimitedListAsync(listPath, manifest.Files.Select(file => file.RelativePath), cancellationToken).ConfigureAwait(false);

        var tar = await _processRunner.RunAsync(
            "/usr/bin/tar",
            ["-C", manifest.SourceRoot, "--null", "-T", listPath, "-cf", archivePath],
            null,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (tar.ExitCode != 0)
        {
            throw ToSeedError(tar, "Could not create workspace seed archive.");
        }

        var sshTarget = OpenSshGuestExecutor.ToTarget(options, target);
        OpenSshGuestExecutor.RemoveControlSocket(sshTarget);
        var remoteArchive = "/tmp/tinycosmos-seed-" + TinyId.NewOperationId().Value + ".tar";
        var upload = SftpCommandPlanner.PlanWrite(new SftpWriteRequest(sshTarget, archivePath, remoteArchive, batchPath, Executable: false));
        await File.WriteAllTextAsync(batchPath, upload.BatchText, cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(upload.FileName, upload.Arguments, TimeSpan.FromMinutes(2), cancellationToken, "Could not upload workspace seed archive.").ConfigureAwait(false);

        OpenSshGuestExecutor.RemoveControlSocket(sshTarget);
        var extract = SshCommandPlanner.PlanExec(new SshExecRequest(
            sshTarget,
            "/",
            [
                "/bin/bash",
                "-lc",
                """
                set -euo pipefail
                dest=$1
                archive=$2
                digest=$3
                file_count=$4
                /usr/bin/mkdir -p "$dest"
                if [ -n "$(/usr/bin/find "$dest" -mindepth 1 -maxdepth 1 ! -name lost+found ! -name .tinycosmos-seed -print -quit)" ]; then
                  /usr/bin/rm -f "$archive"
                  exit 0
                fi
                /usr/bin/tar -C "$dest" -xpf "$archive"
                /usr/bin/printf 'digest=%s\nfiles=%s\n' "$digest" "$file_count" > "$dest/.tinycosmos-seed"
                /usr/bin/rm -f "$archive"
                """,
                "tinycosmos-seed",
                guestProjectPath,
                remoteArchive,
                manifest.DirtyStateDigest(),
                manifest.Files.Count.ToString(CultureInfo.InvariantCulture)
            ],
            TimeoutSeconds: 300,
            OutputLimitBytes: 4096));
        await RunCheckedAsync(extract.FileName, extract.Arguments, TimeSpan.FromMinutes(5), cancellationToken, "Could not extract workspace seed archive.").ConfigureAwait(false);
    }

    private async Task RunCheckedAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string message)
    {
        var run = await _processRunner.RunAsync(fileName, arguments, null, timeout, cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw ToSeedError(run, message);
        }
    }

    private static async Task WriteNullDelimitedListAsync(string path, IEnumerable<string> entries, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        foreach (var entry in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateGuestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\0') || path.Any(char.IsControl))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Guest seed path must be an absolute POSIX path."));
        }

        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Guest seed path must not contain '..' segments."));
            }
        }
    }

    private static TinyCosmosException ToSeedError(ProcessRunResult run, string message) =>
        new(new TinyCosmosError(
            run.TimedOut ? TinyCosmosErrorCode.NotReady : TinyCosmosErrorCode.PlatformRejected,
            message,
            string.IsNullOrWhiteSpace(run.Stderr) ? null : OutputSanitizer.SanitizeStructuredText(run.Stderr)));
}
