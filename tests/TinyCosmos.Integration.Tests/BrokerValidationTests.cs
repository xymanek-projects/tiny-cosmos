using TinyCosmos.Core;
using TinyCosmos.Linux;

namespace TinyCosmos.Integration.Tests;

public sealed class BrokerValidationTests
{
    [Fact]
    public void BrokerRejectsCallerSelectedExecutablePaths()
    {
        var config = ValidConfig() with { FirecrackerPath = "/tmp/firecracker" };

        var error = BrokerRequestValidator.ValidateFirecrackerConfig(config);

        Assert.NotNull(error);
        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, error.Code);
    }

    [Fact]
    public void BrokerAcceptsPinnedApprovedPaths()
    {
        Assert.Null(BrokerRequestValidator.ValidateFirecrackerConfig(ValidConfig()));
    }

    [Fact]
    public void StoragePlanUsesFixedToolsAndApprovedStatePaths()
    {
        var plan = BrokerCommandPlanner.PlanStorage(new BrokerStorageRequest(1000, "grp_abc", "sbx_def", 12, 32));

        Assert.StartsWith("/var/lib/tiny-cosmos/groups/grp_abc/sandboxes/sbx_def/", plan.SystemDiskPath, StringComparison.Ordinal);
        Assert.All(plan.Commands, command => Assert.True(command.RequiresRoot));
        Assert.Contains(plan.Commands, command => command.FileName == "/bin/sh" && command.Arguments[1].Contains("tinycosmos-storage", StringComparison.Ordinal) && command.Arguments[1].Contains("/usr/lib/tiny-cosmos/images/rootfs.ext4", StringComparison.Ordinal));
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/sbin/resize2fs" && command.Arguments.Contains(plan.SystemDiskPath));
        Assert.Contains(plan.Commands, command => command.FileName == "/bin/sh" && command.Arguments[1].Contains("tinycosmos-storage", StringComparison.Ordinal) && command.Arguments[1].Contains("mkfs.ext4", StringComparison.Ordinal) && command.Arguments[1].Contains(plan.WorkspaceDiskPath, StringComparison.Ordinal));
    }

    [Fact]
    public void StoragePlanRejectsHostPathTokens()
    {
        Assert.Throws<ArgumentException>(() => BrokerCommandPlanner.PlanStorage(new BrokerStorageRequest(1000, "../escape", "sbx_def", 12, 32)));
    }

    [Fact]
    public void StoragePlanCloneCommandRejectsBaseRootfsOutsideImageRoot()
    {
        var plan = BrokerCommandPlanner.PlanStorage(new BrokerStorageRequest(1000, "grp_abc", "sbx_def", 12, 32, "/tmp/rootfs.ext4"));
        var clone = Assert.Single(plan.Commands, command => command.FileName == "/bin/sh" && command.Arguments[1].Contains("/tmp/rootfs.ext4", StringComparison.Ordinal));

        var error = BrokerCommandExecutor.ValidateApprovedCommand(clone);

        Assert.NotNull(error);
        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, error.Code);
    }

    [Fact]
    public void NetworkPlanUsesGuardedCommandsForSharedResources()
    {
        var plan = BrokerCommandPlanner.PlanNetwork(new BrokerNetworkRequest("grp_net", "sbx_net", "172.31.10.0/24", "172.31.10.1/24", "172.31.10.2/24", "172.31.10.254/24"));

        Assert.StartsWith("tc-", plan.NamespaceName, StringComparison.Ordinal);
        Assert.True(plan.BridgeName.Length <= 15);
        Assert.True(plan.TapName.Length <= 15);
        Assert.True(plan.HostVethName.Length <= 15);
        Assert.True(plan.GuestVethName.Length <= 15);
        Assert.Contains(plan.Commands, command => command.FileName == "/bin/sh" && command.Arguments[1].Contains("tinycosmos-network", StringComparison.Ordinal) && command.Arguments[1].Contains("netns add", StringComparison.Ordinal));
        Assert.Contains(plan.Commands, command => command.FileName == "/bin/sh" && command.Arguments[1].Contains("172.31.10.254/24", StringComparison.Ordinal));
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/sbin/ip" && command.Arguments.SequenceEqual(["link", "set", plan.HostVethName, "up"]));
        Assert.Contains(plan.Commands, command => command.FileName == "/bin/sh" && command.Arguments[1].Contains(" master ", StringComparison.Ordinal));
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/sbin/ip" && command.Arguments.SequenceEqual(["-n", plan.NamespaceName, "link", "set", plan.GuestVethName, "up"]));
        Assert.Contains(plan.Commands, command => command.FileName == "/bin/sh" && command.Arguments[1].Contains("/usr/sbin/nft", StringComparison.Ordinal));
    }

    [Fact]
    public void LaunchPlanRendersFirecrackerApiJsonAndSystemdJailerCommand()
    {
        var request = new BrokerLaunchRequest(
            1000,
            "sbx_launch",
            "ubuntu-24.04-dev",
            "/usr/lib/tiny-cosmos/images/kernel",
            "/usr/lib/tiny-cosmos/images/initrd",
            "/var/lib/tiny-cosmos/groups/g/sandboxes/s/system.ext4",
            "/var/lib/tiny-cosmos/groups/g/sandboxes/s/workspace.ext4",
            "/run/tiny-cosmos/sandboxes/s/api.sock",
            "/run/tiny-cosmos/sandboxes/s/vsock.sock",
            2,
            4096,
            "console=ttyS0 reboot=k panic=1 acpi=off");

        var plan = BrokerCommandPlanner.PlanLaunch(request);

        Assert.Contains("\"boot-source\"", plan.FirecrackerApiJson, StringComparison.Ordinal);
        Assert.Contains("\"initrd_path\":\"/run/tiny-cosmos/initrd\"", plan.FirecrackerApiJson, StringComparison.Ordinal);
        Assert.Contains("\"machine-config\"", plan.FirecrackerApiJson, StringComparison.Ordinal);
        Assert.Contains(plan.Commands, command =>
            command.FileName == "/usr/bin/systemd-run" &&
            command.Arguments.Contains("/usr/sbin/ip") &&
            command.Arguments.Contains("netns") &&
            command.Arguments.Contains("exec") &&
            command.Arguments.Contains("/usr/lib/tiny-cosmos/bin/jailer") &&
            command.Arguments.Contains("--collect"));
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/bin/install" && command.Arguments.Contains("/srv/jailer/firecracker"));
        var launchCommand = Assert.Single(plan.Commands, command => command.FileName == "/usr/bin/systemd-run");
        var jailerId = launchCommand.Arguments.SkipWhile(argument => argument != "--id").Skip(1).First();
        Assert.Equal(12, jailerId.Length);
        Assert.DoesNotContain("_", jailerId, StringComparison.Ordinal);
        Assert.Equal(
            ["/boot-source", "/machine-config", "/drives/rootfs", "/drives/workspace", "/network-interfaces/eth0", "/vsock", "/actions"],
            plan.ApiCalls.Select(call => call.Path));
        Assert.StartsWith("/srv/jailer/firecracker/", plan.ValidationConfig.ApiSocketPath, StringComparison.Ordinal);
        Assert.StartsWith("/srv/jailer/firecracker/", plan.ValidationConfig.VsockSocketPath, StringComparison.Ordinal);
        Assert.Contains("\"uds_path\":\"/run/vsock.sock\"", plan.ApiCalls.Single(call => call.Path == "/vsock").Body, StringComparison.Ordinal);
        Assert.Contains("\"initrd_path\":\"/run/tiny-cosmos/initrd\"", plan.ApiCalls.Single(call => call.Path == "/boot-source").Body, StringComparison.Ordinal);
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/bin/curl" && command.Arguments.Contains("http://localhost/actions"));
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/bin/curl" && command.Arguments.Contains("http://localhost/network-interfaces/eth0"));
        Assert.Equal("/usr/lib/tiny-cosmos/bin/firecracker", plan.ValidationConfig.FirecrackerPath);
    }

    [Fact]
    public void CurlValidationRejectsFirecrackerNetworkInterfaceWithUnapprovedTap()
    {
        var command = new PlannedCommand("/usr/bin/curl", [
            "--fail",
            "--silent",
            "--show-error",
            "--retry-connrefused",
            "--retry", "50",
            "--retry-delay", "0",
            "--connect-timeout", "1",
            "--unix-socket", "/run/tiny-cosmos/sandboxes/s/api.sock",
            "-X", "PUT",
            "http://localhost/network-interfaces/eth0",
            "-H", "Content-Type: application/json",
            "-d", "{\"iface_id\":\"eth0\",\"guest_mac\":\"AA:FC:00:00:00:01\",\"host_dev_name\":\"eth0\"}"
        ], true);

        var error = BrokerCommandExecutor.ValidateApprovedCommand(command);

        Assert.NotNull(error);
        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, error.Code);
    }

    [Fact]
    public void LaunchPlanFirecrackerApiCommandsPassDeepValidation()
    {
        var storage = BrokerCommandPlanner.PlanStorage(new BrokerStorageRequest(1000, "grp_launch", "sbx_launch", 12, 32));
        var launch = BrokerCommandPlanner.PlanLaunch(new BrokerLaunchRequest(
            1000,
            "sbx_launch",
            "ubuntu-24.04-dev",
            "/usr/lib/tiny-cosmos/images/kernel",
            null,
            storage.SystemDiskPath,
            storage.WorkspaceDiskPath,
            "/run/tiny-cosmos/sandboxes/sbx_launch/api.sock",
            "/run/tiny-cosmos/sandboxes/sbx_launch/vsock.sock",
            2,
            4096,
            "console=ttyS0 reboot=k panic=1 acpi=off"));

        Assert.All(launch.Commands, command => Assert.Null(BrokerCommandExecutor.ValidateApprovedCommand(command)));
    }

    [Fact]
    public void CurlValidationRejectsFirecrackerApiBodyWithHostPathEscape()
    {
        var command = new PlannedCommand("/usr/bin/curl", [
            "--fail",
            "--silent",
            "--show-error",
            "--retry-connrefused",
            "--retry", "50",
            "--retry-delay", "0",
            "--connect-timeout", "1",
            "--unix-socket", "/run/tiny-cosmos/sandboxes/s/api.sock",
            "-X", "PUT",
            "http://localhost/drives/rootfs",
            "-H", "Content-Type: application/json",
            "-d", "{\"drive_id\":\"rootfs\",\"path_on_host\":\"/tmp/root.ext4\",\"is_root_device\":true,\"is_read_only\":false}"
        ], true);

        var error = BrokerCommandExecutor.ValidateApprovedCommand(command);

        Assert.NotNull(error);
        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, error.Code);
    }

    [Fact]
    public void CleanupPlanRemovesOnlyTinyCosmosOwnedRoots()
    {
        var plan = BrokerCommandPlanner.PlanCleanup(new BrokerCleanupRequest("grp_cleanup", "sbx_cleanup"));

        Assert.StartsWith("/var/lib/tiny-cosmos/groups/grp_cleanup/sandboxes/sbx_cleanup", plan.SandboxRoot, StringComparison.Ordinal);
        Assert.StartsWith("/run/tiny-cosmos/sandboxes/sbx_cleanup", plan.RuntimeRoot, StringComparison.Ordinal);
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/sbin/ip" && command.Arguments.Contains("delete"));
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/bin/rm" && command.Arguments.Contains("-rf") && command.Arguments.Any(argument => argument.StartsWith("/srv/jailer/firecracker/", StringComparison.Ordinal)));
        Assert.All(plan.Commands, command => Assert.Null(BrokerCommandExecutor.ValidateApprovedCommand(command)));
    }

    [Fact]
    public void StopPlanStopsFirecrackerUnitWithoutDeletingPersistentDisks()
    {
        var plan = BrokerCommandPlanner.PlanStop(new BrokerStopRequest("grp_stop", "sbx_stop"));

        Assert.StartsWith("/var/lib/tiny-cosmos/groups/grp_stop/sandboxes/sbx_stop", plan.SandboxRoot, StringComparison.Ordinal);
        Assert.StartsWith("/run/tiny-cosmos/sandboxes/sbx_stop", plan.RuntimeRoot, StringComparison.Ordinal);
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/bin/systemctl" && command.Arguments[0] == "stop");
        Assert.Contains(plan.Commands, command => command.FileName == "/usr/bin/umount" && command.Arguments.Any(argument => argument.EndsWith("/root.ext4", StringComparison.Ordinal)));
        Assert.DoesNotContain(plan.Commands, command => command.FileName == "/usr/sbin/ip" && command.Arguments.Contains("delete"));
        Assert.DoesNotContain(plan.Commands, command => command.FileName == "/usr/bin/rm" && command.Arguments.Contains(plan.SandboxRoot));
        Assert.All(plan.Commands, command => Assert.Null(BrokerCommandExecutor.ValidateApprovedCommand(command)));
    }

    [Fact]
    public void CleanupValidationRejectsRmOutsideTinyCosmosRoots()
    {
        var error = BrokerCommandExecutor.ValidateApprovedCommand(new PlannedCommand("/usr/bin/rm", ["-rf", "/"], true));

        Assert.NotNull(error);
        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, error.Code);
    }

    [Fact]
    public void BrokerProtocolPlansCleanupFromTypedPayload()
    {
        var plan = BrokerCommandPlanner.PlanCleanup(new BrokerCleanupRequest("grp_proto", "sbx_proto"));

        Assert.Contains("grp_proto", plan.SandboxRoot, StringComparison.Ordinal);
        Assert.Contains("sbx_proto", plan.RuntimeRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationLedgerReturnsSameRecordForSameRequestAndRejectsReuse()
    {
        using var temp = new TempDir();
        var ledger = Path.Combine(temp.Path, "ledger.tsv");

        var first = BrokerCommandPlanner.RecordIdempotentResult(ledger, "op_abc", 1000, "Start", "request", "result");
        var second = BrokerCommandPlanner.RecordIdempotentResult(ledger, "op_abc", 1000, "Start", "request", "different-result-ignored");

        Assert.Equal(first.RequestDigest, second.RequestDigest);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
        var ex = Assert.Throws<TinyCosmosException>(() => BrokerCommandPlanner.RecordIdempotentResult(ledger, "op_abc", 1000, "Stop", "request", "result"));
        Assert.Equal(TinyCosmosErrorCode.LifecycleConflict, ex.Error.Code);
    }

    [Fact]
    public async Task CommandExecutorDryRunRejectsUnapprovedExecutables()
    {
        var ex = await Assert.ThrowsAsync<TinyCosmosException>(() => BrokerCommandExecutor.ExecuteAsync(
            [new PlannedCommand("/bin/sh", ["-c", "echo nope"], true)],
            dryRun: true));

        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, ex.Error.Code);
    }

    [Fact]
    public async Task CommandExecutorRejectsApprovedExecutableWithUnapprovedArguments()
    {
        var ex = await Assert.ThrowsAsync<TinyCosmosException>(() => BrokerCommandExecutor.ExecuteAsync(
            [new PlannedCommand("/usr/bin/truncate", ["-s", "1G", "/tmp/escape.ext4"], true)],
            dryRun: true));

        Assert.Equal(TinyCosmosErrorCode.PlatformRejected, ex.Error.Code);
    }

    [Fact]
    public void PlannedBrokerCommandsPassDeepExecutionValidation()
    {
        var storage = BrokerCommandPlanner.PlanStorage(new BrokerStorageRequest(1000, "grp_valid", "sbx_valid", 12, 32));
        var network = BrokerCommandPlanner.PlanNetwork(new BrokerNetworkRequest("grp_valid", "sbx_valid", "172.31.10.0/24", "172.31.10.1/24", "172.31.10.2/24", "172.31.10.254/24"));
        var launch = BrokerCommandPlanner.PlanLaunch(new BrokerLaunchRequest(
            1000,
            "sbx_valid",
            "ubuntu-24.04-dev",
            "/usr/lib/tiny-cosmos/images/kernel",
            null,
            storage.SystemDiskPath,
            storage.WorkspaceDiskPath,
            "/run/tiny-cosmos/sandboxes/sbx_valid/api.sock",
            "/run/tiny-cosmos/sandboxes/sbx_valid/vsock.sock",
            2,
            4096,
            "console=ttyS0 reboot=k panic=1 acpi=off"));

        foreach (var command in storage.Commands.Concat(network.Commands).Concat(launch.Commands))
        {
            Assert.Null(BrokerCommandExecutor.ValidateApprovedCommand(command));
        }
    }

    [Fact]
    public async Task CommandExecutorDryRunReturnsApprovedCommandTextWithoutExecuting()
    {
        var results = await BrokerCommandExecutor.ExecuteAsync(
            [new PlannedCommand("/usr/bin/truncate", ["-s", "1G", "/var/lib/tiny-cosmos/groups/g/disk.ext4"], true)],
            dryRun: true);

        var result = Assert.Single(results);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/usr/bin/truncate", result.Stdout, StringComparison.Ordinal);
    }

    private static FirecrackerVmConfig ValidConfig() => new(
        "/usr/lib/tiny-cosmos/bin/firecracker",
        "/usr/lib/tiny-cosmos/bin/jailer",
        "/usr/lib/tiny-cosmos/images/kernel",
        null,
        "/var/lib/tiny-cosmos/groups/g/system.ext4",
        "/var/lib/tiny-cosmos/groups/g/workspace.ext4",
        "/srv/jailer/firecracker/0123456789ab/root/run/api.sock",
        "/srv/jailer/firecracker/0123456789ab/root/run/vsock.sock",
        2,
        4096);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-broker-" + Guid.NewGuid().ToString("N"));

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
