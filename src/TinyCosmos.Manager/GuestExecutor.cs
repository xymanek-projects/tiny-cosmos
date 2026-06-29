using TinyCosmos.Core;
using TinyCosmos.Protocol;

namespace TinyCosmos.Manager;

public interface IGuestExecutor
{
    Task<ExecResult> ExecuteAsync(GroupBundle target, ExecPayload payload, CancellationToken cancellationToken);
}

public sealed class NotReadyGuestExecutor : IGuestExecutor
{
    public Task<ExecResult> ExecuteAsync(GroupBundle target, ExecPayload payload, CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(new TinyCosmosError(
            TinyCosmosErrorCode.NotReady,
            "Guest execution requires a verified SSH data plane for the sandbox."));
    }
}

public sealed class RecordingGuestExecutor(Func<GroupBundle, ExecPayload, ExecResult> handler) : IGuestExecutor
{
    public List<ExecPayload> Requests { get; } = [];

    public Task<ExecResult> ExecuteAsync(GroupBundle target, ExecPayload payload, CancellationToken cancellationToken)
    {
        Requests.Add(payload);
        return Task.FromResult(handler(target, payload));
    }
}
