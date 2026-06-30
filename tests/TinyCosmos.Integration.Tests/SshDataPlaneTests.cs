using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Manager;
using TinyCosmos.Protocol;

namespace TinyCosmos.Integration.Tests;

public sealed class SshDataPlaneTests
{
    [Fact]
    public void SshPlannerUsesStrictHostCheckingAndCommandVectorQuoting()
    {
        var plan = SshCommandPlanner.PlanExec(new SshExecRequest(
            new SshTarget("172.31.1.2", 22, "agent", "/tmp/key", "/tmp/known_hosts", "/tmp/cm-%h"),
            "/workspace/project with space",
            ["bash", "-lc", "printf '%s' hello"],
            30,
            4096));

        Assert.Equal("/usr/bin/ssh", plan.FileName);
        Assert.Contains("StrictHostKeyChecking=yes", plan.Arguments);
        Assert.Contains("UserKnownHostsFile=/tmp/known_hosts", plan.Arguments);
        Assert.Contains("ControlPath=/tmp/cm-%h", plan.Arguments);
        Assert.Contains("'printf '\\''%s'\\'' hello'", plan.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("cd '/workspace/project with space' && exec", plan.Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void SshPlannerRequestsPtyWhenExecPayloadNeedsTerminalSemantics()
    {
        var plan = SshCommandPlanner.PlanExec(new SshExecRequest(
            new SshTarget("172.31.1.2", 22, "agent", "/tmp/key", "/tmp/known_hosts", null),
            "/workspace/project",
            ["bash", "-lc", "stty -a"],
            30,
            4096,
            PseudoTerminal: true));

        Assert.Contains("-tt", plan.Arguments);
        Assert.Contains("stty -a", plan.Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SshGuestExecutorTruncatesOutputWithoutShellingOutToHostCommand()
    {
        var executor = new SshGuestExecutor(new SshTarget("127.0.0.1", 1, "agent", "/tmp/key", "/tmp/known_hosts", null));
        var result = await executor.ExecuteAsync(new ExecPayload(1000, "grp", "/workspace", ["true"], 1, 10));

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void SftpPlannerUsesStrictHostCheckingAndBatchFiles()
    {
        var target = new SshTarget("172.31.1.2", 22, "agent", "/tmp/key", "/tmp/known_hosts", "/tmp/cm-%h");
        var plan = SftpCommandPlanner.PlanRead(new SftpReadRequest(
            target,
            "/workspace/project/file with space.txt",
            "/tmp/tinycosmos-read",
            "/tmp/tinycosmos-batch"));

        Assert.Equal("/usr/bin/sftp", plan.FileName);
        Assert.Contains("-b", plan.Arguments);
        Assert.Contains("/tmp/tinycosmos-batch", plan.Arguments);
        Assert.Contains("StrictHostKeyChecking=yes", plan.Arguments);
        Assert.Contains("UserKnownHostsFile=/tmp/known_hosts", plan.Arguments);
        Assert.Contains("ControlPath=/tmp/cm-%h", plan.Arguments);
        Assert.Equal("get -p \"/workspace/project/file with space.txt\" \"/tmp/tinycosmos-read\"\n", plan.BatchText);
    }

    [Fact]
    public void SftpPlannerCanPlanExecutableWritesAndDirectoryLists()
    {
        var target = new SshTarget("172.31.1.2", 22, "agent", "/tmp/key", "/tmp/known_hosts", null);

        var write = SftpCommandPlanner.PlanWrite(new SftpWriteRequest(
            target,
            "/tmp/local-script",
            "/workspace/project/script.sh",
            "/tmp/write-batch",
            Executable: true));
        var list = SftpCommandPlanner.PlanList(new SftpListRequest(target, "/workspace/project", "/tmp/list-batch"));

        Assert.Contains("put -p \"/tmp/local-script\" \"/workspace/project/script.sh\"", write.BatchText, StringComparison.Ordinal);
        Assert.Contains("chmod 755 \"/workspace/project/script.sh\"", write.BatchText, StringComparison.Ordinal);
        Assert.Equal("ls -la \"/workspace/project\"\n", list.BatchText);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/workspace/../secret")]
    [InlineData("/workspace/project\nbad")]
    public void SftpPlannerRejectsUnsafeGuestPaths(string path)
    {
        var target = new SshTarget("172.31.1.2", 22, "agent", "/tmp/key", "/tmp/known_hosts", null);

        Assert.Throws<ArgumentException>(() => SftpCommandPlanner.PlanList(new SftpListRequest(target, path, "/tmp/batch")));
    }

    [Fact]
    public async Task OpenSshGuestExecutorUsesSandboxAddressAndTruncatesOutput()
    {
        using var temp = new TempDir();
        var runner = new RecordingProcessRunner((fileName, _, _, _) =>
            new ProcessRunResult(fileName == "/usr/bin/ssh" ? 0 : 1, "abcdef", string.Empty, TimedOut: false));
        var executor = new OpenSshGuestExecutor(Options(temp.Path), runner);
        var group = Group();

        var result = await executor.ExecuteAsync(group, new ExecPayload(1000, group.Group.GroupId.Value, "/workspace/project", ["printf", "abcdef"], 10, 3), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("abc", result.Stdout);
        Assert.True(result.Truncated);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("/usr/bin/ssh", call.FileName);
        Assert.Contains("agent@172.31.42.2", call.Arguments);
        Assert.Contains(call.Arguments, argument => argument.Contains("ControlPath=", StringComparison.Ordinal) && argument.Contains("/tc-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenSshGuestExecutorRemovesStaleControlSocketBeforeExec()
    {
        using var temp = new TempDir();
        var options = Options(temp.Path);
        var group = Group();
        string? controlPath = null;
        var callCount = 0;

        var runner = new RecordingProcessRunner((fileName, _, _, _) =>
        {
            callCount++;
            if (callCount == 2)
            {
                Assert.NotNull(controlPath);
                Assert.False(File.Exists(controlPath));
            }
            return new ProcessRunResult(fileName == "/usr/bin/ssh" ? 0 : 1, "ok", string.Empty, TimedOut: false);
        });
        var executor = new OpenSshGuestExecutor(options, runner);

        await executor.ExecuteAsync(group, new ExecPayload(1000, group.Group.GroupId.Value, "/workspace/project", ["true"], 10, 1024), CancellationToken.None);
        controlPath = runner.Calls[0].Arguments.Single(argument => argument.StartsWith("ControlPath=", StringComparison.Ordinal))["ControlPath=".Length..];
        await File.WriteAllTextAsync(controlPath, "stale");

        var result = await executor.ExecuteAsync(group, new ExecPayload(1000, group.Group.GroupId.Value, "/workspace/project", ["true"], 10, 1024), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Stdout);
    }

    [Fact]
    public async Task OpenSftpFileServiceReadsThroughBatchFileAndHonorsReadLimit()
    {
        using var temp = new TempDir();
        var capturedBatch = string.Empty;
        var runner = new RecordingProcessRunner((fileName, arguments, _, _) =>
        {
            if (fileName == "/usr/bin/sftp")
            {
                var batch = arguments[FindArgument(arguments, "-b") + 1];
                capturedBatch = File.ReadAllText(batch);
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(batch)!, "read.bin"), "hello"u8.ToArray());
                return new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: false);
            }

            return new ProcessRunResult(1, string.Empty, "unexpected", TimedOut: false);
        });
        var files = new OpenSftpGuestFileService(Options(temp.Path), runner);

        var result = await files.ReadAsync(Group(), new FileReadPayload(1000, "grp_test", "/workspace/project/file.txt", 3), CancellationToken.None);

        Assert.Equal("hel", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(result.ContentBase64)));
        Assert.True(result.Truncated);
        Assert.Single(runner.Calls);
        Assert.Contains("get -p \"/workspace/project/file.txt\"", capturedBatch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenSftpFileServiceChecksOverwritePolicyBeforeWrite()
    {
        using var temp = new TempDir();
        var runner = new RecordingProcessRunner((fileName, _, _, _) =>
            new ProcessRunResult(fileName == "/usr/bin/ssh" ? 1 : 0, string.Empty, string.Empty, TimedOut: false));
        var files = new OpenSftpGuestFileService(Options(temp.Path), runner);

        var ex = await Assert.ThrowsAsync<TinyCosmosException>(() => files.WriteAsync(
            Group(),
            new FileWritePayload(1000, "grp_test", "/workspace/project/existing.txt", Convert.ToBase64String("data"u8.ToArray()), Overwrite: false, Executable: false),
            CancellationToken.None));

        Assert.Equal(TinyCosmosErrorCode.InvalidRequest, ex.Error.Code);
        Assert.Single(runner.Calls);
        Assert.Equal("/usr/bin/ssh", runner.Calls[0].FileName);
    }

    [Fact]
    public async Task OpenSftpFileServiceParsesListAndSearchResults()
    {
        using var temp = new TempDir();
        var runner = new RecordingProcessRunner((fileName, _, _, _) =>
        {
            if (fileName == "/usr/bin/sftp")
            {
                return new ProcessRunResult(
                    0,
                    "-rw-r--r--    1 agent agent       12 Jan 01 00:00 hello.txt\n" +
                    "drwxr-xr-x    2 agent agent     4096 Jan 01 00:00 src\n",
                    string.Empty,
                    TimedOut: false);
            }

            return new ProcessRunResult(
                0,
                "/workspace/project/hello.txt:7:hello tiny cosmos\n",
                string.Empty,
                TimedOut: false);
        });
        var files = new OpenSftpGuestFileService(Options(temp.Path), runner);

        var list = await files.ListAsync(Group(), new FileListPayload(1000, "grp_test", "/workspace/project", Recursive: false, MaxEntries: 10), CancellationToken.None);
        var search = await files.SearchAsync(Group(), new FileSearchPayload(1000, "grp_test", "/workspace/project", "tiny", MaxMatches: 10), CancellationToken.None);

        Assert.Contains(list.Entries, entry => entry.Path == "/workspace/project/hello.txt" && !entry.IsDirectory && entry.Length == 12);
        Assert.Contains(list.Entries, entry => entry.Path == "/workspace/project/src" && entry.IsDirectory);
        var searchCommand = runner.Calls.Last(call => call.FileName == "/usr/bin/ssh").Arguments.Last();
        Assert.Contains("/usr/bin/find", searchCommand, StringComparison.Ordinal);
        Assert.Contains("lost+found", searchCommand, StringComparison.Ordinal);
        Assert.Contains("tinycosmos-search", searchCommand, StringComparison.Ordinal);
        var match = Assert.Single(search.Matches);
        Assert.Equal("/workspace/project/hello.txt", match.Path);
        Assert.Equal(7, match.LineNumber);
        Assert.Equal("hello tiny cosmos", match.Line);
    }

    [Fact]
    public async Task OpenSftpFileServiceTreatsSearchWithoutMatchesAsEmptyResult()
    {
        using var temp = new TempDir();
        var runner = new RecordingProcessRunner((fileName, _, _, _) =>
            new ProcessRunResult(fileName == "/usr/bin/ssh" ? 0 : 1, string.Empty, string.Empty, TimedOut: false));
        var files = new OpenSftpGuestFileService(Options(temp.Path), runner);

        var search = await files.SearchAsync(Group(), new FileSearchPayload(1000, "grp_test", "/workspace/project", "absent", MaxMatches: 10), CancellationToken.None);

        Assert.Empty(search.Matches);
        Assert.False(search.Truncated);
    }

    private static GuestSshOptions Options(string root)
    {
        return new GuestSshOptions(
            "agent",
            22,
            Path.Combine(root, "id_ed25519"),
            Path.Combine(root, "known_hosts"),
            Path.Combine(root, "control"),
            Path.Combine(root, "tmp"));
    }

    private static GroupBundle Group()
    {
        var groupId = new GroupId("grp_testfixture1234567");
        var sandboxId = new SandboxId("sbx_testfixture1234567");
        return new GroupBundle(
            new GroupRecord(groupId, new LogicalGroupName("opencode:test"), 1000, new GroupMetadata(null, "/workspace/project", null), sandboxId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new SandboxRecord(
                sandboxId,
                groupId,
                LifecycleState.Ready,
                IsPrimary: true,
                "image",
                ResourceAllocation.Default,
                new NetworkAllocation("172.31.42.0/24", "172.31.42.1", "172.31.42.2"),
                "system",
                "workspace",
                StopReason.None,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch));
    }

    private static int FindArgument(IReadOnlyList<string> arguments, string value)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == value)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Argument not found: " + value);
    }

    private sealed class RecordingProcessRunner(Func<string, IReadOnlyList<string>, string?, TimeSpan?, ProcessRunResult> handler) : IProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            var call = new ProcessCall(fileName, arguments.ToArray(), workingDirectory, timeout);
            Calls.Add(call);
            return Task.FromResult(handler(fileName, call.Arguments, workingDirectory, timeout));
        }
    }

    private sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory, TimeSpan? Timeout);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-ssh-test-" + Guid.NewGuid().ToString("N"));

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
