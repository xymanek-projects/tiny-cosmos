using TinyCosmos.Core;

namespace TinyCosmos.Core.Tests;

public sealed class StateStoreTests
{
    [Fact]
    public async Task GetOrCreateConvergesOnExistingGroup()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var logicalName = ParseName("opencode:abc");
        var request = new CreateGroupRequest(
            logicalName,
            1000,
            new GroupMetadata("/host/project", "/workspace/project", null),
            "ubuntu-24.04-dev",
            ResourceAllocation.Default);

        var first = await store.GetOrCreateGroupAsync(request);
        var second = await store.GetOrCreateGroupAsync(request with { Metadata = new GroupMetadata("/changed", "/workspace/changed", null) });

        Assert.Equal(first.Group.GroupId, second.Group.GroupId);
        Assert.Equal("/host/project", second.Group.Metadata.HostWorkspacePath);
        Assert.Equal(first.PrimarySandbox.SandboxId, second.PrimarySandbox.SandboxId);
    }

    [Fact]
    public async Task ExplicitCreateRejectsDuplicateName()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var request = new CreateGroupRequest(ParseName("pi:abc"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default);

        await store.CreateGroupAsync(request);
        var ex = await Assert.ThrowsAsync<TinyCosmosException>(() => store.CreateGroupAsync(request));
        Assert.Equal(TinyCosmosErrorCode.NameConflict, ex.Error.Code);
    }

    [Fact]
    public async Task MetadataUpdatePreservesImmutableIdsAndReleasesOldName()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var bundle = await store.CreateGroupAsync(new CreateGroupRequest(
            ParseName("pi:old"),
            1000,
            new GroupMetadata("/old", "/workspace/old", null),
            "image",
            ResourceAllocation.Default));

        var updated = await store.UpdateMetadataAsync(
            1000,
            bundle.Group.GroupId,
            ParseName("pi:new"),
            new GroupMetadata("/new", "/workspace/new", DateTimeOffset.UnixEpoch));

        Assert.Equal(bundle.Group.GroupId, updated.Group.GroupId);
        Assert.Equal(bundle.PrimarySandbox.SandboxId, updated.PrimarySandbox.SandboxId);
        Assert.Equal("pi:new", updated.Group.LogicalName.Value);

        var newOld = await store.CreateGroupAsync(new CreateGroupRequest(
            ParseName("pi:old"),
            1000,
            new GroupMetadata(null, null, null),
            "image",
            ResourceAllocation.Default));
        Assert.NotEqual(bundle.Group.GroupId, newOld.Group.GroupId);
    }

    [Fact]
    public async Task ForkGroupCopiesMetadataAndPrimaryShapeWithNewIdentity()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var source = await store.CreateGroupAsync(new CreateGroupRequest(
            ParseName("pi:source"),
            1000,
            new GroupMetadata("/host/source", "/workspace/source", DateTimeOffset.UnixEpoch),
            "image-v1",
            new ResourceAllocation(4, 8192, 16, 64)));

        var fork = await store.ForkGroupAsync(1000, source.Group.GroupId, ParseName("pi:fork"));

        Assert.NotEqual(source.Group.GroupId, fork.Group.GroupId);
        Assert.NotEqual(source.PrimarySandbox.SandboxId, fork.PrimarySandbox.SandboxId);
        Assert.Equal("pi:fork", fork.Group.LogicalName.Value);
        Assert.Equal(source.Group.Metadata, fork.Group.Metadata);
        Assert.Equal(source.PrimarySandbox.ImageId, fork.PrimarySandbox.ImageId);
        Assert.Equal(source.PrimarySandbox.Resources, fork.PrimarySandbox.Resources);
        Assert.Equal(LifecycleState.Stopped, fork.PrimarySandbox.LifecycleState);
    }

    [Fact]
    public async Task ForkGroupRejectsDuplicateTargetName()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var source = await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:source"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));
        await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:target"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));

        var ex = await Assert.ThrowsAsync<TinyCosmosException>(() => store.ForkGroupAsync(1000, source.Group.GroupId, ParseName("pi:target")));

        Assert.Equal(TinyCosmosErrorCode.NameConflict, ex.Error.Code);
    }

    [Fact]
    public async Task ReplacePrimarySandboxRetiresOldPrimaryAndCreatesStoppedReplacement()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var bundle = await store.CreateGroupAsync(new CreateGroupRequest(
            ParseName("pi:replace"),
            1000,
            new GroupMetadata(null, null, null),
            "image-v1",
            ResourceAllocation.Default));

        var replacement = await store.ReplacePrimarySandboxAsync(
            1000,
            bundle.Group.GroupId,
            "image-v2",
            new ResourceAllocation(3, 6144, 20, 40));

        Assert.Equal(bundle.Group.GroupId, replacement.Group.GroupId);
        Assert.NotEqual(bundle.PrimarySandbox.SandboxId, replacement.PrimarySandbox.SandboxId);
        Assert.Equal("image-v2", replacement.PrimarySandbox.ImageId);
        Assert.Equal(new ResourceAllocation(3, 6144, 20, 40), replacement.PrimarySandbox.Resources);
        Assert.Equal(LifecycleState.Stopped, replacement.PrimarySandbox.LifecycleState);

        var sandboxes = await store.ListSandboxesForGroupAsync(1000, bundle.Group.GroupId);
        Assert.Equal(2, sandboxes.Count);
        Assert.Contains(sandboxes, sandbox => sandbox.SandboxId == bundle.PrimarySandbox.SandboxId && !sandbox.IsPrimary && sandbox.LifecycleState == LifecycleState.Retired);
        Assert.Contains(sandboxes, sandbox => sandbox.SandboxId == replacement.PrimarySandbox.SandboxId && sandbox.IsPrimary && sandbox.LifecycleState == LifecycleState.Stopped);
    }

    [Fact]
    public async Task ReplacePrimarySandboxRejectsRunningPrimary()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var bundle = await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:running-replace"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));
        await store.TransitionSandboxAsync(1000, bundle.PrimarySandbox.SandboxId, LifecycleState.Starting);
        await store.TransitionSandboxAsync(1000, bundle.PrimarySandbox.SandboxId, LifecycleState.Ready);

        var ex = await Assert.ThrowsAsync<TinyCosmosException>(() =>
            store.ReplacePrimarySandboxAsync(1000, bundle.Group.GroupId, null, null));

        Assert.Equal(TinyCosmosErrorCode.LifecycleConflict, ex.Error.Code);
    }

    [Fact]
    public async Task TransitionRejectsIllegalStateChanges()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var bundle = await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:life"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.TransitionSandboxAsync(1000, bundle.PrimarySandbox.SandboxId, LifecycleState.Ready));
        Assert.Contains("Illegal lifecycle transition", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconcileInterruptedSandboxesRestoresStartingAndStoppingToStopped()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var starting = await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:starting"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));
        var stopping = await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:stopping"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));
        await store.TransitionSandboxAsync(1000, starting.PrimarySandbox.SandboxId, LifecycleState.Starting);
        await store.TransitionSandboxAsync(1000, stopping.PrimarySandbox.SandboxId, LifecycleState.Starting);
        await store.TransitionSandboxAsync(1000, stopping.PrimarySandbox.SandboxId, LifecycleState.Ready);
        await store.TransitionSandboxAsync(1000, stopping.PrimarySandbox.SandboxId, LifecycleState.Stopping, StopReason.Explicit);

        var reopened = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await reopened.InitializeAsync();
        var reconciled = await reopened.ReconcileInterruptedSandboxesAsync();

        Assert.Equal(2, reconciled.Count);
        Assert.Contains(reconciled, item => item.SandboxId == starting.PrimarySandbox.SandboxId && item.PreviousState == LifecycleState.Starting);
        Assert.Contains(reconciled, item => item.SandboxId == stopping.PrimarySandbox.SandboxId && item.PreviousState == LifecycleState.Stopping);
        var afterStarting = await reopened.FindByGroupIdAsync(1000, starting.Group.GroupId);
        var afterStopping = await reopened.FindByGroupIdAsync(1000, stopping.Group.GroupId);
        Assert.Equal(LifecycleState.Stopped, afterStarting!.PrimarySandbox.LifecycleState);
        Assert.Equal(StopReason.HostReconcile, afterStarting.PrimarySandbox.StopReason);
        Assert.Equal(LifecycleState.Stopped, afterStopping!.PrimarySandbox.LifecycleState);
        Assert.Equal(StopReason.HostReconcile, afterStopping.PrimarySandbox.StopReason);
    }

    [Fact]
    public async Task ListIdleReadySandboxesUsesPersistedUpdatedAtCutoff()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var bundle = await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:idle"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));
        await store.TransitionSandboxAsync(1000, bundle.PrimarySandbox.SandboxId, LifecycleState.Starting);
        await store.TransitionSandboxAsync(1000, bundle.PrimarySandbox.SandboxId, LifecycleState.Ready);

        var recentCutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var futureCutoff = DateTimeOffset.UtcNow.AddMinutes(5);

        Assert.Empty(await store.ListIdleReadySandboxesAsync(recentCutoff));
        var idle = await store.ListIdleReadySandboxesAsync(futureCutoff);
        var candidate = Assert.Single(idle);
        Assert.Equal(bundle.Group.GroupId, candidate.Group.GroupId);
        Assert.Equal(LifecycleState.Ready, candidate.PrimarySandbox.LifecycleState);
    }

    [Fact]
    public async Task SandboxLeasesBlockOtherOwnersUntilExpired()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var bundle = await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:lease"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));
        var now = DateTimeOffset.UnixEpoch;

        var first = await store.AcquireLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-a", TimeSpan.FromMinutes(5), now);
        Assert.Equal(now.AddMinutes(5), first.ExpiresAt);

        var conflict = await Assert.ThrowsAsync<TinyCosmosException>(() =>
            store.AcquireLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-b", TimeSpan.FromMinutes(5), now.AddMinutes(1)));
        Assert.Equal(TinyCosmosErrorCode.LifecycleConflict, conflict.Error.Code);

        var replaced = await store.AcquireLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-b", TimeSpan.FromMinutes(5), now.AddMinutes(6));
        Assert.Equal("manager-b", replaced.LeaseOwner);
    }

    [Fact]
    public async Task SandboxLeasesRenewReleaseAndExpireByOwner()
    {
        using var temp = new TempDir();
        var store = new StateStore(Path.Combine(temp.Path, "state.sqlite3"));
        await store.InitializeAsync();
        var bundle = await store.CreateGroupAsync(new CreateGroupRequest(ParseName("pi:lease-renew"), 1000, new GroupMetadata(null, null, null), "image", ResourceAllocation.Default));
        var now = DateTimeOffset.UnixEpoch;

        await store.AcquireLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-a", TimeSpan.FromMinutes(5), now);
        var renewed = await store.RenewLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-a", TimeSpan.FromMinutes(10), now.AddMinutes(1));
        Assert.Equal(now.AddMinutes(11), renewed.ExpiresAt);

        var wrongOwner = await Assert.ThrowsAsync<TinyCosmosException>(() =>
            store.ReleaseLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-b"));
        Assert.Equal(TinyCosmosErrorCode.LifecycleConflict, wrongOwner.Error.Code);

        await store.ReleaseLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-a");
        await store.AcquireLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-b", TimeSpan.FromMinutes(5), now);
        var expired = await store.ExpireLeasesAsync(now.AddMinutes(6));
        Assert.Equal(1, expired);
        await store.AcquireLeaseAsync(1000, bundle.PrimarySandbox.SandboxId, "manager-c", TimeSpan.FromMinutes(5), now.AddMinutes(7));
    }

    private static LogicalGroupName ParseName(string value)
    {
        Assert.True(LogicalGroupName.TryParse(value, out var name, out var error), error);
        return name;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-test-" + Guid.NewGuid().ToString("N"));

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
