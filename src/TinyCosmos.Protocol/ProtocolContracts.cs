using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TinyCosmos.Core;

namespace TinyCosmos.Protocol;

public static class ProtocolVersion
{
    public const int Current = 1;
}

public static class ManagerOperations
{
    public const string Capabilities = "Capabilities";
    public const string GetOrCreateGroup = "GetOrCreateGroup";
    public const string CreateGroup = "CreateGroup";
    public const string ListGroups = "ListGroups";
    public const string InspectGroup = "InspectGroup";
    public const string UpdateGroupMetadata = "UpdateGroupMetadata";
    public const string DeleteGroup = "DeleteGroup";
    public const string ForkGroup = "ForkGroup";
    public const string ReplacePrimarySandbox = "ReplacePrimarySandbox";
    public const string StartSandbox = "StartSandbox";
    public const string StopSandbox = "StopSandbox";
    public const string Exec = "Exec";
    public const string FileRead = "FileRead";
    public const string FileWrite = "FileWrite";
    public const string FileList = "FileList";
    public const string FileSearch = "FileSearch";
    public const string Diagnostics = "Diagnostics";
}

public static class GuestControlOperations
{
    public const string Hello = "Hello";
    public const string InstallSshKey = "InstallSshKey";
    public const string Ready = "Ready";
    public const string FlushShutdown = "FlushShutdown";
}

public sealed record ProtocolEnvelope(
    int Version,
    string RequestId,
    string Operation,
    JsonElement Payload);

public sealed record ProtocolResponse(
    int Version,
    string RequestId,
    bool Success,
    JsonElement? Payload,
    TinyCosmosError? Error);

public sealed record ProtocolEvent(
    int Version,
    string RequestId,
    string Kind,
    JsonElement Payload);

public sealed record EmptyPayload;

public sealed record CapabilityResponse(
    int ProtocolVersion,
    IReadOnlyList<string> Operations,
    IReadOnlyDictionary<string, bool> Capabilities);

public sealed record GroupCreatePayload(
    string LogicalName,
    int OwnerUid,
    string? HostWorkspacePath,
    string? GuestProjectPath,
    string ImageId,
    ResourceAllocation Resources);

public sealed record ListGroupsPayload(int OwnerUid);

public sealed record GroupTargetPayload(int OwnerUid, string GroupId);

public sealed record UpdateGroupMetadataPayload(
    int OwnerUid,
    string GroupId,
    string? NewLogicalName,
    string? HostWorkspacePath,
    string? GuestProjectPath,
    DateTimeOffset? ArchivedAt);

public sealed record ForkGroupPayload(
    int OwnerUid,
    string SourceGroupId,
    string NewLogicalName);

public sealed record ReplacePrimarySandboxPayload(
    int OwnerUid,
    string GroupId,
    string? ImageId,
    ResourceAllocation? Resources);

public sealed record SandboxTransitionPayload(int OwnerUid, string SandboxId);

public sealed record ExecPayload(
    int OwnerUid,
    string GroupId,
    string WorkingDirectory,
    IReadOnlyList<string> Command,
    int TimeoutSeconds,
    int OutputLimitBytes,
    bool PseudoTerminal = false);

public sealed record ExecResult(int ExitCode, string Stdout, string Stderr, bool Truncated);

public sealed record FileReadPayload(
    int OwnerUid,
    string GroupId,
    string Path,
    int MaxBytes);

public sealed record FileReadResult(
    string Path,
    string ContentBase64,
    bool Truncated);

public sealed record FileWritePayload(
    int OwnerUid,
    string GroupId,
    string Path,
    string ContentBase64,
    bool Overwrite,
    bool Executable);

public sealed record FileWriteResult(
    string Path,
    long BytesWritten);

public sealed record FileListPayload(
    int OwnerUid,
    string GroupId,
    string Path,
    bool Recursive,
    int MaxEntries);

public sealed record FileEntry(
    string Path,
    bool IsDirectory,
    long Length,
    bool Executable,
    DateTimeOffset? ModifiedAt);

public sealed record FileListResult(
    string Path,
    IReadOnlyList<FileEntry> Entries,
    bool Truncated);

public sealed record FileSearchPayload(
    int OwnerUid,
    string GroupId,
    string Path,
    string Pattern,
    int MaxMatches);

public sealed record FileSearchMatch(
    string Path,
    int LineNumber,
    string Line);

public sealed record FileSearchResult(
    IReadOnlyList<FileSearchMatch> Matches,
    bool Truncated);

public sealed record DiagnosticCheckPayload(
    string Name,
    bool Passed,
    string Message,
    string? Remediation);

public sealed record ManagerDiagnosticsResponse(
    bool Passed,
    IReadOnlyList<DiagnosticCheckPayload> Checks);

public sealed record GuestHelloRequest(
    string SandboxId,
    string BootNonce,
    int ProtocolVersion,
    string ManagerVersion);

public sealed record GuestHelloResponse(
    string SandboxId,
    string BootNonce,
    int ProtocolVersion,
    string SupervisorVersion,
    IReadOnlyList<string> Capabilities);

public sealed record GuestSshKeyRequest(
    string BootNonce,
    string PublicKey,
    string Comment);

public sealed record GuestSshKeyResponse(
    string BootNonce,
    bool Installed,
    string AuthorizedKeysPath);

public sealed record GuestReadyReport(
    string BootNonce,
    string SshUser,
    string? SshHostKey,
    bool SudoAvailable,
    bool DockerAvailable,
    DateTimeOffset ReportedAt);

public sealed record GuestReadyRequest(string BootNonce);

public sealed record GuestShutdownRequest(
    string BootNonce,
    bool PowerOff);

public sealed record GuestShutdownResponse(
    string BootNonce,
    bool FilesystemsFlushed,
    bool ShutdownRequested);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProtocolEnvelope))]
[JsonSerializable(typeof(ProtocolResponse))]
[JsonSerializable(typeof(ProtocolEvent))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(TinyCosmosError))]
[JsonSerializable(typeof(CapabilityResponse))]
[JsonSerializable(typeof(GroupCreatePayload))]
[JsonSerializable(typeof(ListGroupsPayload))]
[JsonSerializable(typeof(GroupTargetPayload))]
[JsonSerializable(typeof(UpdateGroupMetadataPayload))]
[JsonSerializable(typeof(ForkGroupPayload))]
[JsonSerializable(typeof(ReplacePrimarySandboxPayload))]
[JsonSerializable(typeof(SandboxTransitionPayload))]
[JsonSerializable(typeof(ExecPayload))]
[JsonSerializable(typeof(ExecResult))]
[JsonSerializable(typeof(FileReadPayload))]
[JsonSerializable(typeof(FileReadResult))]
[JsonSerializable(typeof(FileWritePayload))]
[JsonSerializable(typeof(FileWriteResult))]
[JsonSerializable(typeof(FileListPayload))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(FileEntry[]))]
[JsonSerializable(typeof(FileListResult))]
[JsonSerializable(typeof(FileSearchPayload))]
[JsonSerializable(typeof(FileSearchMatch))]
[JsonSerializable(typeof(FileSearchMatch[]))]
[JsonSerializable(typeof(FileSearchResult))]
[JsonSerializable(typeof(DiagnosticCheckPayload))]
[JsonSerializable(typeof(DiagnosticCheckPayload[]))]
[JsonSerializable(typeof(ManagerDiagnosticsResponse))]
[JsonSerializable(typeof(GuestHelloRequest))]
[JsonSerializable(typeof(GuestHelloResponse))]
[JsonSerializable(typeof(GuestSshKeyRequest))]
[JsonSerializable(typeof(GuestSshKeyResponse))]
[JsonSerializable(typeof(GuestReadyRequest))]
[JsonSerializable(typeof(GuestReadyReport))]
[JsonSerializable(typeof(GuestShutdownRequest))]
[JsonSerializable(typeof(GuestShutdownResponse))]
[JsonSerializable(typeof(GroupBundle))]
[JsonSerializable(typeof(GroupBundle[]))]
[JsonSerializable(typeof(ResourceAllocation))]
[JsonSerializable(typeof(NetworkAllocation))]
[JsonSerializable(typeof(GroupMetadata))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(string[]))]
public partial class TinyCosmosJsonContext : JsonSerializerContext;

public static class ProtocolJson
{
    public static JsonElement ToElement<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.SerializeToElement(value, typeInfo);
    }

    public static T FromElement<T>(JsonElement element, JsonTypeInfo<T> typeInfo)
    {
        return element.Deserialize(typeInfo)
            ?? throw new InvalidOperationException("JSON payload deserialized to null.");
    }
}
