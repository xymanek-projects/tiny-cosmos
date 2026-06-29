using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

namespace TinyCosmos.Broker;

[SupportedOSPlatform("linux")]
public sealed class BrokerProtocolServer(string socketPath, bool dryRunExecution = false)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var socket = CreateListeningSocket(socketPath);

        while (!cancellationToken.IsCancellationRequested)
        {
            var accepted = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => HandleClientAsync(accepted, cancellationToken), CancellationToken.None);
        }
    }

    private static Socket CreateListeningSocket(string socketPath)
    {
        if (Environment.GetEnvironmentVariable("LISTEN_FDS") == "1" &&
            Environment.GetEnvironmentVariable("LISTEN_PID") == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return new Socket(new SafeSocketHandle(new IntPtr(3), ownsHandle: true));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(socketPath))!);
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        socket.Listen(128);
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        return socket;
    }

    public async Task<ProtocolResponse> DispatchAsync(ProtocolEnvelope envelope, PeerCredentials credentials, CancellationToken cancellationToken = default)
    {
        try
        {
            return envelope.Operation switch
            {
                BrokerOperationNames.PlanStorage => Success(envelope, BrokerCommandPlanner.PlanStorage(FromPayload(envelope.Payload, LinuxJsonContext.Default.BrokerStorageRequest))),
                BrokerOperationNames.PlanNetwork => Success(envelope, BrokerCommandPlanner.PlanNetwork(FromPayload(envelope.Payload, LinuxJsonContext.Default.BrokerNetworkRequest))),
                BrokerOperationNames.PlanLaunch => Success(envelope, BrokerCommandPlanner.PlanLaunch(FromPayload(envelope.Payload, LinuxJsonContext.Default.BrokerLaunchRequest))),
                BrokerOperationNames.PlanStop => Success(envelope, BrokerCommandPlanner.PlanStop(FromPayload(envelope.Payload, LinuxJsonContext.Default.BrokerStopRequest))),
                BrokerOperationNames.PlanCleanup => Success(envelope, BrokerCommandPlanner.PlanCleanup(FromPayload(envelope.Payload, LinuxJsonContext.Default.BrokerCleanupRequest))),
                BrokerOperationNames.ExecutePlan => Success(envelope, (await BrokerCommandExecutor.ExecuteAsync(
                    FromPayload(envelope.Payload, LinuxJsonContext.Default.PlannedCommandArray),
                    dryRun: dryRunExecution,
                    cancellationToken).ConfigureAwait(false)).ToArray()),
                _ => Failure(envelope, new TinyCosmosError(TinyCosmosErrorCode.UnsupportedOperation, "Unknown broker operation."))
            };
        }
        catch (TinyCosmosException ex)
        {
            return Failure(envelope, ex.Error);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            return Failure(envelope, new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, ex.Message));
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var ownedSocket = socket;
        var credentials = LinuxInterop.GetPeerCredentials((int)ownedSocket.Handle);
        await using var stream = new NetworkStream(ownedSocket, ownsSocket: false);
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

            var response = await DispatchAsync(envelope, credentials, cancellationToken).ConfigureAwait(false);
            await FrameCodec.WriteAsync(stream, response, TinyCosmosJsonContext.Default.ProtocolResponse, cancellationToken).ConfigureAwait(false);
        }
    }

    private static T FromPayload<T>(JsonElement payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        return payload.Deserialize(typeInfo)
            ?? throw new InvalidOperationException("Broker payload deserialized to null.");
    }

    private static ProtocolResponse Success<T>(ProtocolEnvelope envelope, T payload)
    {
        return new ProtocolResponse(
            ProtocolVersion.Current,
            envelope.RequestId,
            true,
            JsonSerializer.SerializeToElement(payload, LinuxJsonContext.Default.Options.GetTypeInfo(typeof(T))),
            null);
    }

    private static ProtocolResponse Failure(ProtocolEnvelope envelope, TinyCosmosError error)
    {
        return new ProtocolResponse(ProtocolVersion.Current, envelope.RequestId, false, null, error);
    }
}
