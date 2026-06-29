using TinyCosmos.Linux;

namespace TinyCosmos.Integration.Tests;

public sealed class WorkspaceOperationTests
{
    [Fact]
    public async Task GitSeedManifestIncludesTrackedDirtyAndNonIgnoredUntrackedFiles()
    {
        using var temp = new TempDir();
        await RunGitAsync(temp.Path, "init");
        await RunGitAsync(temp.Path, "config", "user.email", "test@example.invalid");
        await RunGitAsync(temp.Path, "config", "user.name", "Tiny Test");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "tracked.txt"), "one");
        await RunGitAsync(temp.Path, "add", "tracked.txt");
        await RunGitAsync(temp.Path, "commit", "-m", "seed");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "tracked.txt"), "two");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "untracked.txt"), "three");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".gitignore"), "ignored.txt\n");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "ignored.txt"), "nope");

        var manifest = await WorkspaceSeeder.CreateManifestAsync(temp.Path);

        Assert.True(manifest.IsGitRepository);
        Assert.Contains(manifest.Files, file => file.RelativePath == "tracked.txt");
        Assert.Contains(manifest.Files, file => file.RelativePath == "untracked.txt");
        Assert.DoesNotContain(manifest.Files, file => file.RelativePath == "ignored.txt");
        Assert.Equal(64, manifest.DirtyStateDigest().Length);
    }

    [Fact]
    public void GitHandoffPlanUsesNoCheckoutAndTinyCosmosBranchPrefix()
    {
        using var temp = new TempDir();
        var plan = GitHandoff.Plan(temp.Path, "tiny-cosmos/project/date-sandbox", Path.Combine(temp.Path, "..", "handoff"));

        Assert.Contains(plan.Commands, command => command.Contains("worktree add --no-checkout", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => GitHandoff.Plan(temp.Path, "feature/nope", Path.Combine(temp.Path, "w")));
    }

    [Fact]
    public void VscodeConfigRendersDelimitedStrictHostEntry()
    {
        var rendered = VscodeSshConfig.RenderManagedBlock(new VscodeSshHostEntry("tc-sbx", "172.31.1.2", 22, "agent", "~/.ssh/tc-key"));

        Assert.Contains(">>> tiny-cosmos tc-sbx", rendered, StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking yes", rendered, StringComparison.Ordinal);
        Assert.Contains("UserKnownHostsFile ~/.ssh/tiny-cosmos-known-hosts", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void VscodeOpenPlanUsesRemoteSshFolderUriAndRejectsUnsafeInputs()
    {
        var plan = VscodeSshConfig.PlanOpen("tc-sbx", "/workspace/project with space", "/usr/bin/code");

        Assert.Equal("/usr/bin/code", plan.FileName);
        Assert.Equal("vscode-remote://ssh-remote+tc-sbx/workspace/project%20with%20space", plan.RemoteUri);
        Assert.Equal(["--folder-uri", plan.RemoteUri], plan.Arguments);
        Assert.Throws<ArgumentException>(() => VscodeSshConfig.PlanOpen("bad alias", "/workspace/project"));
        Assert.Throws<ArgumentException>(() => VscodeSshConfig.PlanOpen("tc-sbx", "relative/path"));
    }

    [Fact]
    public async Task CliExposesSeedExportAndVscodeWorkspaceCommands()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "README.md"), "hello");

        var seed = await RunCliAsync("seed", "manifest", temp.Path);
        Assert.True(seed.ExitCode == 0, seed.Stderr);
        var manifest = System.Text.Json.JsonSerializer.Deserialize(seed.Stdout, LinuxJsonContext.Default.SeedManifest);
        Assert.NotNull(manifest);
        Assert.Contains(manifest!.Files, file => file.RelativePath == "README.md");

        var export = await RunCliAsync("export", "worktree", temp.Path, "tiny-cosmos/test", Path.Combine(temp.Path, "..", "handoff"));
        Assert.True(export.ExitCode == 0, export.Stderr);
        var handoff = System.Text.Json.JsonSerializer.Deserialize(export.Stdout, LinuxJsonContext.Default.GitHandoffPlan);
        Assert.NotNull(handoff);
        Assert.Contains(handoff!.Commands, command => command.Contains("worktree add --no-checkout", StringComparison.Ordinal));

        var vscode = await RunCliAsync("vscode", "ssh-config", "tc-test", "172.31.1.2", "22", "agent", "/tmp/key");
        Assert.True(vscode.ExitCode == 0, vscode.Stderr);
        Assert.Contains("Host tc-test", vscode.Stdout, StringComparison.Ordinal);
        Assert.Contains("IdentityFile /tmp/key", vscode.Stdout, StringComparison.Ordinal);

        var open = await RunCliAsync("vscode", "open", "tc-test", "/workspace/project", "--code", "/usr/bin/code", "--dry-run");
        Assert.True(open.ExitCode == 0, open.Stderr);
        var openPlan = System.Text.Json.JsonSerializer.Deserialize(open.Stdout, LinuxJsonContext.Default.VscodeOpenPlan);
        Assert.NotNull(openPlan);
        Assert.Equal("vscode-remote://ssh-remote+tc-test/workspace/project", openPlan!.RemoteUri);
        Assert.Equal("/usr/bin/code", openPlan.FileName);
    }

    private static async Task RunGitAsync(string cwd, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(10));
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    private static async Task<ProcessRunResult> RunCliAsync(params string[] args)
    {
        var cli = Path.Combine(FindRepositoryRoot(), "src", "TinyCosmos.Cli", "bin", "Debug", "net10.0", "linux-x64", "TinyCosmos.Cli");
        return await ProcessRunner.RunAsync(
            cli,
            args,
            timeout: TimeSpan.FromSeconds(10));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TinyCosmos.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-workspace-" + Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
