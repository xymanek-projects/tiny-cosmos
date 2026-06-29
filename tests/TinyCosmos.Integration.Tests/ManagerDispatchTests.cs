using TinyCosmos.Core;
using TinyCosmos.Manager;
using TinyCosmos.Protocol;

namespace TinyCosmos.Integration.Tests;

public sealed class ManagerDispatchTests
{
    [Fact]
    public async Task DispatcherCreatesListsAndInspectsGroup()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var server = new ManagerProtocolServer(Path.Combine(temp.Path, "manager.sock"), store);

        var create = new GroupCreatePayload("opencode:test", 1000, "/host", "/workspace/test", "image", ResourceAllocation.Default);
        var createResponse = await server.DispatchAsync(Envelope(ManagerOperations.GetOrCreateGroup, create));
        Assert.True(createResponse.Success, createResponse.Error?.Message);
        var created = ProtocolJson.FromElement(createResponse.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundle);

        var listResponse = await server.DispatchAsync(Envelope(ManagerOperations.ListGroups, new ListGroupsPayload(1000)));
        Assert.True(listResponse.Success, listResponse.Error?.Message);
        var listed = ProtocolJson.FromElement(listResponse.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundleArray);
        Assert.Single(listed);

        var inspectResponse = await server.DispatchAsync(Envelope(ManagerOperations.InspectGroup, new GroupTargetPayload(1000, created.Group.GroupId.Value)));
        Assert.True(inspectResponse.Success, inspectResponse.Error?.Message);
    }

    [Fact]
    public async Task DispatcherForksGroupThroughProtocol()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var server = new ManagerProtocolServer(Path.Combine(temp.Path, "manager.sock"), store);
        var createResponse = await server.DispatchAsync(Envelope(
            ManagerOperations.CreateGroup,
            new GroupCreatePayload("opencode:source", 1000, "/host", "/workspace/source", "image", ResourceAllocation.Default)));
        Assert.True(createResponse.Success, createResponse.Error?.Message);
        var source = ProtocolJson.FromElement(createResponse.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundle);

        var forkResponse = await server.DispatchAsync(Envelope(
            ManagerOperations.ForkGroup,
            new ForkGroupPayload(1000, source.Group.GroupId.Value, "opencode:fork")));

        Assert.True(forkResponse.Success, forkResponse.Error?.Message);
        var fork = ProtocolJson.FromElement(forkResponse.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundle);
        Assert.NotEqual(source.Group.GroupId, fork.Group.GroupId);
        Assert.NotEqual(source.PrimarySandbox.SandboxId, fork.PrimarySandbox.SandboxId);
        Assert.Equal("opencode:fork", fork.Group.LogicalName.Value);
        Assert.Equal("/host", fork.Group.Metadata.HostWorkspacePath);
    }

    [Fact]
    public async Task OrdinaryExecDoesNotFallbackToHostExecutionWithoutGuestExecutor()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var server = new ManagerProtocolServer(
            Path.Combine(temp.Path, "manager.sock"),
            store,
            new LocalPlanningBrokerClient(),
            new PassingHostPrerequisiteChecker());
        var createResponse = await server.DispatchAsync(Envelope(
            ManagerOperations.GetOrCreateGroup,
            new GroupCreatePayload("opencode:exec", 1000, null, "/workspace/test", "image", ResourceAllocation.Default)));
        var group = ProtocolJson.FromElement(createResponse.Payload!.Value, TinyCosmosJsonContext.Default.GroupBundle);

        var execResponse = await server.DispatchAsync(Envelope(
            ManagerOperations.Exec,
            new ExecPayload(1000, group.Group.GroupId.Value, "/workspace/test", ["/bin/sh", "-c", "echo host"], 5, 1024)));

        Assert.False(execResponse.Success);
        Assert.Equal(TinyCosmosErrorCode.NotReady, execResponse.Error?.Code);
    }

    [Fact]
    public async Task DiagnosticsReturnsHostChecksThroughManagerProtocol()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var server = new ManagerProtocolServer(Path.Combine(temp.Path, "manager.sock"), store);

        var response = await server.DispatchAsync(Envelope(ManagerOperations.Diagnostics, new EmptyPayload()));

        Assert.True(response.Success, response.Error?.Message);
        var diagnostics = ProtocolJson.FromElement(response.Payload!.Value, TinyCosmosJsonContext.Default.ManagerDiagnosticsResponse);
        Assert.NotEmpty(diagnostics.Checks);
        Assert.Contains(diagnostics.Checks, check => check.Name == "os");
        Assert.Contains(diagnostics.Checks, check => check.Name.StartsWith("tool:", StringComparison.Ordinal));
    }

    private static ProtocolEnvelope Envelope<T>(string operation, T payload)
    {
        return new ProtocolEnvelope(
            ProtocolVersion.Current,
            TinyId.NewOperationId().Value,
            operation,
            System.Text.Json.JsonSerializer.SerializeToElement(payload, TinyCosmosJsonContext.Default.Options.GetTypeInfo(typeof(T))));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-int-" + Guid.NewGuid().ToString("N"));

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
