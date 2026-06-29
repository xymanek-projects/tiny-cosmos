using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

namespace TinyCosmos.Manager;

[SupportedOSPlatform("linux")]
public sealed class ManagerProtocolServer(
    string socketPath,
    StateStore stateStore,
    IBrokerClient? brokerClient = null,
    IHostPrerequisiteChecker? prerequisiteChecker = null,
    IGuestExecutor? guestExecutor = null,
    IGuestControlClient? guestControlClient = null,
    IGuestFileService? guestFileService = null,
    TimeSpan? idleStopAfter = null,
    TimeSpan? idleScanInterval = null)
{
    private readonly SandboxProvisioner _provisioner = new(brokerClient ?? new LocalPlanningBrokerClient());
    private readonly SandboxCleaner _cleaner = new(brokerClient ?? new LocalPlanningBrokerClient());
    private readonly IHostPrerequisiteChecker _prerequisiteChecker = prerequisiteChecker ?? new LinuxHostPrerequisiteChecker();
    private readonly IGuestExecutor _guestExecutor = guestExecutor ?? new NotReadyGuestExecutor();
    private readonly IGuestControlClient _guestControlClient = guestControlClient ?? new NotReadyGuestControlClient();
    private readonly IGuestFileService _guestFileService = guestFileService ?? new NotReadyGuestFileService();
    private readonly TimeSpan? _idleStopAfter = idleStopAfter;
    private readonly TimeSpan _idleScanInterval = idleScanInterval ?? TimeSpan.FromMinutes(1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _ = await stateStore.ReconcileInterruptedSandboxesAsync(cancellationToken).ConfigureAwait(false);
        var idleWorker = _idleStopAfter is null
            ? Task.CompletedTask
            : Task.Run(() => RunIdleStopLoopAsync(cancellationToken), CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(socketPath))!);
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        socket.Listen(128);
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        while (!cancellationToken.IsCancellationRequested)
        {
            var accepted = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => HandleClientAsync(accepted, cancellationToken), CancellationToken.None);
        }

        await idleWorker.ConfigureAwait(false);
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var _ = socket;
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolEnvelope envelope;
            try
            {
                envelope = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolEnvelope, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            var response = await DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
            await FrameCodec.WriteAsync(stream, response, TinyCosmosJsonContext.Default.ProtocolResponse, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ProtocolResponse> DispatchAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Version != ProtocolVersion.Current)
        {
            return Failure(envelope, new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Unsupported protocol version."));
        }

        try
        {
            return envelope.Operation switch
            {
                ManagerOperations.Capabilities => Success(envelope, new CapabilityResponse(
                    ProtocolVersion.Current,
                    [
                        ManagerOperations.Capabilities,
                        ManagerOperations.GetOrCreateGroup,
                        ManagerOperations.CreateGroup,
                        ManagerOperations.ListGroups,
                        ManagerOperations.InspectGroup,
                        ManagerOperations.UpdateGroupMetadata,
                        ManagerOperations.DeleteGroup,
                        ManagerOperations.ForkGroup,
                        ManagerOperations.ReplacePrimarySandbox,
                        ManagerOperations.StartSandbox,
                        ManagerOperations.StopSandbox,
                        ManagerOperations.Exec,
                        ManagerOperations.FileRead,
                        ManagerOperations.FileWrite,
                        ManagerOperations.FileList,
                        ManagerOperations.FileSearch,
                        ManagerOperations.Diagnostics
                    ],
                    new Dictionary<string, bool>
                    {
                        ["portPublication"] = false,
                        ["guestLsp"] = false,
                        ["sandboxLocalMcp"] = false,
                        ["nativeVmStart"] = true
                    })),
                ManagerOperations.GetOrCreateGroup => Success(envelope, await GetOrCreateAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.CreateGroup => Success(envelope, await CreateAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.ListGroups => Success(envelope, (await ListAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)).ToArray()),
                ManagerOperations.InspectGroup => Success(envelope, await InspectAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.UpdateGroupMetadata => Success(envelope, await UpdateAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.DeleteGroup => Success(envelope, await DeleteAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.ForkGroup => Success(envelope, await ForkAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.ReplacePrimarySandbox => Success(envelope, await ReplacePrimaryAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.StartSandbox => Success(envelope, await TransitionAsync(envelope.Payload, LifecycleState.Ready, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.StopSandbox => Success(envelope, await TransitionAsync(envelope.Payload, LifecycleState.Stopped, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.Exec => Success(envelope, await ExecAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.FileRead => Success(envelope, await FileReadAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.FileWrite => Success(envelope, await FileWriteAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.FileList => Success(envelope, await FileListAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.FileSearch => Success(envelope, await FileSearchAsync(envelope.Payload, cancellationToken).ConfigureAwait(false)),
                ManagerOperations.Diagnostics => Success(envelope, Diagnostics()),
                _ => Failure(envelope, new TinyCosmosError(TinyCosmosErrorCode.UnsupportedOperation, "Unknown manager operation."))
            };
        }
        catch (TinyCosmosException ex)
        {
            return Failure(envelope, ex.Error);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException or SocketException or JsonException)
        {
            return Failure(envelope, new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, ex.Message));
        }
    }

    private async Task<GroupBundle> GetOrCreateAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        return await stateStore.GetOrCreateGroupAsync(ParseCreate(payload), cancellationToken).ConfigureAwait(false);
    }

    private async Task<GroupBundle> CreateAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        return await stateStore.CreateGroupAsync(ParseCreate(payload), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GroupBundle>> ListAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.ListGroupsPayload);
        return await stateStore.ListGroupsAsync(request.OwnerUid, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GroupBundle> InspectAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.GroupTargetPayload);
        return await stateStore.FindByGroupIdAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found."));
    }

    private async Task<GroupBundle> UpdateAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.UpdateGroupMetadataPayload);
        LogicalGroupName? newName = null;
        if (!string.IsNullOrEmpty(request.NewLogicalName))
        {
            if (!LogicalGroupName.TryParse(request.NewLogicalName, out var parsed, out var error))
            {
                throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, error));
            }

            newName = parsed;
        }

        return await stateStore.UpdateMetadataAsync(
            request.OwnerUid,
            new GroupId(request.GroupId),
            newName,
            new GroupMetadata(request.HostWorkspacePath, request.GuestProjectPath, request.ArchivedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GroupBundle> DeleteAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.GroupTargetPayload);
        var existing = await stateStore.FindByGroupIdAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found."));
        await _cleaner.CleanupPrimaryAsync(existing, cancellationToken).ConfigureAwait(false);
        await stateStore.DeleteGroupAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false);
        return existing;
    }

    private async Task<GroupBundle> ForkAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.ForkGroupPayload);
        if (!LogicalGroupName.TryParse(request.NewLogicalName, out var logicalName, out var error))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, error));
        }

        return await stateStore.ForkGroupAsync(request.OwnerUid, new GroupId(request.SourceGroupId), logicalName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GroupBundle> ReplacePrimaryAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.ReplacePrimarySandboxPayload);
        var existing = await stateStore.FindByGroupIdAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found."));
        if (existing.PrimarySandbox.LifecycleState is not (LifecycleState.Stopped or LifecycleState.Failed))
        {
            throw new TinyCosmosException(new TinyCosmosError(
                TinyCosmosErrorCode.LifecycleConflict,
                "Primary sandbox must be stopped or failed before replacement."));
        }

        await _cleaner.CleanupPrimaryAsync(existing, cancellationToken).ConfigureAwait(false);
        return await stateStore.ReplacePrimarySandboxAsync(
            request.OwnerUid,
            new GroupId(request.GroupId),
            request.ImageId,
            request.Resources,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecResult> ExecAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.ExecPayload);
        if (request.Command.Count == 0)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Exec command must not be empty."));
        }

        var group = await EnsureReadyGroupAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false);
        var result = await _guestExecutor.ExecuteAsync(group, request, cancellationToken).ConfigureAwait(false);
        await stateStore.TouchSandboxAsync(request.OwnerUid, group.PrimarySandbox.SandboxId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<FileReadResult> FileReadAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.FileReadPayload);
        ValidateGuestPath(request.Path);
        if (request.MaxBytes is < 1 or > 64 * 1024 * 1024)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Read limit must be between 1 byte and 64 MiB."));
        }

        var group = await EnsureReadyGroupAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false);
        var result = await _guestFileService.ReadAsync(group, request, cancellationToken).ConfigureAwait(false);
        await stateStore.TouchSandboxAsync(request.OwnerUid, group.PrimarySandbox.SandboxId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<FileWriteResult> FileWriteAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.FileWritePayload);
        ValidateGuestPath(request.Path);
        _ = Convert.FromBase64String(request.ContentBase64);
        var group = await EnsureReadyGroupAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false);
        var result = await _guestFileService.WriteAsync(group, request, cancellationToken).ConfigureAwait(false);
        await stateStore.TouchSandboxAsync(request.OwnerUid, group.PrimarySandbox.SandboxId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<FileListResult> FileListAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.FileListPayload);
        ValidateGuestPath(request.Path);
        if (request.MaxEntries is < 1 or > 10_000)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "List limit must be between 1 and 10000 entries."));
        }

        var group = await EnsureReadyGroupAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false);
        var result = await _guestFileService.ListAsync(group, request, cancellationToken).ConfigureAwait(false);
        await stateStore.TouchSandboxAsync(request.OwnerUid, group.PrimarySandbox.SandboxId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<FileSearchResult> FileSearchAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.FileSearchPayload);
        ValidateGuestPath(request.Path);
        if (string.IsNullOrEmpty(request.Pattern))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Search pattern must not be empty."));
        }

        if (request.MaxMatches is < 1 or > 10_000)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Search limit must be between 1 and 10000 matches."));
        }

        var group = await EnsureReadyGroupAsync(request.OwnerUid, new GroupId(request.GroupId), cancellationToken).ConfigureAwait(false);
        var result = await _guestFileService.SearchAsync(group, request, cancellationToken).ConfigureAwait(false);
        await stateStore.TouchSandboxAsync(request.OwnerUid, group.PrimarySandbox.SandboxId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<int> StopIdleSandboxesOnceAsync(DateTimeOffset idleBefore, CancellationToken cancellationToken = default)
    {
        var idle = await stateStore.ListIdleReadySandboxesAsync(idleBefore, cancellationToken).ConfigureAwait(false);
        var stopped = 0;
        foreach (var group in idle)
        {
            try
            {
                await _guestControlClient.FlushAndShutdownAsync(group, cancellationToken).ConfigureAwait(false);
                await _cleaner.StopPrimaryAsync(group, cancellationToken).ConfigureAwait(false);
                await stateStore.TransitionSandboxAsync(
                    group.Group.OwnerUid,
                    group.PrimarySandbox.SandboxId,
                    LifecycleState.Stopping,
                    StopReason.Idle,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await stateStore.TransitionSandboxAsync(
                    group.Group.OwnerUid,
                    group.PrimarySandbox.SandboxId,
                    LifecycleState.Stopped,
                    StopReason.Idle,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                stopped++;
            }
            catch (TinyCosmosException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }

        return stopped;
    }

    private async Task RunIdleStopLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_idleScanInterval, cancellationToken).ConfigureAwait(false);
                if (_idleStopAfter is { } idleStopAfter)
                {
                    await StopIdleSandboxesOnceAsync(DateTimeOffset.UtcNow.Subtract(idleStopAfter), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static ManagerDiagnosticsResponse Diagnostics()
    {
        var report = HostDiagnostics.Run();
        return new ManagerDiagnosticsResponse(
            report.Passed,
            report.Checks.Select(check => new DiagnosticCheckPayload(
                check.Name,
                check.Passed,
                check.Message,
                check.Remediation)).ToArray());
    }

    private async Task<GroupBundle> EnsureReadyGroupAsync(int ownerUid, GroupId groupId, CancellationToken cancellationToken)
    {
        var group = await stateStore.FindByGroupIdAsync(ownerUid, groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found."));
        if (group.PrimarySandbox.LifecycleState == LifecycleState.Stopped)
        {
            var transitionPayload = new SandboxTransitionPayload(ownerUid, group.PrimarySandbox.SandboxId.Value);
            var transitionElement = JsonSerializer.SerializeToElement(transitionPayload, TinyCosmosJsonContext.Default.SandboxTransitionPayload);
            group = await TransitionAsync(transitionElement, LifecycleState.Ready, cancellationToken).ConfigureAwait(false);
        }

        if (group.PrimarySandbox.LifecycleState != LifecycleState.Ready)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.LifecycleConflict, "Sandbox is not ready for guest operations."));
        }

        return group;
    }

    private static void ValidateGuestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\0'))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Guest paths must be absolute POSIX paths."));
        }

        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Guest paths must not contain '..' segments."));
            }
        }
    }

    private async Task<GroupBundle> TransitionAsync(JsonElement payload, LifecycleState requested, CancellationToken cancellationToken)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.SandboxTransitionPayload);
        var groups = await stateStore.ListGroupsAsync(request.OwnerUid, cancellationToken).ConfigureAwait(false);
        var group = groups.FirstOrDefault(candidate => candidate.PrimarySandbox.SandboxId.Value == request.SandboxId)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.SandboxNotFound, "Sandbox not found."));

        var current = group.PrimarySandbox.LifecycleState;
        if (requested == LifecycleState.Ready)
        {
            if (current == LifecycleState.Ready)
            {
                return group;
            }

            if (current != LifecycleState.Stopped)
            {
                throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.LifecycleConflict, "Sandbox is not startable from its current state."));
            }

            var dependencyError = _prerequisiteChecker.ValidateForVmStart();
            if (dependencyError is not null)
            {
                throw new TinyCosmosException(dependencyError);
            }

            await stateStore.TransitionSandboxAsync(request.OwnerUid, new SandboxId(request.SandboxId), LifecycleState.Starting, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                var starting = await stateStore.FindByGroupIdAsync(request.OwnerUid, group.Group.GroupId, cancellationToken).ConfigureAwait(false)
                    ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found during startup."));
                var provisioning = await _provisioner.ProvisionPrimaryAsync(starting, cancellationToken).ConfigureAwait(false);
                await _guestControlClient.EstablishAsync(starting, provisioning, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await stateStore.TransitionSandboxAsync(request.OwnerUid, new SandboxId(request.SandboxId), LifecycleState.Stopped, StopReason.HostReconcile, cancellationToken: cancellationToken).ConfigureAwait(false);
                throw;
            }

            await stateStore.TransitionSandboxAsync(request.OwnerUid, new SandboxId(request.SandboxId), LifecycleState.Ready, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (current == LifecycleState.Stopped)
            {
                return group;
            }

            if (current != LifecycleState.Ready)
            {
                throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.LifecycleConflict, "Sandbox is not stoppable from its current state."));
            }

            await _guestControlClient.FlushAndShutdownAsync(group, cancellationToken).ConfigureAwait(false);
            await _cleaner.StopPrimaryAsync(group, cancellationToken).ConfigureAwait(false);
            await stateStore.TransitionSandboxAsync(request.OwnerUid, new SandboxId(request.SandboxId), LifecycleState.Stopping, StopReason.Explicit, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stateStore.TransitionSandboxAsync(request.OwnerUid, new SandboxId(request.SandboxId), LifecycleState.Stopped, StopReason.Explicit, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return (await stateStore.FindByGroupIdAsync(request.OwnerUid, group.Group.GroupId, cancellationToken).ConfigureAwait(false))!;
    }

    private static CreateGroupRequest ParseCreate(JsonElement payload)
    {
        var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.GroupCreatePayload);
        if (!LogicalGroupName.TryParse(request.LogicalName, out var logicalName, out var error))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, error));
        }

        return new CreateGroupRequest(
            logicalName,
            request.OwnerUid,
            new GroupMetadata(request.HostWorkspacePath, request.GuestProjectPath, null),
            request.ImageId,
            request.Resources);
    }

    private static ProtocolResponse Success<T>(ProtocolEnvelope envelope, T payload)
    {
        return new ProtocolResponse(
            ProtocolVersion.Current,
            envelope.RequestId,
            true,
            JsonSerializer.SerializeToElement(payload, TinyCosmosJsonContext.Default.Options.GetTypeInfo(typeof(T))),
            null);
    }

    private static ProtocolResponse Failure(ProtocolEnvelope envelope, TinyCosmosError error)
    {
        return new ProtocolResponse(ProtocolVersion.Current, envelope.RequestId, false, null, error);
    }
}
