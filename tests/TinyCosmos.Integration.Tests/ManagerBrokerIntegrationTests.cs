using System.Text.Json;
using System.Text;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Manager;
using TinyCosmos.Protocol;

namespace TinyCosmos.Integration.Tests;

public sealed class ManagerBrokerIntegrationTests
{
    [Fact]
    public async Task StartSandboxPlansBrokerResourcesBeforeMarkingReady()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var broker = new LocalPlanningBrokerClient();
        var guestControl = new RecordingGuestControlClient();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new PassingHostPrerequisiteChecker(),
            guestControlClient: guestControl);
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.StartSandbox,
            new SandboxTransitionPayload(1000, group.PrimarySandbox.SandboxId.Value)));

        Assert.True(response.Success, response.Error?.Message);
        var updated = ProtocolJson.FromElement(response.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundle);
        Assert.Equal(LifecycleState.Ready, updated.PrimarySandbox.LifecycleState);
        Assert.Contains(broker.ExecutedCommands, command => command.FileName == "/bin/sh" && command.Arguments[1].Contains("tinycosmos-storage", StringComparison.Ordinal) && command.Arguments[1].Contains("mkfs.ext4", StringComparison.Ordinal));
        Assert.Contains(broker.ExecutedCommands, command => command.FileName == "/usr/sbin/ip");
        Assert.Contains(broker.ExecutedCommands, IsFirecrackerUnitLaunch);
        Assert.Contains(broker.ExecutedCommands, command =>
            command.FileName == "/usr/bin/curl" &&
            command.Arguments.Any(argument => argument.Contains("ip=" + group.PrimarySandbox.Network.PrimaryAddress, StringComparison.Ordinal)));
        Assert.Equal(
            [GuestControlOperations.Hello, GuestControlOperations.InstallSshKey, GuestControlOperations.Ready],
            guestControl.Operations);
        Assert.Equal(group.PrimarySandbox.SandboxId.Value, guestControl.HelloRequests.Single().SandboxId);
    }

    [Fact]
    public async Task StartSandboxReturnsDependencyErrorBeforeBrokerWhenHostPrerequisitesFail()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var broker = new LocalPlanningBrokerClient();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new FailingPrerequisiteChecker());
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.StartSandbox,
            new SandboxTransitionPayload(1000, group.PrimarySandbox.SandboxId.Value)));

        Assert.False(response.Success);
        Assert.Equal(TinyCosmosErrorCode.DependencyMissing, response.Error?.Code);
        Assert.Empty(broker.ExecutedCommands);
    }

    [Fact]
    public async Task StartSandboxRestoresStoppedWhenBrokerProvisioningFails()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            new FailingBrokerClient(new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "planned failure")),
            new PassingHostPrerequisiteChecker(),
            guestControlClient: new RecordingGuestControlClient());
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.StartSandbox,
            new SandboxTransitionPayload(1000, group.PrimarySandbox.SandboxId.Value)));

        Assert.False(response.Success);
        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, response.Error?.Code);
        var after = await store.FindByGroupIdAsync(1000, group.Group.GroupId);
        Assert.Equal(LifecycleState.Stopped, after!.PrimarySandbox.LifecycleState);
        Assert.Equal(StopReason.HostReconcile, after.PrimarySandbox.StopReason);
    }

    [Fact]
    public async Task StartSandboxRestoresStoppedWhenGuestControlFails()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var broker = new LocalPlanningBrokerClient();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new PassingHostPrerequisiteChecker(),
            guestControlClient: new RecordingGuestControlClient(
                new TinyCosmosError(TinyCosmosErrorCode.NotReady, "guest handshake failed")));
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.StartSandbox,
            new SandboxTransitionPayload(1000, group.PrimarySandbox.SandboxId.Value)));

        Assert.False(response.Success);
        Assert.Equal(TinyCosmosErrorCode.NotReady, response.Error?.Code);
        Assert.NotEmpty(broker.ExecutedCommands);
        var after = await store.FindByGroupIdAsync(1000, group.Group.GroupId);
        Assert.Equal(LifecycleState.Stopped, after!.PrimarySandbox.LifecycleState);
        Assert.Equal(StopReason.HostReconcile, after.PrimarySandbox.StopReason);
    }

    [Fact]
    public async Task StopSandboxFlushesGuestBeforeMarkingStopped()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var broker = new LocalPlanningBrokerClient();
        var guestControl = new RecordingGuestControlClient();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new PassingHostPrerequisiteChecker(),
            guestControlClient: guestControl);
        var group = await CreateGroupAsync(server);
        var start = await server.DispatchAsync(Envelope(
            ManagerOperations.StartSandbox,
            new SandboxTransitionPayload(1000, group.PrimarySandbox.SandboxId.Value)));
        Assert.True(start.Success, start.Error?.Message);

        var stop = await server.DispatchAsync(Envelope(
            ManagerOperations.StopSandbox,
            new SandboxTransitionPayload(1000, group.PrimarySandbox.SandboxId.Value)));

        Assert.True(stop.Success, stop.Error?.Message);
        var updated = ProtocolJson.FromElement(stop.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundle);
        Assert.Equal(LifecycleState.Stopped, updated.PrimarySandbox.LifecycleState);
        Assert.Contains(GuestControlOperations.FlushShutdown, guestControl.Operations);
        Assert.Single(guestControl.ShutdownRequests);
        Assert.Contains(broker.ExecutedCommands, command => command.FileName == "/usr/bin/systemctl" && command.Arguments[0] == "stop");
        Assert.DoesNotContain(broker.ExecutedCommands, command => command.FileName == "/usr/bin/rm" && command.Arguments.Contains(BrokerPaths.SandboxRoot(group.Group.GroupId.Value, group.PrimarySandbox.SandboxId.Value)));
    }

    [Fact]
    public async Task IdleStopPassFlushesAndStopsReadySandboxes()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var guestControl = new RecordingGuestControlClient();
        var broker = new LocalPlanningBrokerClient();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new PassingHostPrerequisiteChecker(),
            guestControlClient: guestControl);
        var group = await CreateGroupAsync(server);
        await store.TransitionSandboxAsync(1000, group.PrimarySandbox.SandboxId, LifecycleState.Starting);
        await store.TransitionSandboxAsync(1000, group.PrimarySandbox.SandboxId, LifecycleState.Ready);

        var stopped = await server.StopIdleSandboxesOnceAsync(DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(1, stopped);
        var updated = await store.FindByGroupIdAsync(1000, group.Group.GroupId);
        Assert.Equal(LifecycleState.Stopped, updated!.PrimarySandbox.LifecycleState);
        Assert.Equal(StopReason.Idle, updated.PrimarySandbox.StopReason);
        Assert.Contains(GuestControlOperations.FlushShutdown, guestControl.Operations);
        Assert.Contains(broker.ExecutedCommands, command => command.FileName == "/usr/bin/systemctl" && command.Arguments[0] == "stop");
    }

    [Fact]
    public async Task ExecAutoStartsStoppedSandboxAndUsesGuestExecutor()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var broker = new LocalPlanningBrokerClient();
        var guest = new RecordingGuestExecutor((_, payload) => new ExecResult(0, string.Join(' ', payload.Command), string.Empty, false));
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new PassingHostPrerequisiteChecker(),
            guest,
            new RecordingGuestControlClient());
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.Exec,
            new ExecPayload(1000, group.Group.GroupId.Value, "/workspace/project", ["echo", "hello"], 10, 4096)));

        Assert.True(response.Success, response.Error?.Message);
        var result = ProtocolJson.FromElement(response.Payload!.Value, TinyCosmosJsonContext.Default.ExecResult);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("echo hello", result.Stdout);
        Assert.Single(guest.Requests);
        Assert.Contains(broker.ExecutedCommands, IsFirecrackerUnitLaunch);
    }

    [Fact]
    public async Task ExecPreservesPseudoTerminalFlagForGuestExecutor()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var guest = new RecordingGuestExecutor((_, payload) => new ExecResult(0, payload.PseudoTerminal ? "pty" : "plain", string.Empty, false));
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            new LocalPlanningBrokerClient(),
            new PassingHostPrerequisiteChecker(),
            guest,
            new RecordingGuestControlClient());
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.Exec,
            new ExecPayload(1000, group.Group.GroupId.Value, "/workspace/project", ["bash"], 10, 4096, PseudoTerminal: true)));

        Assert.True(response.Success, response.Error?.Message);
        var result = ProtocolJson.FromElement(response.Payload!.Value, TinyCosmosJsonContext.Default.ExecResult);
        Assert.Equal("pty", result.Stdout);
        Assert.True(Assert.Single(guest.Requests).PseudoTerminal);
    }

    [Fact]
    public async Task FileOperationsAutoStartSandboxAndUseGuestFileService()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var broker = new LocalPlanningBrokerClient();
        var files = new RecordingGuestFileService();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new PassingHostPrerequisiteChecker(),
            guestControlClient: new RecordingGuestControlClient(),
            guestFileService: files);
        var group = await CreateGroupAsync(server);

        var write = await server.DispatchAsync(Envelope(
            ManagerOperations.FileWrite,
            new FileWritePayload(
                1000,
                group.Group.GroupId.Value,
                "/workspace/project/hello.txt",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("hello from guest\n")),
                Overwrite: true,
                Executable: false)));
        Assert.True(write.Success, write.Error?.Message);

        var read = await server.DispatchAsync(Envelope(
            ManagerOperations.FileRead,
            new FileReadPayload(1000, group.Group.GroupId.Value, "/workspace/project/hello.txt", 4096)));
        Assert.True(read.Success, read.Error?.Message);
        var readResult = ProtocolJson.FromElement(read.Payload!.Value, TinyCosmosJsonContext.Default.FileReadResult);
        Assert.Equal("hello from guest\n", Encoding.UTF8.GetString(Convert.FromBase64String(readResult.ContentBase64)));

        var list = await server.DispatchAsync(Envelope(
            ManagerOperations.FileList,
            new FileListPayload(1000, group.Group.GroupId.Value, "/workspace/project", Recursive: true, MaxEntries: 100)));
        Assert.True(list.Success, list.Error?.Message);
        var listResult = ProtocolJson.FromElement(list.Payload!.Value, TinyCosmosJsonContext.Default.FileListResult);
        Assert.Contains(listResult.Entries, entry => entry.Path == "/workspace/project/hello.txt");

        var search = await server.DispatchAsync(Envelope(
            ManagerOperations.FileSearch,
            new FileSearchPayload(1000, group.Group.GroupId.Value, "/workspace/project", "guest", MaxMatches: 10)));
        Assert.True(search.Success, search.Error?.Message);
        var searchResult = ProtocolJson.FromElement(search.Payload!.Value, TinyCosmosJsonContext.Default.FileSearchResult);
        Assert.Contains(searchResult.Matches, match => match.Path == "/workspace/project/hello.txt" && match.LineNumber == 1);
        Assert.Contains(broker.ExecutedCommands, IsFirecrackerUnitLaunch);
        Assert.Equal(4, files.Requests.Count);
    }

    [Fact]
    public async Task FileOperationsRejectRelativeAndParentSegmentPathsBeforeGuestService()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var files = new RecordingGuestFileService();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            new LocalPlanningBrokerClient(),
            new PassingHostPrerequisiteChecker(),
            guestControlClient: new RecordingGuestControlClient(),
            guestFileService: files);
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.FileRead,
            new FileReadPayload(1000, group.Group.GroupId.Value, "/workspace/../secret", 4096)));

        Assert.False(response.Success);
        Assert.Equal(TinyCosmosErrorCode.InvalidRequest, response.Error?.Code);
        Assert.Empty(files.Requests);
    }

    [Fact]
    public async Task DeleteGroupRunsBrokerCleanupBeforeRemovingState()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var broker = new LocalPlanningBrokerClient();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new PassingHostPrerequisiteChecker());
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.DeleteGroup,
            new GroupTargetPayload(1000, group.Group.GroupId.Value)));

        Assert.True(response.Success, response.Error?.Message);
        Assert.Contains(broker.ExecutedCommands, command => command.FileName == "/usr/bin/rm" && command.Arguments.Contains("-rf"));
        Assert.Null(await store.FindByGroupIdAsync(1000, group.Group.GroupId));
    }

    [Fact]
    public async Task DeleteGroupKeepsStateWhenBrokerCleanupFails()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            new FailingBrokerClient(new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "cleanup failed")),
            new PassingHostPrerequisiteChecker());
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.DeleteGroup,
            new GroupTargetPayload(1000, group.Group.GroupId.Value)));

        Assert.False(response.Success);
        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, response.Error?.Code);
        Assert.NotNull(await store.FindByGroupIdAsync(1000, group.Group.GroupId));
    }

    [Fact]
    public async Task ReplacePrimaryRunsBrokerCleanupAndSwapsStoppedPrimary()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var broker = new LocalPlanningBrokerClient();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            broker,
            new PassingHostPrerequisiteChecker());
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.ReplacePrimarySandbox,
            new ReplacePrimarySandboxPayload(1000, group.Group.GroupId.Value, "ubuntu-24.04-dev-v2", null)));

        Assert.True(response.Success, response.Error?.Message);
        var replacement = ProtocolJson.FromElement(response.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundle);
        Assert.Equal(group.Group.GroupId, replacement.Group.GroupId);
        Assert.NotEqual(group.PrimarySandbox.SandboxId, replacement.PrimarySandbox.SandboxId);
        Assert.Equal("ubuntu-24.04-dev-v2", replacement.PrimarySandbox.ImageId);
        Assert.Contains(broker.ExecutedCommands, command => command.FileName == "/usr/bin/rm" && command.Arguments.Contains("-rf"));

        var sandboxes = await store.ListSandboxesForGroupAsync(1000, group.Group.GroupId);
        Assert.Contains(sandboxes, sandbox => sandbox.SandboxId == group.PrimarySandbox.SandboxId && sandbox.LifecycleState == LifecycleState.Retired);
        Assert.Contains(sandboxes, sandbox => sandbox.SandboxId == replacement.PrimarySandbox.SandboxId && sandbox.IsPrimary);
    }

    [Fact]
    public async Task ReplacePrimaryKeepsStateWhenBrokerCleanupFails()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            new FailingBrokerClient(new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "cleanup failed")),
            new PassingHostPrerequisiteChecker());
        var group = await CreateGroupAsync(server);

        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.ReplacePrimarySandbox,
            new ReplacePrimarySandboxPayload(1000, group.Group.GroupId.Value, null, null)));

        Assert.False(response.Success);
        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, response.Error?.Code);
        var unchanged = await store.FindByGroupIdAsync(1000, group.Group.GroupId);
        Assert.Equal(group.PrimarySandbox.SandboxId, unchanged!.PrimarySandbox.SandboxId);
        Assert.Equal(LifecycleState.Stopped, unchanged.PrimarySandbox.LifecycleState);
    }

    private static async Task<GroupBundle> CreateGroupAsync(ManagerProtocolServer server)
    {
        var response = await server.DispatchAsync(Envelope(
            ManagerOperations.GetOrCreateGroup,
            new GroupCreatePayload("opencode:broker-start", 1000, "/host", "/workspace/project", "ubuntu-24.04-dev", ResourceAllocation.Default)));
        Assert.True(response.Success, response.Error?.Message);
        return ProtocolJson.FromElement(response.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundle);
    }

    private static ProtocolEnvelope Envelope<T>(string operation, T payload)
    {
        return new ProtocolEnvelope(
            ProtocolVersion.Current,
            TinyId.NewOperationId().Value,
            operation,
            JsonSerializer.SerializeToElement(payload, TinyCosmosJsonContext.Default.Options.GetTypeInfo(typeof(T))));
    }

    private static bool IsFirecrackerUnitLaunch(PlannedCommand command)
    {
        return command.FileName == "/usr/bin/systemd-run" &&
            command.Arguments.Contains("/usr/lib/tiny-cosmos/bin/jailer") &&
            command.Arguments.Contains("/usr/lib/tiny-cosmos/bin/firecracker");
    }

    private sealed class FailingPrerequisiteChecker : IHostPrerequisiteChecker
    {
        public TinyCosmosError? ValidateForVmStart() => new(TinyCosmosErrorCode.DependencyMissing, "missing test dependency");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-manager-broker-" + Guid.NewGuid().ToString("N"));

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
