using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyCosmos.Core;

namespace TinyCosmos.Linux;

public sealed record PlannedCommand(string FileName, IReadOnlyList<string> Arguments, bool RequiresRoot)
{
    public override string ToString() => FileName + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string value) =>
        value.IndexOfAny([' ', '\t', '\n', '\'', '"', '$', ';', '&', '|', '<', '>', '(', ')']) < 0
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

public sealed record BrokerStorageRequest(
    int OwnerUid,
    string GroupId,
    string SandboxId,
    long SystemDiskGiB,
    long WorkspaceDiskGiB,
    string BaseRootfsPath = "/usr/lib/tiny-cosmos/images/rootfs.ext4");

public sealed record BrokerStoragePlan(
    string SystemDiskPath,
    string WorkspaceDiskPath,
    IReadOnlyList<PlannedCommand> Commands);

public sealed record BrokerNetworkRequest(
    string GroupId,
    string SandboxId,
    string Cidr,
    string GatewayAddress,
    string PrimaryAddress,
    string HostVethAddress);

public sealed record BrokerNetworkPlan(
    string NamespaceName,
    string BridgeName,
    string TapName,
    string HostVethName,
    string GuestVethName,
    IReadOnlyList<PlannedCommand> Commands);

public sealed record BrokerLaunchRequest(
    int OwnerUid,
    string SandboxId,
    string ImageId,
    string KernelImagePath,
    string? InitrdImagePath,
    string RootDrivePath,
    string WorkspaceDrivePath,
    string ApiSocketPath,
    string VsockSocketPath,
    int Vcpu,
    long MemoryMiB,
    string BootArgs,
    string NetworkNamespaceName = "tc-000000000000",
    string TapDeviceName = "tap-000000000");

public sealed record BrokerLaunchPlan(
    FirecrackerVmConfig ValidationConfig,
    string FirecrackerApiJson,
    IReadOnlyList<FirecrackerApiCall> ApiCalls,
    IReadOnlyList<PlannedCommand> Commands);

public sealed record FirecrackerApiCall(string Method, string Path, string Body);

public sealed record BrokerCleanupRequest(string GroupId, string SandboxId);

public sealed record BrokerCleanupPlan(
    string SandboxRoot,
    string RuntimeRoot,
    IReadOnlyList<PlannedCommand> Commands);

public sealed record BrokerStopRequest(string GroupId, string SandboxId);

public sealed record BrokerStopPlan(
    string SandboxRoot,
    string RuntimeRoot,
    IReadOnlyList<PlannedCommand> Commands);

public sealed record BrokerOperationRecord(
    string OperationId,
    int OwnerUid,
    string OperationName,
    string RequestDigest,
    string ResultDigest,
    DateTimeOffset RecordedAt);

public sealed record BrokerCommandResult(string FileName, IReadOnlyList<string> Arguments, int ExitCode, string Stdout, string Stderr, bool TimedOut);

public static class BrokerOperationNames
{
    public const string PlanStorage = "PlanStorage";
    public const string PlanNetwork = "PlanNetwork";
    public const string PlanLaunch = "PlanLaunch";
    public const string PlanStop = "PlanStop";
    public const string PlanCleanup = "PlanCleanup";
    public const string ExecutePlan = "ExecutePlan";
}

public static class BrokerPaths
{
    public const string InstallBinRoot = "/usr/lib/tiny-cosmos/bin";
    public const string ImageRoot = "/usr/lib/tiny-cosmos/images";
    public const string StateRoot = "/var/lib/tiny-cosmos";
    public const string RunRoot = "/run/tiny-cosmos";
    public const string JailerRoot = "/srv/jailer";

    public static string GroupRoot(string groupId) => Path.Combine(StateRoot, "groups", SanitizeResourceToken(groupId));

    public static string SandboxRoot(string groupId, string sandboxId) => Path.Combine(GroupRoot(groupId), "sandboxes", SanitizeResourceToken(sandboxId));

    public static string RuntimeRoot(string sandboxId) => Path.Combine(RunRoot, "sandboxes", SanitizeResourceToken(sandboxId));

    public static string SanitizeResourceToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException("Resource token must be non-empty and at most 128 characters.", nameof(value));
        }

        foreach (var c in value)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '_' or '-'))
            {
                throw new ArgumentException("Resource token contains unsupported characters.", nameof(value));
            }
        }

        return value;
    }
}

public static class BrokerCommandPlanner
{
    private const string FirecrackerApiSocketInJailer = "/run/api.sock";
    private const string FirecrackerVsockSocketInJailer = "/run/vsock.sock";
    private const string KernelPathInJailer = "/run/tiny-cosmos/kernel";
    private const string InitrdPathInJailer = "/run/tiny-cosmos/initrd";
    private const string RootDrivePathInJailer = "/run/tiny-cosmos/root.ext4";
    private const string WorkspaceDrivePathInJailer = "/run/tiny-cosmos/workspace.ext4";

    public static BrokerStoragePlan PlanStorage(BrokerStorageRequest request)
    {
        ValidateDiskSize(request.SystemDiskGiB);
        ValidateDiskSize(request.WorkspaceDiskGiB);

        var sandboxRoot = BrokerPaths.SandboxRoot(request.GroupId, request.SandboxId);
        var systemDisk = Path.Combine(sandboxRoot, "system.ext4");
        var workspaceDisk = Path.Combine(sandboxRoot, "workspace.ext4");
        return new BrokerStoragePlan(
            systemDisk,
            workspaceDisk,
            [
                new PlannedCommand("/usr/bin/install", ["-d", "-m", "0700", "-o", "root", "-g", "root", sandboxRoot], true),
                StorageShell(
                    $"/usr/bin/test -e {ShellWord(systemDisk)} || " +
                    $"(/usr/bin/cp --reflink=auto --sparse=always --preserve=mode {ShellWord(request.BaseRootfsPath)} {ShellWord(systemDisk)} && " +
                    $"/usr/bin/truncate -s {request.SystemDiskGiB}G {ShellWord(systemDisk)})"),
                new PlannedCommand("/usr/sbin/e2fsck", ["-fy", systemDisk], true),
                new PlannedCommand("/usr/sbin/resize2fs", [systemDisk], true),
                StorageShell(
                    $"/usr/bin/test -e {ShellWord(workspaceDisk)} || " +
                    $"(/usr/bin/truncate -s {request.WorkspaceDiskGiB}G {ShellWord(workspaceDisk)} && " +
                    $"/usr/sbin/mkfs.ext4 -F -q -L tinycosmos-workspace {ShellWord(workspaceDisk)})")
            ]);
    }

    public static BrokerNetworkPlan PlanNetwork(BrokerNetworkRequest request)
    {
        var group = BrokerPaths.SanitizeResourceToken(request.GroupId);
        var sandbox = BrokerPaths.SanitizeResourceToken(request.SandboxId);
        var ns = "tc-" + ShortToken(group);
        var groupInterfaceToken = ShortInterfaceToken(group);
        var sandboxInterfaceToken = ShortInterfaceToken(sandbox);
        var bridge = "br-" + groupInterfaceToken;
        var tap = "tap-" + sandboxInterfaceToken;
        var hostVeth = "veth-" + groupInterfaceToken;
        var guestVeth = "vpeer-" + groupInterfaceToken;

        return new BrokerNetworkPlan(
            ns,
            bridge,
            tap,
            hostVeth,
            guestVeth,
            [
                NetworkShell($"/usr/sbin/ip netns list | /usr/bin/grep -q '^{ShellWord(ns)}' || /usr/sbin/ip netns add {ShellWord(ns)}"),
                NetworkShell($"/usr/sbin/ip link show {ShellWord(hostVeth)} >/dev/null 2>&1 || /usr/sbin/ip link add {ShellWord(hostVeth)} type veth peer name {ShellWord(guestVeth)}"),
                NetworkShell($"/usr/sbin/ip -n {ShellWord(ns)} link show {ShellWord(guestVeth)} >/dev/null 2>&1 || /usr/sbin/ip link set {ShellWord(guestVeth)} netns {ShellWord(ns)}"),
                NetworkShell($"/usr/sbin/ip addr show dev {ShellWord(hostVeth)} | /usr/bin/grep -q {ShellWord(request.HostVethAddress.Split('/')[0])} || /usr/sbin/ip addr add {ShellWord(request.HostVethAddress)} dev {ShellWord(hostVeth)}"),
                new PlannedCommand("/usr/sbin/ip", ["link", "set", hostVeth, "up"], true),
                new PlannedCommand("/usr/sbin/ip", ["-n", ns, "link", "set", "lo", "up"], true),
                NetworkShell($"/usr/sbin/ip -n {ShellWord(ns)} link show {ShellWord(bridge)} >/dev/null 2>&1 || /usr/sbin/ip -n {ShellWord(ns)} link add {ShellWord(bridge)} type bridge"),
                NetworkShell($"/usr/sbin/ip -n {ShellWord(ns)} addr show dev {ShellWord(bridge)} | /usr/bin/grep -q {ShellWord(request.GatewayAddress.Split('/')[0])} || /usr/sbin/ip -n {ShellWord(ns)} addr add {ShellWord(request.GatewayAddress)} dev {ShellWord(bridge)}"),
                new PlannedCommand("/usr/sbin/ip", ["-n", ns, "link", "set", bridge, "up"], true),
                NetworkShell($"/usr/sbin/ip -n {ShellWord(ns)} link set {ShellWord(guestVeth)} master {ShellWord(bridge)} || true"),
                new PlannedCommand("/usr/sbin/ip", ["-n", ns, "link", "set", guestVeth, "up"], true),
                NetworkShell($"/usr/sbin/ip -n {ShellWord(ns)} link show {ShellWord(tap)} >/dev/null 2>&1 || (/usr/sbin/ip tuntap add {ShellWord(tap)} mode tap && /usr/sbin/ip link set {ShellWord(tap)} netns {ShellWord(ns)})"),
                new PlannedCommand("/usr/sbin/ip", ["-n", ns, "link", "set", tap, "master", bridge], true),
                new PlannedCommand("/usr/sbin/ip", ["-n", ns, "link", "set", tap, "up"], true),
                NetworkShell($"/usr/sbin/nft list table inet {ShellWord("tiny_cosmos_" + ShortToken(group))} >/dev/null 2>&1 || /usr/sbin/nft add table inet {ShellWord("tiny_cosmos_" + ShortToken(group))}")
            ]);
    }

    private static PlannedCommand NetworkShell(string script) =>
        new("/bin/sh", ["-c", ": tinycosmos-network; " + script], true);

    private static PlannedCommand StorageShell(string script) =>
        new("/bin/sh", ["-c", ": tinycosmos-storage; " + script], true);

    private static string ShellWord(string value)
    {
        if (value.Length == 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '/' or '_' or '-')))
        {
            throw new ArgumentException("Network shell token contains unsupported characters.", nameof(value));
        }

        return value;
    }

    public static BrokerLaunchPlan PlanLaunch(BrokerLaunchRequest request)
    {
        var hostApiSocketPath = HostJailerRunPath(request.SandboxId, "api.sock");
        var hostVsockSocketPath = HostJailerRunPath(request.SandboxId, "vsock.sock");
        var hostJailerMountDir = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos");
        var hostKernelPath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "kernel");
        var hostInitrdPath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "initrd");
        var hostRootDrivePath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "root.ext4");
        var hostWorkspaceDrivePath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "workspace.ext4");
        var validation = new FirecrackerVmConfig(
            Path.Combine(BrokerPaths.InstallBinRoot, "firecracker"),
            Path.Combine(BrokerPaths.InstallBinRoot, "jailer"),
            request.KernelImagePath,
            request.InitrdImagePath,
            request.RootDrivePath,
            request.WorkspaceDrivePath,
            hostApiSocketPath,
            hostVsockSocketPath,
            request.Vcpu,
            request.MemoryMiB);
        var error = BrokerRequestValidator.ValidateFirecrackerConfig(validation);
        if (error is not null)
        {
            throw new TinyCosmosException(error);
        }

        var apiJson = RenderFirecrackerApiJson(request);
        var apiCalls = RenderFirecrackerApiCalls(request);
        var commands = new List<PlannedCommand>
        {
            new("/usr/bin/install", ["-d", "-m", "0710", "-o", "root", "-g", "root", Path.GetDirectoryName(request.ApiSocketPath)!], true),
            new("/usr/bin/install", ["-d", "-m", "0710", "-o", "root", "-g", "root", Path.Combine(BrokerPaths.JailerRoot, "firecracker")], true),
            new("/usr/bin/systemd-run", [
                "--unit", FirecrackerUnitName(request.SandboxId),
                "--collect",
                "--property", "Type=simple",
                "--property", "KillMode=control-group",
                "/usr/sbin/ip",
                "netns",
                "exec",
                request.NetworkNamespaceName,
                validation.JailerPath,
                "--id", JailerId(request.SandboxId),
                "--exec-file", validation.FirecrackerPath,
                "--uid", "0",
                "--gid", "0",
                "--",
                "--api-sock", FirecrackerApiSocketInJailer
            ], true),
            new("/usr/bin/install", ["-d", "-m", "0700", "-o", "root", "-g", "root", hostJailerMountDir], true),
            new("/usr/bin/touch", [hostKernelPath, hostRootDrivePath, hostWorkspaceDrivePath], true),
            new("/usr/bin/mount", ["--bind", request.KernelImagePath, hostKernelPath], true),
            new("/usr/bin/mount", ["--bind", request.RootDrivePath, hostRootDrivePath], true),
            new("/usr/bin/mount", ["--bind", request.WorkspaceDrivePath, hostWorkspaceDrivePath], true),
            new("/usr/bin/chmod", ["0711", Path.Combine(BrokerPaths.JailerRoot, "firecracker"), Path.Combine(BrokerPaths.JailerRoot, "firecracker", JailerId(request.SandboxId)), Path.Combine(BrokerPaths.JailerRoot, "firecracker", JailerId(request.SandboxId), "root"), Path.Combine(BrokerPaths.JailerRoot, "firecracker", JailerId(request.SandboxId), "root", "run")], true)
        };
        if (!string.IsNullOrWhiteSpace(request.InitrdImagePath))
        {
            commands.Insert(4, new PlannedCommand("/usr/bin/touch", [hostInitrdPath], true));
            commands.Insert(7, new PlannedCommand("/usr/bin/mount", ["--bind", request.InitrdImagePath, hostInitrdPath], true));
        }

        foreach (var call in apiCalls)
        {
            commands.Add(FirecrackerApiCommand(hostApiSocketPath, call));
            if (call.Path == "/vsock")
            {
                commands.Add(new PlannedCommand("/usr/bin/chown", [$"{request.OwnerUid}:{request.OwnerUid}", hostVsockSocketPath], true));
                commands.Add(new PlannedCommand("/usr/bin/chmod", ["0600", hostVsockSocketPath], true));
            }
        }

        return new BrokerLaunchPlan(
            validation,
            apiJson,
            apiCalls,
            commands);
    }

    public static BrokerCleanupPlan PlanCleanup(BrokerCleanupRequest request)
    {
        var network = PlanNetwork(new BrokerNetworkRequest(
            request.GroupId,
            request.SandboxId,
            "172.31.0.0/24",
            "172.31.0.1/24",
            "172.31.0.2/24",
            "172.31.0.254/24"));
        var sandboxRoot = BrokerPaths.SandboxRoot(request.GroupId, request.SandboxId);
        var runtimeRoot = BrokerPaths.RuntimeRoot(request.SandboxId);
        var jailerRoot = Path.Combine(BrokerPaths.JailerRoot, "firecracker", JailerId(request.SandboxId));
        var hostKernelPath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "kernel");
        var hostInitrdPath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "initrd");
        var hostRootDrivePath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "root.ext4");
        var hostWorkspaceDrivePath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "workspace.ext4");
        return new BrokerCleanupPlan(
            sandboxRoot,
            runtimeRoot,
            [
                new PlannedCommand("/usr/bin/systemctl", ["stop", FirecrackerUnitName(request.SandboxId)], true),
                new PlannedCommand("/usr/bin/umount", ["-l", hostKernelPath], true),
                new PlannedCommand("/usr/bin/umount", ["-l", hostInitrdPath], true),
                new PlannedCommand("/usr/bin/umount", ["-l", hostRootDrivePath], true),
                new PlannedCommand("/usr/bin/umount", ["-l", hostWorkspaceDrivePath], true),
                new PlannedCommand("/usr/sbin/ip", ["netns", "delete", network.NamespaceName], true),
                new PlannedCommand("/usr/bin/rm", ["-rf", sandboxRoot, runtimeRoot, jailerRoot], true)
            ]);
    }

    public static BrokerStopPlan PlanStop(BrokerStopRequest request)
    {
        var sandboxRoot = BrokerPaths.SandboxRoot(request.GroupId, request.SandboxId);
        var runtimeRoot = BrokerPaths.RuntimeRoot(request.SandboxId);
        var jailerRoot = Path.Combine(BrokerPaths.JailerRoot, "firecracker", JailerId(request.SandboxId));
        var hostKernelPath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "kernel");
        var hostInitrdPath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "initrd");
        var hostRootDrivePath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "root.ext4");
        var hostWorkspaceDrivePath = HostJailerRootPath(request.SandboxId, "run", "tiny-cosmos", "workspace.ext4");
        return new BrokerStopPlan(
            sandboxRoot,
            runtimeRoot,
            [
                new PlannedCommand("/usr/bin/systemctl", ["stop", FirecrackerUnitName(request.SandboxId)], true),
                new PlannedCommand("/usr/bin/umount", ["-l", hostKernelPath], true),
                new PlannedCommand("/usr/bin/umount", ["-l", hostInitrdPath], true),
                new PlannedCommand("/usr/bin/umount", ["-l", hostRootDrivePath], true),
                new PlannedCommand("/usr/bin/umount", ["-l", hostWorkspaceDrivePath], true),
                new PlannedCommand("/usr/bin/rm", ["-rf", runtimeRoot, jailerRoot], true)
            ]);
    }

    public static BrokerOperationRecord RecordIdempotentResult(
        string ledgerPath,
        string operationId,
        int ownerUid,
        string operationName,
        string requestText,
        string resultText)
    {
        _ = BrokerPaths.SanitizeResourceToken(operationId);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ledgerPath))!);
        var requestDigest = Sha256(requestText);
        var resultDigest = Sha256(resultText);

        if (File.Exists(ledgerPath))
        {
            foreach (var line in File.ReadLines(ledgerPath))
            {
                var parts = line.Split('\t');
                if (parts.Length == 6 && parts[0] == operationId)
                {
                    if (parts[1] != ownerUid.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                        parts[2] != operationName ||
                        parts[3] != requestDigest)
                    {
                        throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.LifecycleConflict, "Operation id was reused for a different request."));
                    }

                    return new BrokerOperationRecord(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2], parts[3], parts[4], DateTimeOffset.Parse(parts[5], null, System.Globalization.DateTimeStyles.RoundtripKind));
                }
            }
        }

        var record = new BrokerOperationRecord(operationId, ownerUid, operationName, requestDigest, resultDigest, DateTimeOffset.UtcNow);
        File.AppendAllText(ledgerPath, string.Join('\t', record.OperationId, record.OwnerUid, record.OperationName, record.RequestDigest, record.ResultDigest, record.RecordedAt.ToString("O")) + "\n");
        return record;
    }

    public static string HostJailerRunPath(string sandboxId, string fileName)
    {
        if (fileName is not ("api.sock" or "vsock.sock"))
        {
            throw new ArgumentException("Unsupported jailer runtime file.", nameof(fileName));
        }

        return Path.Combine(BrokerPaths.JailerRoot, "firecracker", JailerId(sandboxId), "root", "run", fileName);
    }

    private static string HostJailerRootPath(string sandboxId, params string[] relativeParts)
    {
        return Path.Combine([BrokerPaths.JailerRoot, "firecracker", JailerId(sandboxId), "root", .. relativeParts]);
    }

    private static string RenderFirecrackerApiJson(BrokerLaunchRequest request)
    {
        var rootDrive = new JsonObject
        {
            ["drive_id"] = "rootfs",
            ["path_on_host"] = RootDrivePathInJailer,
            ["is_root_device"] = true,
            ["is_read_only"] = false
        };
        var workspaceDrive = new JsonObject
        {
            ["drive_id"] = "workspace",
            ["path_on_host"] = WorkspaceDrivePathInJailer,
            ["is_root_device"] = false,
            ["is_read_only"] = false
        };
        var bootSource = new JsonObject
        {
            ["kernel_image_path"] = KernelPathInJailer,
            ["boot_args"] = request.BootArgs
        };
        if (!string.IsNullOrWhiteSpace(request.InitrdImagePath))
        {
            bootSource["initrd_path"] = InitrdPathInJailer;
        }

        var document = new JsonObject
        {
            ["boot-source"] = bootSource,
            ["machine-config"] = new JsonObject
            {
                ["vcpu_count"] = request.Vcpu,
                ["mem_size_mib"] = request.MemoryMiB,
                ["smt"] = false,
                ["track_dirty_pages"] = false
            },
            ["drives"] = new JsonArray(rootDrive, workspaceDrive),
            ["network-interfaces"] = new JsonArray(new JsonObject
            {
                ["iface_id"] = "eth0",
                ["guest_mac"] = "AA:FC:00:00:00:01",
                ["host_dev_name"] = request.TapDeviceName
            }),
            ["vsock"] = new JsonObject
            {
                ["vsock_id"] = "tc-vsock",
                ["guest_cid"] = 3,
                ["uds_path"] = FirecrackerVsockSocketInJailer
            }
        };
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static IReadOnlyList<FirecrackerApiCall> RenderFirecrackerApiCalls(BrokerLaunchRequest request)
    {
        var bootSource = new JsonObject
        {
            ["kernel_image_path"] = KernelPathInJailer,
            ["boot_args"] = request.BootArgs
        };
        if (!string.IsNullOrWhiteSpace(request.InitrdImagePath))
        {
            bootSource["initrd_path"] = InitrdPathInJailer;
        }

        var machineConfig = new JsonObject
        {
            ["vcpu_count"] = request.Vcpu,
            ["mem_size_mib"] = request.MemoryMiB,
            ["smt"] = false,
            ["track_dirty_pages"] = false
        };
        var rootDrive = new JsonObject
        {
            ["drive_id"] = "rootfs",
            ["path_on_host"] = RootDrivePathInJailer,
            ["is_root_device"] = true,
            ["is_read_only"] = false
        };
        var workspaceDrive = new JsonObject
        {
            ["drive_id"] = "workspace",
            ["path_on_host"] = WorkspaceDrivePathInJailer,
            ["is_root_device"] = false,
            ["is_read_only"] = false
        };
        var vsock = new JsonObject
        {
            ["vsock_id"] = "tc-vsock",
            ["guest_cid"] = 3,
            ["uds_path"] = FirecrackerVsockSocketInJailer
        };
        var network = new JsonObject
        {
            ["iface_id"] = "eth0",
            ["guest_mac"] = "AA:FC:00:00:00:01",
            ["host_dev_name"] = request.TapDeviceName
        };
        var start = new JsonObject
        {
            ["action_type"] = "InstanceStart"
        };

        return
        [
            new FirecrackerApiCall("PUT", "/boot-source", bootSource.ToJsonString()),
            new FirecrackerApiCall("PUT", "/machine-config", machineConfig.ToJsonString()),
            new FirecrackerApiCall("PUT", "/drives/rootfs", rootDrive.ToJsonString()),
            new FirecrackerApiCall("PUT", "/drives/workspace", workspaceDrive.ToJsonString()),
            new FirecrackerApiCall("PUT", "/network-interfaces/eth0", network.ToJsonString()),
            new FirecrackerApiCall("PUT", "/vsock", vsock.ToJsonString()),
            new FirecrackerApiCall("PUT", "/actions", start.ToJsonString())
        ];
    }

    private static PlannedCommand FirecrackerApiCommand(string apiSocketPath, FirecrackerApiCall call)
    {
        return new PlannedCommand("/usr/bin/curl", [
            "--fail",
            "--silent",
            "--show-error",
            "--retry-connrefused",
            "--retry", "50",
            "--retry-delay", "0",
            "--connect-timeout", "1",
            "--unix-socket", apiSocketPath,
            "-X", call.Method,
            "http://localhost" + call.Path,
            "-H", "Content-Type: application/json",
            "-d", call.Body
        ], true);
    }

    private static void ValidateDiskSize(long sizeGiB)
    {
        if (sizeGiB is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeGiB), "Disk size must be between 1 and 1024 GiB.");
        }
    }

    private static string ShortToken(string token) => Sha256(token)[..12].ToLowerInvariant();

    private static string ShortInterfaceToken(string token) => Sha256(token)[..9].ToLowerInvariant();

    private static string FirecrackerUnitName(string sandboxId) => "tinycosmos-sandbox-" + ShortToken(BrokerPaths.SanitizeResourceToken(sandboxId));

    private static string JailerId(string sandboxId) => ShortToken(BrokerPaths.SanitizeResourceToken(sandboxId));

    private static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }
}

public static class BrokerCommandExecutor
{
    private const string FirecrackerApiSocketInJailer = "/run/api.sock";
    private const string FirecrackerVsockSocketInJailer = "/run/vsock.sock";
    private const string KernelPathInJailer = "/run/tiny-cosmos/kernel";
    private const string InitrdPathInJailer = "/run/tiny-cosmos/initrd";
    private const string RootDrivePathInJailer = "/run/tiny-cosmos/root.ext4";
    private const string WorkspaceDrivePathInJailer = "/run/tiny-cosmos/workspace.ext4";

    public static async Task<IReadOnlyList<BrokerCommandResult>> ExecuteAsync(
        IEnumerable<PlannedCommand> commands,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BrokerCommandResult>();
        foreach (var command in commands)
        {
            var validationError = ValidateApprovedCommand(command);
            if (validationError is not null)
            {
                throw new TinyCosmosException(validationError);
            }

            if (dryRun)
            {
                results.Add(new BrokerCommandResult(command.FileName, command.Arguments, 0, command.ToString(), string.Empty, false));
                continue;
            }

            var result = await ProcessRunner.RunAsync(command.FileName, command.Arguments, timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken).ConfigureAwait(false);
            var exitCode = command.FileName == "/usr/sbin/e2fsck" && result.ExitCode == 1 ? 0 : result.ExitCode;
            results.Add(new BrokerCommandResult(command.FileName, command.Arguments, exitCode, result.Stdout, result.Stderr, result.TimedOut));
            if (exitCode != 0)
            {
                break;
            }
        }

        return results;
    }

    public static TinyCosmosError? ValidateApprovedCommand(PlannedCommand command)
    {
        if (command.Arguments.Any(argument => argument.Contains('\0') || argument.Length > 16 * 1024))
        {
            return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "Planned command contains an invalid argument.");
        }

        return command.FileName switch
        {
            "/usr/bin/install" => ValidateInstall(command.Arguments),
            "/usr/bin/touch" => ValidateTouch(command.Arguments),
            "/usr/bin/mount" => ValidateMount(command.Arguments),
            "/usr/bin/umount" => ValidateUmount(command.Arguments),
            "/usr/bin/chmod" => ValidateChmod(command.Arguments),
            "/usr/bin/chown" => ValidateChown(command.Arguments),
            "/usr/bin/cp" => ValidateCopy(command.Arguments),
            "/usr/bin/truncate" => ValidateTruncate(command.Arguments),
            "/usr/sbin/mkfs.ext4" => ValidateMkfs(command.Arguments),
            "/usr/sbin/e2fsck" => ValidateE2fsck(command.Arguments),
            "/usr/sbin/resize2fs" => ValidateResize2fs(command.Arguments),
            "/usr/sbin/ip" => ValidateIp(command.Arguments),
            "/usr/sbin/nft" => ValidateNft(command.Arguments),
            "/bin/sh" => ValidateBrokerShell(command.Arguments),
            "/usr/bin/rm" => ValidateRm(command.Arguments),
            "/usr/bin/curl" => ValidateCurl(command.Arguments),
            "/usr/bin/systemd-run" => ValidateSystemdRun(command.Arguments),
            "/usr/bin/systemctl" => ValidateSystemctl(command.Arguments),
            "/usr/lib/tiny-cosmos/bin/jailer" => ValidateJailer(command.Arguments),
            _ => new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "Planned command executable is not approved.")
        };
    }

    private static TinyCosmosError? ValidateInstall(IReadOnlyList<string> args)
    {
        if (args is ["-d", "-m", "0700" or "0710", "-o", "root", "-g", "root", var path] &&
            IsUnderBrokerRoot(path, BrokerPaths.StateRoot, BrokerPaths.RunRoot, BrokerPaths.JailerRoot))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "install command does not match an approved broker directory operation.");
    }

    private static TinyCosmosError? ValidateTouch(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args.All(path => IsUnderBrokerRoot(path, BrokerPaths.JailerRoot)))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "touch command does not match an approved jailer file preparation operation.");
    }

    private static TinyCosmosError? ValidateMount(IReadOnlyList<string> args)
    {
        if (args is ["--bind", var source, var target] &&
            IsApprovedBindMountSource(source) &&
            IsUnderBrokerRoot(target, BrokerPaths.JailerRoot))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "mount command does not match an approved jailer bind operation.");
    }

    private static TinyCosmosError? ValidateUmount(IReadOnlyList<string> args)
    {
        if (args is ["-l", var target] && IsUnderBrokerRoot(target, BrokerPaths.JailerRoot))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "umount command does not match an approved jailer bind cleanup operation.");
    }

    private static TinyCosmosError? ValidateChmod(IReadOnlyList<string> args)
    {
        if (args.Count >= 2 &&
            args[0] is "0711" or "0600" &&
            args.Skip(1).All(path => IsUnderBrokerRoot(path, BrokerPaths.JailerRoot)))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "chmod command does not match an approved jailer permission operation.");
    }

    private static TinyCosmosError? ValidateChown(IReadOnlyList<string> args)
    {
        if (args is [var owner, var path] &&
            IsNumericOwner(owner) &&
            IsUnderBrokerRoot(path, BrokerPaths.JailerRoot) &&
            path.EndsWith("vsock.sock", StringComparison.Ordinal))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "chown command does not match an approved jailer socket handoff operation.");
    }

    private static TinyCosmosError? ValidateCopy(IReadOnlyList<string> args)
    {
        if (args is ["--reflink=auto", "--sparse=always", "--preserve=mode", var source, var destination] &&
            IsUnderBrokerRoot(source, BrokerPaths.ImageRoot) &&
            source.EndsWith(".ext4", StringComparison.Ordinal) &&
            IsUnderBrokerRoot(destination, BrokerPaths.StateRoot) &&
            destination.EndsWith(".ext4", StringComparison.Ordinal))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "cp command does not match an approved broker image clone operation.");
    }

    private static TinyCosmosError? ValidateTruncate(IReadOnlyList<string> args)
    {
        if (args is ["-s", var size, var path] &&
            IsApprovedDiskSize(size) &&
            IsUnderBrokerRoot(path, BrokerPaths.StateRoot) &&
            path.EndsWith(".ext4", StringComparison.Ordinal))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "truncate command does not match an approved broker disk operation.");
    }

    private static TinyCosmosError? ValidateMkfs(IReadOnlyList<string> args)
    {
        if (args is ["-F", "-q", "-L", "tinycosmos-workspace", var path] &&
            IsUnderBrokerRoot(path, BrokerPaths.StateRoot) &&
            path.EndsWith(".ext4", StringComparison.Ordinal))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "mkfs.ext4 command does not match an approved broker disk operation.");
    }

    private static TinyCosmosError? ValidateResize2fs(IReadOnlyList<string> args)
    {
        if (args is [var path] &&
            IsUnderBrokerRoot(path, BrokerPaths.StateRoot) &&
            path.EndsWith(".ext4", StringComparison.Ordinal))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "resize2fs command does not match an approved broker disk resize operation.");
    }

    private static TinyCosmosError? ValidateE2fsck(IReadOnlyList<string> args)
    {
        if (args is ["-fy", var path] &&
            IsUnderBrokerRoot(path, BrokerPaths.StateRoot) &&
            path.EndsWith(".ext4", StringComparison.Ordinal))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "e2fsck command does not match an approved broker disk check operation.");
    }

    private static TinyCosmosError? ValidateIp(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(ContainsShellMetacharacter))
        {
            return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "ip command contains invalid arguments.");
        }

        return null;
    }

    private static TinyCosmosError? ValidateNft(IReadOnlyList<string> args)
    {
        if (args is ["add", "table", "inet", var table] &&
            table.StartsWith("tiny_cosmos_", StringComparison.Ordinal) &&
            table.Length <= "tiny_cosmos_".Length + 32 &&
            table["tiny_cosmos_".Length..].All(IsLowerHex))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "nft command does not match an approved broker firewall operation.");
    }

    private static TinyCosmosError? ValidateBrokerShell(IReadOnlyList<string> args)
    {
        if (args is not ["-c", var script] ||
            script.Length > 4096 ||
            script.Contains('\n') ||
            script.Contains('\r') ||
            script.Contains('`') ||
            script.Contains('$'))
        {
            return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "shell command does not match an approved broker operation.");
        }

        if (script.StartsWith(": tinycosmos-network; ", StringComparison.Ordinal))
        {
            return null;
        }

        if (script.StartsWith(": tinycosmos-storage; ", StringComparison.Ordinal))
        {
            return ValidateStorageShell(script);
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "shell command does not match an approved broker operation.");
    }

    private static TinyCosmosError? ValidateStorageShell(string script)
    {
        foreach (var token in ExtractAbsolutePathTokens(script))
        {
            if (token is "/usr/bin/test" or "/usr/bin/cp" or "/usr/bin/truncate" or "/usr/sbin/mkfs.ext4")
            {
                continue;
            }

            if ((IsUnderBrokerRoot(token, BrokerPaths.ImageRoot) || IsUnderBrokerRoot(token, BrokerPaths.StateRoot)) &&
                token.EndsWith(".ext4", StringComparison.Ordinal))
            {
                continue;
            }

            return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "storage shell command contains an unapproved path.");
        }

        return null;
    }

    private static IEnumerable<string> ExtractAbsolutePathTokens(string script)
    {
        foreach (var token in script.Split([' ', '\t', '(', ')', ';', '&', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("/", StringComparison.Ordinal))
            {
                yield return token;
            }
        }
    }

    private static TinyCosmosError? ValidateJailer(IReadOnlyList<string> args)
    {
        if (args is ["--id", var id, "--exec-file", var firecrackerPath, "--uid", "0", "--gid", "0", "--", "--api-sock", var apiSocket] &&
            firecrackerPath == Path.Combine(BrokerPaths.InstallBinRoot, "firecracker") &&
            IsApprovedJailerId(id) &&
            apiSocket == FirecrackerApiSocketInJailer)
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "jailer command does not match an approved Firecracker launch operation.");
    }

    private static TinyCosmosError? ValidateSystemdRun(IReadOnlyList<string> args)
    {
        if (args.Count >= 12 &&
            args[0] == "--unit" &&
            IsApprovedSandboxUnit(args[1]) &&
            args[2] == "--collect" &&
            args[3] == "--property" &&
            args[4] == "Type=simple" &&
            args[5] == "--property" &&
            args[6] == "KillMode=control-group" &&
            args[7] == "/usr/sbin/ip" &&
            args[8] == "netns" &&
            args[9] == "exec" &&
            IsApprovedNetworkNamespace(args[10]) &&
            args[11] == "/usr/lib/tiny-cosmos/bin/jailer" &&
            ValidateJailer(args.Skip(12).ToArray()) is null)
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "systemd-run command does not match an approved Firecracker unit launch operation.");
    }

    private static TinyCosmosError? ValidateSystemctl(IReadOnlyList<string> args)
    {
        if (args is ["stop", var unit] && IsApprovedSandboxUnit(unit))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "systemctl command does not match an approved Firecracker unit cleanup operation.");
    }

    private static TinyCosmosError? ValidateRm(IReadOnlyList<string> args)
    {
        if (args.Count >= 2 &&
            args[0] == "-rf" &&
            args.Skip(1).All(path => IsUnderBrokerRoot(path, BrokerPaths.StateRoot, BrokerPaths.RunRoot, BrokerPaths.JailerRoot)))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "rm command does not match an approved broker cleanup operation.");
    }

    private static TinyCosmosError? ValidateCurl(IReadOnlyList<string> args)
    {
        if (args is [
            "--fail",
            "--silent",
            "--show-error",
            "--retry-connrefused",
            "--retry", "50",
            "--retry-delay", "0",
            "--connect-timeout", "1",
            "--unix-socket", var socketPath,
            "-X", "PUT",
            var url,
            "-H", "Content-Type: application/json",
            "-d", var body
        ] &&
            IsUnderBrokerRoot(socketPath, BrokerPaths.RunRoot, BrokerPaths.JailerRoot) &&
            socketPath.EndsWith("api.sock", StringComparison.Ordinal) &&
            TryGetFirecrackerEndpoint(url, out var endpoint) &&
            IsApprovedFirecrackerBody(endpoint, body))
        {
            return null;
        }

        return new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "curl command does not match an approved Firecracker API operation.");
    }

    private static bool TryGetFirecrackerEndpoint(string url, out string endpoint)
    {
        const string prefix = "http://localhost";
        endpoint = string.Empty;
        if (!url.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        endpoint = url[prefix.Length..];
        return endpoint is "/boot-source" or "/machine-config" or "/drives/rootfs" or "/drives/workspace" or "/network-interfaces/eth0" or "/vsock" or "/actions";
    }

    private static bool IsApprovedFirecrackerBody(string endpoint, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return endpoint switch
            {
                "/boot-source" =>
                    root.TryGetProperty("kernel_image_path", out var kernel) &&
                    kernel.ValueKind == JsonValueKind.String &&
                    kernel.GetString() == KernelPathInJailer &&
                    root.TryGetProperty("boot_args", out var bootArgs) &&
                    bootArgs.ValueKind == JsonValueKind.String &&
                    (!root.TryGetProperty("initrd_path", out var initrdPath) ||
                        (initrdPath.ValueKind == JsonValueKind.String && initrdPath.GetString() == InitrdPathInJailer)),
                "/machine-config" =>
                    root.TryGetProperty("vcpu_count", out var vcpu) &&
                    vcpu.TryGetInt32(out var vcpuValue) &&
                    vcpuValue is >= 1 and <= 16 &&
                    root.TryGetProperty("mem_size_mib", out var memory) &&
                    memory.TryGetInt64(out var memoryValue) &&
                    memoryValue is >= 512 and <= 65536,
                "/drives/rootfs" =>
                    ValidateFirecrackerDriveBody(root, "rootfs", isRoot: true),
                "/drives/workspace" =>
                    ValidateFirecrackerDriveBody(root, "workspace", isRoot: false),
                "/network-interfaces/eth0" =>
                    root.TryGetProperty("iface_id", out var ifaceId) &&
                    ifaceId.GetString() == "eth0" &&
                    root.TryGetProperty("guest_mac", out var guestMac) &&
                    guestMac.GetString() == "AA:FC:00:00:00:01" &&
                    root.TryGetProperty("host_dev_name", out var hostDevName) &&
                    IsApprovedTapName(hostDevName.GetString() ?? string.Empty),
                "/vsock" =>
                    root.TryGetProperty("vsock_id", out var vsockId) &&
                    vsockId.GetString() == "tc-vsock" &&
                    root.TryGetProperty("guest_cid", out var guestCid) &&
                    guestCid.TryGetInt32(out var cid) &&
                    cid == 3 &&
                    root.TryGetProperty("uds_path", out var udsPath) &&
                    udsPath.GetString() == FirecrackerVsockSocketInJailer,
                "/actions" =>
                    root.TryGetProperty("action_type", out var action) &&
                    action.GetString() == "InstanceStart",
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidateFirecrackerDriveBody(JsonElement root, string expectedId, bool isRoot)
    {
        return root.TryGetProperty("drive_id", out var driveId) &&
            driveId.GetString() == expectedId &&
            root.TryGetProperty("path_on_host", out var path) &&
            path.GetString() == (isRoot ? RootDrivePathInJailer : WorkspaceDrivePathInJailer) &&
            root.TryGetProperty("is_root_device", out var rootDevice) &&
            rootDevice.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            rootDevice.GetBoolean() == isRoot &&
            root.TryGetProperty("is_read_only", out var readOnly) &&
            readOnly.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            !readOnly.GetBoolean();
    }

    private static bool IsApprovedBindMountSource(string source)
    {
        if (source == Path.Combine(BrokerPaths.ImageRoot, "kernel") ||
            source == Path.Combine(BrokerPaths.ImageRoot, "initrd"))
        {
            return true;
        }

        return IsUnderBrokerRoot(source, BrokerPaths.StateRoot) && source.EndsWith(".ext4", StringComparison.Ordinal);
    }

    private static bool IsNumericOwner(string owner)
    {
        var separator = owner.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == owner.Length - 1 || owner[..separator] != owner[(separator + 1)..])
        {
            return false;
        }

        return int.TryParse(owner[..separator], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var uid) &&
            uid is >= 0 and <= 65535;
    }

    private static bool IsApprovedDiskSize(string value)
    {
        if (!value.EndsWith('G') || !long.TryParse(value[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size))
        {
            return false;
        }

        return size is >= 1 and <= 1024;
    }

    private static bool IsUnderBrokerRoot(string path, params string[] roots)
    {
        if (!Path.IsPathRooted(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        foreach (var root in roots)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsShellMetacharacter(string value)
    {
        return value.IndexOfAny(['\n', '\r', '\t', ';', '&', '|', '<', '>', '`', '$']) >= 0;
    }

    private static bool IsResourceToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        return value.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '_' or '-');
    }

    private static bool IsApprovedSandboxUnit(string value)
    {
        const string prefix = "tinycosmos-sandbox-";
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.Length == prefix.Length + 12 &&
            value[prefix.Length..].All(IsLowerHex);
    }

    private static bool IsApprovedNetworkNamespace(string value)
    {
        const string prefix = "tc-";
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.Length == prefix.Length + 12 &&
            value[prefix.Length..].All(IsLowerHex);
    }

    private static bool IsApprovedJailerId(string value)
    {
        return value.Length == 12 && value.All(IsLowerHex);
    }

    private static bool IsApprovedTapName(string value)
    {
        const string prefix = "tap-";
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.Length == prefix.Length + 9 &&
            value[prefix.Length..].All(IsLowerHex);
    }

    private static bool IsLowerHex(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
    }
}
