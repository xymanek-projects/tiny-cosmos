using System.Security.Cryptography;
using System.Text;
using TinyCosmos.Core;

namespace TinyCosmos.Linux;

public sealed record SeedFile(string RelativePath, string Sha256, long Length, bool Executable);

public sealed record SeedManifest(string SourceRoot, bool IsGitRepository, string? HeadCommit, IReadOnlyList<SeedFile> Files)
{
    public string DirtyStateDigest()
    {
        using var sha = SHA256.Create();
        foreach (var file in Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            var line = $"{file.RelativePath}\0{file.Sha256}\0{file.Length}\0{file.Executable}\n";
            _ = sha.TransformBlock(Encoding.UTF8.GetBytes(line), 0, Encoding.UTF8.GetByteCount(line), null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }
}

public static class WorkspaceSeeder
{
    public static async Task<SeedManifest> CreateManifestAsync(string sourceRoot, CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(fullRoot);
        }

        var gitRoot = await TryGetGitRootAsync(fullRoot, cancellationToken).ConfigureAwait(false);
        var files = gitRoot is null
            ? EnumerateNonGitFiles(fullRoot)
            : await EnumerateGitFilesAsync(gitRoot, cancellationToken).ConfigureAwait(false);
        return new SeedManifest(
            gitRoot ?? fullRoot,
            gitRoot is not null,
            gitRoot is null ? null : await GitOutputAsync(gitRoot, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false),
            files.Select(file => BuildSeedFile(gitRoot ?? fullRoot, file)).OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private static async Task<string?> TryGetGitRootAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("git", ["-C", path, "rev-parse", "--show-toplevel"], timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Stdout.Trim() : null;
    }

    private static async Task<IReadOnlyList<string>> EnumerateGitFilesAsync(string root, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            "git",
            ["-C", root, "ls-files", "-z", "--cached", "--modified", "--others", "--exclude-standard"],
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.Stderr);
        }

        return result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(relative => Path.Combine(root, relative))
            .Where(File.Exists)
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateNonGitFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => ArchiveSafety.ValidateEntry(new ArchiveEntrySpec(Path.GetRelativePath(root, path), ArchiveEntryKind.RegularFile)) is null)
            .ToArray();
    }

    private static SeedFile BuildSeedFile(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        var validationError = ArchiveSafety.ValidateEntry(new ArchiveEntrySpec(relative, ArchiveEntryKind.RegularFile));
        if (validationError is not null)
        {
            throw new TinyCosmosException(validationError);
        }

        var mode = File.GetUnixFileMode(path);
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexStringLower(sha.ComputeHash(stream));
        return new SeedFile(relative, hash, stream.Length, mode.HasFlag(UnixFileMode.UserExecute));
    }

    private static async Task<string> GitOutputAsync(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("git", ["-C", root, .. arguments], timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.Stderr);
        }

        return result.Stdout.Trim();
    }
}

public sealed record GitHandoffPlan(string BranchName, string WorktreePath, IReadOnlyList<string> Commands);

public static class GitHandoff
{
    public static GitHandoffPlan Plan(string hostRepository, string branchName, string worktreePath)
    {
        if (!branchName.StartsWith("tiny-cosmos/", StringComparison.Ordinal))
        {
            throw new ArgumentException("MVP handoff branches must use the tiny-cosmos/ prefix.", nameof(branchName));
        }

        var fullWorktree = Path.GetFullPath(worktreePath);
        if (Directory.Exists(fullWorktree) && Directory.EnumerateFileSystemEntries(fullWorktree).Any())
        {
            throw new IOException("Handoff worktree destination already exists and is not empty.");
        }

        return new GitHandoffPlan(
            branchName,
            fullWorktree,
            [
                $"git -C {Quote(hostRepository)} fetch . refs/tiny-cosmos/export:refs/tiny-cosmos/import",
                $"git -C {Quote(hostRepository)} worktree add --no-checkout -b {Quote(branchName)} {Quote(fullWorktree)} refs/tiny-cosmos/import"
            ]);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

public sealed record VscodeSshHostEntry(string Alias, string HostName, int Port, string User, string IdentityFile);

public sealed record VscodeOpenPlan(string RemoteUri, string FileName, IReadOnlyList<string> Arguments);

public static class VscodeSshConfig
{
    public static string RenderManagedBlock(VscodeSshHostEntry entry)
    {
        return $$"""
            # >>> tiny-cosmos {{entry.Alias}}
            Host {{entry.Alias}}
              HostName {{entry.HostName}}
              Port {{entry.Port}}
              User {{entry.User}}
              IdentityFile {{entry.IdentityFile}}
              IdentitiesOnly yes
              StrictHostKeyChecking yes
              UserKnownHostsFile ~/.ssh/tiny-cosmos-known-hosts
              ControlMaster auto
              ControlPersist 60
              ControlPath ~/.ssh/tiny-cosmos-{{entry.Alias}}-%r@%h:%p
            # <<< tiny-cosmos {{entry.Alias}}
            """;
    }

    public static VscodeOpenPlan PlanOpen(string alias, string remotePath, string codeExecutable = "code")
    {
        ValidateAlias(alias);
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("VS Code remote path must be an absolute guest path.", nameof(remotePath));
        }

        if (remotePath.Contains('\0') || remotePath.Contains('\n') || remotePath.Contains('\r'))
        {
            throw new ArgumentException("VS Code remote path must not contain control characters.", nameof(remotePath));
        }

        if (string.IsNullOrWhiteSpace(codeExecutable))
        {
            throw new ArgumentException("VS Code executable must not be empty.", nameof(codeExecutable));
        }

        var uri = "vscode-remote://ssh-remote+" + Uri.EscapeDataString(alias) + EscapeRemotePath(remotePath);
        return new VscodeOpenPlan(uri, codeExecutable, ["--folder-uri", uri]);
    }

    private static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("VS Code SSH alias must not be empty.", nameof(alias));
        }

        if (alias.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\'))
        {
            throw new ArgumentException("VS Code SSH alias must not contain whitespace, control characters, or path separators.", nameof(alias));
        }
    }

    private static string EscapeRemotePath(string remotePath)
    {
        return string.Join(
            "/",
            remotePath.Split('/').Select((segment, index) => index == 0 ? string.Empty : Uri.EscapeDataString(segment)));
    }
}

public static class DiskProvisioner
{
    public static async Task CreateSparseExt4Async(string path, long sizeGiB, CancellationToken cancellationToken = default)
    {
        if (sizeGiB is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeGiB), "Disk size must be between 1 and 1024 GiB.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var size = $"{sizeGiB}G";
        var truncate = await ProcessRunner.RunAsync("truncate", ["-s", size, path], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (truncate.ExitCode != 0)
        {
            throw new InvalidOperationException(truncate.Stderr);
        }

        var mkfs = await ProcessRunner.RunAsync("mkfs.ext4", ["-F", "-q", path], timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (mkfs.ExitCode != 0)
        {
            throw new InvalidOperationException(mkfs.Stderr);
        }
    }
}
