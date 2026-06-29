using System.Text.Json;
using TinyCosmos.Broker;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

namespace TinyCosmos.Integration.Tests;

public sealed class BrokerProtocolServerTests
{
    [Fact]
    public async Task BrokerProtocolPlansStorageFromTypedPayload()
    {
        using var temp = new TempDir();
        var server = new BrokerProtocolServer(Path.Combine(temp.Path, "broker.sock"));
        var request = new BrokerStorageRequest(1000, "grp_proto", "sbx_proto", 12, 32);

        var response = await server.DispatchAsync(Envelope(BrokerOperationNames.PlanStorage, request), new PeerCredentials(123, 1000, 1000));

        Assert.True(response.Success, response.Error?.Message);
        var plan = response.Payload!.Value.Deserialize(LinuxJsonContext.Default.BrokerStoragePlan)!;
        Assert.Contains("grp_proto", plan.SystemDiskPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokerProtocolRejectsUnknownOperation()
    {
        using var temp = new TempDir();
        var server = new BrokerProtocolServer(Path.Combine(temp.Path, "broker.sock"));

        var response = await server.DispatchAsync(Envelope("Nope", new EmptyPayload()), new PeerCredentials(123, 1000, 1000));

        Assert.False(response.Success);
        Assert.Equal(TinyCosmosErrorCode.UnsupportedOperation, response.Error?.Code);
    }

    [Fact]
    public async Task BrokerProtocolAllowsNonRootPeerToExecuteApprovedPlanThroughValidator()
    {
        using var temp = new TempDir();
        var server = new BrokerProtocolServer(Path.Combine(temp.Path, "broker.sock"), dryRunExecution: true);

        var response = await server.DispatchAsync(
            Envelope(BrokerOperationNames.ExecutePlan, new[] { new PlannedCommand("/usr/bin/install", ["-d", "-m", "0700", "-o", "root", "-g", "root", "/var/lib/tiny-cosmos/groups/grp/sandboxes/sbx"], true) }),
            new PeerCredentials(123, 1000, 1000));

        Assert.True(response.Success, response.Error?.Message);
        var results = response.Payload!.Value.Deserialize(LinuxJsonContext.Default.BrokerCommandResultArray)!;
        Assert.Single(results);
    }

    [Fact]
    public async Task BrokerProtocolRootExecutePlanUsesConfiguredDryRunMode()
    {
        using var temp = new TempDir();
        var server = new BrokerProtocolServer(Path.Combine(temp.Path, "broker.sock"), dryRunExecution: true);

        var response = await server.DispatchAsync(
            Envelope(BrokerOperationNames.ExecutePlan, new[] { new PlannedCommand("/usr/bin/install", ["-d", "-m", "0700", "-o", "root", "-g", "root", "/var/lib/tiny-cosmos/groups/grp/sandboxes/sbx"], true) }),
            new PeerCredentials(123, 0, 0));

        Assert.True(response.Success, response.Error?.Message);
        var results = response.Payload!.Value.Deserialize(LinuxJsonContext.Default.BrokerCommandResultArray)!;
        var result = Assert.Single(results);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/usr/bin/install", result.Stdout, StringComparison.Ordinal);
    }

    private static ProtocolEnvelope Envelope<T>(string operation, T payload)
    {
        return new ProtocolEnvelope(
            ProtocolVersion.Current,
            TinyId.NewOperationId().Value,
            operation,
            JsonSerializer.SerializeToElement(payload, LinuxJsonContext.Default.Options.GetTypeInfo(typeof(T))));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-broker-proto-" + Guid.NewGuid().ToString("N"));

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
