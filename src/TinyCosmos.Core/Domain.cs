using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TinyCosmos.Core;

public enum LifecycleState
{
    Preparing,
    Starting,
    Ready,
    Stopping,
    Stopped,
    Failed,
    Deleting,
    Deleted,
    Retired
}

public enum StopReason
{
    None,
    Explicit,
    Idle,
    StartupTimeout,
    HostReconcile
}

public enum TinyCosmosErrorCode
{
    None,
    InvalidRequest,
    UnsupportedOperation,
    NameConflict,
    GroupNotFound,
    SandboxNotFound,
    LifecycleConflict,
    AuthorizationFailed,
    PlatformRejected,
    ProtocolError,
    NotReady,
    DependencyMissing,
    ExportRejected
}

public sealed record TinyCosmosError(
    TinyCosmosErrorCode Code,
    string Message,
    string? Remediation = null);

public readonly record struct GroupId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct SandboxId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct OperationId(string Value)
{
    public override string ToString() => Value;
}

public static class TinyId
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static GroupId NewGroupId() => new(New("grp"));

    public static SandboxId NewSandboxId() => new(New("sbx"));

    public static OperationId NewOperationId() => new(New("op"));

    public static bool IsValid(string value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix + "_", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = prefix.Length + 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '2' && c <= '7')))
            {
                return false;
            }
        }

        return value.Length >= prefix.Length + 17;
    }

    private static string New(string prefix)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return prefix + "_" + ToBase32(bytes);
    }

    private static string ToBase32(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return output.ToString();
    }
}

public readonly record struct LogicalGroupName(string Value)
{
    public static bool TryParse(string value, out LogicalGroupName name, out string error)
    {
        name = default;
        error = string.Empty;

        if (Encoding.UTF8.GetByteCount(value) > 512)
        {
            error = "Logical group name must be at most 512 UTF-8 bytes.";
            return false;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            error = "Logical group name must use namespace:opaque form.";
            return false;
        }

        var ns = value[..separator];
        foreach (var c in ns)
        {
            if (c is < 'a' or > 'z')
            {
                error = "Logical group namespace must be lowercase ASCII letters.";
                return false;
            }
        }

        name = new LogicalGroupName(value);
        return true;
    }

    public override string ToString() => Value;
}

public sealed record GroupMetadata(
    string? HostWorkspacePath,
    string? GuestProjectPath,
    DateTimeOffset? ArchivedAt);

public sealed record ResourceAllocation(
    int Vcpu,
    long MemoryMiB,
    long SystemDiskGiB,
    long WorkspaceDiskGiB)
{
    public static ResourceAllocation Default { get; } = new(2, 4096, 12, 32);
}

public sealed record NetworkAllocation(string Cidr, string GatewayAddress, string PrimaryAddress);

public sealed record SandboxRecord(
    SandboxId SandboxId,
    GroupId GroupId,
    LifecycleState LifecycleState,
    bool IsPrimary,
    string ImageId,
    ResourceAllocation Resources,
    NetworkAllocation Network,
    string SystemDiskId,
    string WorkspaceDiskId,
    StopReason StopReason,
    string? FailureCategory,
    string? FailureGuidance,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GroupRecord(
    GroupId GroupId,
    LogicalGroupName LogicalName,
    int OwnerUid,
    GroupMetadata Metadata,
    SandboxId PrimarySandboxId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class LifecycleRules
{
    public static bool CanTransition(LifecycleState from, LifecycleState to)
    {
        if (from == to)
        {
            return true;
        }

        return from switch
        {
            LifecycleState.Preparing => to is LifecycleState.Starting or LifecycleState.Failed or LifecycleState.Deleting,
            LifecycleState.Starting => to is LifecycleState.Ready or LifecycleState.Stopped or LifecycleState.Failed or LifecycleState.Deleting,
            LifecycleState.Ready => to is LifecycleState.Stopping or LifecycleState.Failed or LifecycleState.Deleting,
            LifecycleState.Stopping => to is LifecycleState.Stopped or LifecycleState.Failed or LifecycleState.Deleting,
            LifecycleState.Stopped => to is LifecycleState.Starting or LifecycleState.Deleting or LifecycleState.Failed,
            LifecycleState.Failed => to is LifecycleState.Deleting or LifecycleState.Retired,
            LifecycleState.Deleting => to is LifecycleState.Deleted,
            LifecycleState.Deleted => false,
            LifecycleState.Retired => false,
            _ => false
        };
    }

    public static void RequireTransition(LifecycleState from, LifecycleState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Illegal lifecycle transition {from} -> {to}."));
        }
    }
}

public sealed record CreateGroupRequest(
    LogicalGroupName LogicalName,
    int OwnerUid,
    GroupMetadata Metadata,
    string ImageId,
    ResourceAllocation Resources);

public sealed record GroupBundle(GroupRecord Group, SandboxRecord PrimarySandbox);

public sealed record SandboxReconciliation(
    GroupId GroupId,
    SandboxId SandboxId,
    LifecycleState PreviousState,
    LifecycleState ReconciledState,
    StopReason StopReason);

public sealed record SandboxLease(
    SandboxId SandboxId,
    int OwnerUid,
    string LeaseOwner,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt);
