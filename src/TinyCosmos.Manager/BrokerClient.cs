using System.Net.Sockets;
using System.Text.Json;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

namespace TinyCosmos.Manager;

public interface IBrokerClient
{
    Task<BrokerStoragePlan> PlanStorageAsync(BrokerStorageRequest request, CancellationToken cancellationToken);

    Task<BrokerNetworkPlan> PlanNetworkAsync(BrokerNetworkRequest request, CancellationToken cancellationToken);

    Task<BrokerLaunchPlan> PlanLaunchAsync(BrokerLaunchRequest request, CancellationToken cancellationToken);

    Task<BrokerStopPlan> PlanStopAsync(BrokerStopRequest request, CancellationToken cancellationToken);

    Task<BrokerCleanupPlan> PlanCleanupAsync(BrokerCleanupRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrokerCommandResult>> ExecutePlanAsync(IReadOnlyList<PlannedCommand> commands, CancellationToken cancellationToken);
}

public sealed class UnixBrokerClient(string socketPath) : IBrokerClient
{
    public async Task<BrokerStoragePlan> PlanStorageAsync(BrokerStorageRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync(BrokerOperationNames.PlanStorage, request, LinuxJsonContext.Default.BrokerStoragePlan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrokerNetworkPlan> PlanNetworkAsync(BrokerNetworkRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync(BrokerOperationNames.PlanNetwork, request, LinuxJsonContext.Default.BrokerNetworkPlan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrokerLaunchPlan> PlanLaunchAsync(BrokerLaunchRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync(BrokerOperationNames.PlanLaunch, request, LinuxJsonContext.Default.BrokerLaunchPlan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrokerCleanupPlan> PlanCleanupAsync(BrokerCleanupRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync(BrokerOperationNames.PlanCleanup, request, LinuxJsonContext.Default.BrokerCleanupPlan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrokerStopPlan> PlanStopAsync(BrokerStopRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync(BrokerOperationNames.PlanStop, request, LinuxJsonContext.Default.BrokerStopPlan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BrokerCommandResult>> ExecutePlanAsync(IReadOnlyList<PlannedCommand> commands, CancellationToken cancellationToken)
    {
        return await SendAsync(BrokerOperationNames.ExecutePlan, commands.ToArray(), LinuxJsonContext.Default.BrokerCommandResultArray, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var envelope = new ProtocolEnvelope(
            ProtocolVersion.Current,
            TinyId.NewOperationId().Value,
            operation,
            JsonSerializer.SerializeToElement(request, LinuxJsonContext.Default.Options.GetTypeInfo(typeof(TRequest))));
        await FrameCodec.WriteAsync(stream, envelope, TinyCosmosJsonContext.Default.ProtocolEnvelope, cancellationToken).ConfigureAwait(false);
        var response = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolResponse, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new TinyCosmosException(response.Error ?? new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Broker returned an unknown error."));
        }

        if (response.Payload is not { } payload)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Broker response payload was empty."));
        }

        return payload.Deserialize(responseType)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Broker response payload was empty."));
    }
}

public sealed class LocalPlanningBrokerClient : IBrokerClient
{
    public List<PlannedCommand> ExecutedCommands { get; } = [];

    public Task<BrokerStoragePlan> PlanStorageAsync(BrokerStorageRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(BrokerCommandPlanner.PlanStorage(request));
    }

    public Task<BrokerNetworkPlan> PlanNetworkAsync(BrokerNetworkRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(BrokerCommandPlanner.PlanNetwork(request));
    }

    public Task<BrokerLaunchPlan> PlanLaunchAsync(BrokerLaunchRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(BrokerCommandPlanner.PlanLaunch(request));
    }

    public Task<BrokerCleanupPlan> PlanCleanupAsync(BrokerCleanupRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(BrokerCommandPlanner.PlanCleanup(request));
    }

    public Task<BrokerStopPlan> PlanStopAsync(BrokerStopRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(BrokerCommandPlanner.PlanStop(request));
    }

    public Task<IReadOnlyList<BrokerCommandResult>> ExecutePlanAsync(IReadOnlyList<PlannedCommand> commands, CancellationToken cancellationToken)
    {
        ExecutedCommands.AddRange(commands);
        IReadOnlyList<BrokerCommandResult> results = commands
            .Select(command => new BrokerCommandResult(command.FileName, command.Arguments, 0, command.ToString(), string.Empty, false))
            .ToArray();
        return Task.FromResult(results);
    }
}

public sealed class FailingBrokerClient(TinyCosmosError error) : IBrokerClient
{
    public Task<BrokerStoragePlan> PlanStorageAsync(BrokerStorageRequest request, CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(error);
    }

    public Task<BrokerNetworkPlan> PlanNetworkAsync(BrokerNetworkRequest request, CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(error);
    }

    public Task<BrokerLaunchPlan> PlanLaunchAsync(BrokerLaunchRequest request, CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(error);
    }

    public Task<BrokerCleanupPlan> PlanCleanupAsync(BrokerCleanupRequest request, CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(error);
    }

    public Task<BrokerStopPlan> PlanStopAsync(BrokerStopRequest request, CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(error);
    }

    public Task<IReadOnlyList<BrokerCommandResult>> ExecutePlanAsync(IReadOnlyList<PlannedCommand> commands, CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(error);
    }
}

public sealed record BrokerProvisioningResult(
    BrokerStoragePlan Storage,
    BrokerNetworkPlan Network,
    BrokerLaunchPlan Launch,
    IReadOnlyList<BrokerCommandResult> CommandResults);

public sealed class SandboxProvisioner(IBrokerClient brokerClient)
{
    public async Task<BrokerProvisioningResult> ProvisionPrimaryAsync(GroupBundle bundle, CancellationToken cancellationToken)
    {
        var storage = await brokerClient.PlanStorageAsync(new BrokerStorageRequest(
            bundle.Group.OwnerUid,
            bundle.Group.GroupId.Value,
            bundle.PrimarySandbox.SandboxId.Value,
            bundle.PrimarySandbox.Resources.SystemDiskGiB,
            bundle.PrimarySandbox.Resources.WorkspaceDiskGiB), cancellationToken).ConfigureAwait(false);

        var network = await brokerClient.PlanNetworkAsync(new BrokerNetworkRequest(
            bundle.Group.GroupId.Value,
            bundle.PrimarySandbox.SandboxId.Value,
            bundle.PrimarySandbox.Network.Cidr,
            bundle.PrimarySandbox.Network.GatewayAddress + "/24",
            bundle.PrimarySandbox.Network.PrimaryAddress + "/24",
            BuildHostVethAddress(bundle.PrimarySandbox.Network)), cancellationToken).ConfigureAwait(false);

        var runtimeRoot = BrokerPaths.RuntimeRoot(bundle.PrimarySandbox.SandboxId.Value);
        var launch = await brokerClient.PlanLaunchAsync(new BrokerLaunchRequest(
            bundle.Group.OwnerUid,
            bundle.PrimarySandbox.SandboxId.Value,
            bundle.PrimarySandbox.ImageId,
            Path.Combine(BrokerPaths.ImageRoot, "kernel"),
            OptionalImagePath("initrd"),
            storage.SystemDiskPath,
            storage.WorkspaceDiskPath,
            Path.Combine(runtimeRoot, "api.sock"),
            Path.Combine(runtimeRoot, "vsock.sock"),
            bundle.PrimarySandbox.Resources.Vcpu,
            bundle.PrimarySandbox.Resources.MemoryMiB,
            BuildBootArgs(bundle.PrimarySandbox.Network),
            network.NamespaceName,
            network.TapName), cancellationToken).ConfigureAwait(false);

        var commands = storage.Commands.Concat(network.Commands).Concat(launch.Commands).ToArray();
        var results = await brokerClient.ExecutePlanAsync(commands, cancellationToken).ConfigureAwait(false);
        var failed = results.FirstOrDefault(result => result.ExitCode != 0);
        if (failed is not null)
        {
            var stderr = string.IsNullOrWhiteSpace(failed.Stderr) ? string.Empty : " stderr: " + failed.Stderr.Trim();
            throw new TinyCosmosException(new TinyCosmosError(
                TinyCosmosErrorCode.PlatformRejected,
                $"Broker command failed ({failed.ExitCode}): {failed.FileName} {string.Join(' ', failed.Arguments)}.{stderr}"));
        }

        return new BrokerProvisioningResult(storage, network, launch, results);
    }

    private static string BuildBootArgs(NetworkAllocation network)
    {
        return "console=ttyS0 reboot=k panic=1 acpi=off ip=" +
            network.PrimaryAddress +
            "::" +
            network.GatewayAddress +
            ":255.255.255.0:tinycosmos:eth0:off";
    }

    private static string BuildHostVethAddress(NetworkAllocation network)
    {
        var slash = network.Cidr.IndexOf('/', StringComparison.Ordinal);
        var networkAddress = slash >= 0 ? network.Cidr[..slash] : network.Cidr;
        var octets = networkAddress.Split('.');
        if (octets.Length != 4)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "Network allocation is not an IPv4 /24 CIDR."));
        }

        return string.Join('.', octets[0], octets[1], octets[2], "254") + "/24";
    }

    private static string? OptionalImagePath(string fileName)
    {
        var path = Path.Combine(BrokerPaths.ImageRoot, fileName);
        return File.Exists(path) ? path : null;
    }
}

public sealed class SandboxCleaner(IBrokerClient brokerClient)
{
    public async Task<IReadOnlyList<BrokerCommandResult>> StopPrimaryAsync(GroupBundle bundle, CancellationToken cancellationToken)
    {
        var plan = await brokerClient.PlanStopAsync(new BrokerStopRequest(
            bundle.Group.GroupId.Value,
            bundle.PrimarySandbox.SandboxId.Value), cancellationToken).ConfigureAwait(false);
        var results = await brokerClient.ExecutePlanAsync(plan.Commands, cancellationToken).ConfigureAwait(false);
        if (results.Any(result => result.ExitCode != 0))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "Broker stop plan failed."));
        }

        return results;
    }

    public async Task<IReadOnlyList<BrokerCommandResult>> CleanupPrimaryAsync(GroupBundle bundle, CancellationToken cancellationToken)
    {
        var plan = await brokerClient.PlanCleanupAsync(new BrokerCleanupRequest(
            bundle.Group.GroupId.Value,
            bundle.PrimarySandbox.SandboxId.Value), cancellationToken).ConfigureAwait(false);
        var results = await brokerClient.ExecutePlanAsync(plan.Commands, cancellationToken).ConfigureAwait(false);
        if (results.Any(result => result.ExitCode != 0))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.PlatformRejected, "Broker cleanup plan failed."));
        }

        return results;
    }
}
