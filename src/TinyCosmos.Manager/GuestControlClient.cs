using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

namespace TinyCosmos.Manager;

public interface IGuestControlClient
{
    Task<GuestControlResult> EstablishAsync(
        GroupBundle target,
        BrokerProvisioningResult provisioning,
        CancellationToken cancellationToken);

    Task<GuestShutdownResponse> FlushAndShutdownAsync(
        GroupBundle target,
        CancellationToken cancellationToken);
}

public sealed record GuestControlResult(
    GuestHelloResponse Hello,
    GuestSshKeyResponse SshKey,
    GuestReadyReport Ready);

public sealed class NotReadyGuestControlClient : IGuestControlClient
{
    public Task<GuestControlResult> EstablishAsync(
        GroupBundle target,
        BrokerProvisioningResult provisioning,
        CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(new TinyCosmosError(
            TinyCosmosErrorCode.NotReady,
            "Guest supervisor control requires a connected vsock transport for the sandbox."));
    }

    public Task<GuestShutdownResponse> FlushAndShutdownAsync(
        GroupBundle target,
        CancellationToken cancellationToken)
    {
        throw new TinyCosmosException(new TinyCosmosError(
            TinyCosmosErrorCode.NotReady,
            "Guest supervisor control requires a connected vsock transport for the sandbox."));
    }
}

public sealed record GuestControlOptions(
    int Port,
    string IdentityFile,
    string PublicKeyComment,
    int TcpPort = 41024,
    TimeSpan? VsockStartupTimeout = null,
    string? KnownHostsFile = null,
    int SshPort = 22)
{
    public static GuestControlOptions FromSshOptions(GuestSshOptions sshOptions)
    {
        return new GuestControlOptions(1024, sshOptions.IdentityFile, "tinycosmos-manager", KnownHostsFile: sshOptions.KnownHostsFile, SshPort: sshOptions.Port);
    }

    public TimeSpan EffectiveVsockStartupTimeout => VsockStartupTimeout ?? TimeSpan.FromSeconds(8);

    public string EffectiveKnownHostsFile => KnownHostsFile ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(IdentityFile))!, "known_hosts");
}

public sealed class FirecrackerVsockGuestControlClient(
    GuestControlOptions options,
    IProcessRunner? processRunner = null) : IGuestControlClient
{
    private readonly IProcessRunner _processRunner = processRunner ?? new DefaultProcessRunner();

    public async Task<GuestControlResult> EstablishAsync(
        GroupBundle target,
        BrokerProvisioningResult provisioning,
        CancellationToken cancellationToken)
    {
        var bootNonce = TinyId.NewOperationId().Value;
        var transport = GuestControlTransport.Vsock(provisioning.Launch.ValidationConfig.VsockSocketPath, target.PrimarySandbox.Network.PrimaryAddress);
        var helloRequest = new GuestHelloRequest(target.PrimarySandbox.SandboxId.Value, bootNonce, ProtocolVersion.Current, "tinycosmos-manager");
        GuestHelloResponse hello;
        try
        {
            hello = await SendAsync(
                transport,
                GuestControlOperations.Hello,
                helloRequest,
                TinyCosmosJsonContext.Default.GuestHelloResponse,
                options.EffectiveVsockStartupTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransientVsockStartup(ex) && !cancellationToken.IsCancellationRequested)
        {
            transport = GuestControlTransport.Tcp(target.PrimarySandbox.Network.PrimaryAddress, options.TcpPort);
            hello = await SendAsync(
                transport,
                GuestControlOperations.Hello,
                helloRequest,
                TinyCosmosJsonContext.Default.GuestHelloResponse,
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(hello.SandboxId, target.PrimarySandbox.SandboxId.Value, StringComparison.Ordinal) ||
            !string.Equals(hello.BootNonce, bootNonce, StringComparison.Ordinal) ||
            hello.ProtocolVersion != ProtocolVersion.Current)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Guest hello response did not match the boot request."));
        }

        var publicKey = await EnsurePublicKeyAsync(cancellationToken).ConfigureAwait(false);
        var key = await SendAsync(
            transport,
            GuestControlOperations.InstallSshKey,
            new GuestSshKeyRequest(bootNonce, publicKey, options.PublicKeyComment),
            TinyCosmosJsonContext.Default.GuestSshKeyResponse,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (!key.Installed || !string.Equals(key.BootNonce, bootNonce, StringComparison.Ordinal))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.NotReady, "Guest did not confirm SSH key enrollment."));
        }

        var ready = await SendAsync(
            transport,
            GuestControlOperations.Ready,
            new GuestReadyRequest(bootNonce),
            TinyCosmosJsonContext.Default.GuestReadyReport,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ready.BootNonce, bootNonce, StringComparison.Ordinal) ||
            !string.Equals(ready.SshUser, "agent", StringComparison.Ordinal) ||
            !ready.SudoAvailable ||
            !ready.DockerAvailable)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.NotReady, "Guest readiness report is incomplete."));
        }

        await TrustGuestHostKeyAsync(target.PrimarySandbox.Network.PrimaryAddress, ready.SshHostKey, cancellationToken).ConfigureAwait(false);
        await WaitForSshWorkspaceAsync(target, cancellationToken).ConfigureAwait(false);
        return new GuestControlResult(hello, key, ready);
    }

    public async Task<GuestShutdownResponse> FlushAndShutdownAsync(
        GroupBundle target,
        CancellationToken cancellationToken)
    {
        var bootNonce = TinyId.NewOperationId().Value;
        var request = new GuestShutdownRequest(bootNonce, PowerOff: true);
        try
        {
            return await SendAsync(
                GuestControlTransport.Vsock(BrokerCommandPlanner.HostJailerRunPath(target.PrimarySandbox.SandboxId.Value, "vsock.sock"), target.PrimarySandbox.Network.PrimaryAddress),
                GuestControlOperations.FlushShutdown,
                request,
                TinyCosmosJsonContext.Default.GuestShutdownResponse,
                options.EffectiveVsockStartupTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransientVsockStartup(ex) && !cancellationToken.IsCancellationRequested)
        {
            return await SendAsync(
                GuestControlTransport.Tcp(target.PrimarySandbox.Network.PrimaryAddress, options.TcpPort),
                GuestControlOperations.FlushShutdown,
                request,
                TinyCosmosJsonContext.Default.GuestShutdownResponse,
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        GuestControlTransport transport,
        string operation,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                return transport.Kind == GuestControlTransportKind.Vsock
                    ? await SendOnceVsockAsync(transport.VsockSocketPath!, operation, request, responseType, cancellationToken).ConfigureAwait(false)
                    : await SendOnceTcpAsync(transport.TcpHost!, transport.TcpPort, operation, request, responseType, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientVsockStartup(ex) && DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<TResponse> SendOnceTcpAsync<TRequest, TResponse>(
        string host,
        int port,
        string operation,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        return await SendFramedAsync(stream, operation, request, responseType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendOnceVsockAsync<TRequest, TResponse>(
        string vsockSocketPath,
        string operation,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(vsockSocketPath), cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await WriteFirecrackerConnectAsync(stream, cancellationToken).ConfigureAwait(false);
        return await SendFramedAsync(stream, operation, request, responseType, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TResponse> SendFramedAsync<TRequest, TResponse>(
        Stream stream,
        string operation,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        var envelope = new ProtocolEnvelope(
            ProtocolVersion.Current,
            TinyId.NewOperationId().Value,
            operation,
            JsonSerializer.SerializeToElement(request, TinyCosmosJsonContext.Default.Options.GetTypeInfo(typeof(TRequest))));
        await FrameCodec.WriteAsync(stream, envelope, TinyCosmosJsonContext.Default.ProtocolEnvelope, cancellationToken).ConfigureAwait(false);
        var response = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolResponse, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new TinyCosmosException(response.Error ?? new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Guest supervisor returned an unknown error."));
        }

        if (response.Payload is not { } payload)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Guest supervisor returned an empty payload."));
        }

        return payload.Deserialize(responseType)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Guest supervisor payload deserialized to null."));
    }

    private async Task<string> EnsurePublicKeyAsync(CancellationToken cancellationToken)
    {
        var publicKeyPath = options.IdentityFile + ".pub";
        if (!File.Exists(publicKeyPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.IdentityFile))!);
            var result = await _processRunner.RunAsync(
                "/usr/bin/ssh-keygen",
                ["-t", "ed25519", "-N", string.Empty, "-C", options.PublicKeyComment, "-f", options.IdentityFile],
                null,
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new TinyCosmosException(new TinyCosmosError(
                    TinyCosmosErrorCode.PlatformRejected,
                    "Could not create manager SSH keypair.",
                    OutputSanitizer.SanitizeStructuredText(result.Stderr)));
            }
        }

        return (await File.ReadAllTextAsync(publicKeyPath, cancellationToken).ConfigureAwait(false)).Trim();
    }

    private async Task TrustGuestHostKeyAsync(string host, string? hostKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostKey))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.NotReady, "Guest did not report an SSH host key."));
        }

        var marker = options.SshPort == 22
            ? host
            : "[" + host + "]:" + options.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var line = marker + " " + hostKey.Trim();
        var knownHostsFile = options.EffectiveKnownHostsFile;
        var knownHostsDirectory = Path.GetDirectoryName(Path.GetFullPath(knownHostsFile))!;
        Directory.CreateDirectory(knownHostsDirectory);
        var existing = File.Exists(knownHostsFile)
            ? await File.ReadAllLinesAsync(knownHostsFile, cancellationToken).ConfigureAwait(false)
            : [];
        var retained = existing
            .Where(existingLine => !existingLine.StartsWith(marker + " ", StringComparison.Ordinal))
            .Append(line);
        await File.WriteAllLinesAsync(knownHostsFile, retained, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForSshWorkspaceAsync(GroupBundle target, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        ProcessRunResult? last = null;
        var sshTarget = new SshTarget(
            target.PrimarySandbox.Network.PrimaryAddress,
            options.SshPort,
            "agent",
            options.IdentityFile,
            options.EffectiveKnownHostsFile,
            ControlPath: null);
        var request = new SshExecRequest(
            sshTarget,
            "/",
            [
                "/bin/sh",
                "-lc",
                "/usr/bin/mountpoint -q /workspace/project || /usr/bin/sudo /usr/bin/mount -t ext4 LABEL=tinycosmos-workspace /workspace/project; /usr/bin/mountpoint -q /workspace/project && /usr/bin/test -w /workspace/project"
            ],
            TimeoutSeconds: 5,
            OutputLimitBytes: 4096);
        var plan = SshCommandPlanner.PlanExec(request);

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            last = await _processRunner.RunAsync(
                plan.FileName,
                plan.Arguments,
                null,
                TimeSpan.FromSeconds(request.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            if (!last.TimedOut && last.ExitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        var detail = last is null
            ? "probe did not run"
            : $"last exit code {last.ExitCode}" + (string.IsNullOrWhiteSpace(last.Stderr) ? string.Empty : ": " + last.Stderr.Trim());
        throw new TinyCosmosException(new TinyCosmosError(
            TinyCosmosErrorCode.NotReady,
            "Guest SSH workspace did not become ready.",
            detail));
    }

    private static bool IsTransientVsockStartup(Exception ex)
    {
        return ex is SocketException or IOException or EndOfStreamException ||
            ex is TinyCosmosException { Error.Code: TinyCosmosErrorCode.NotReady };
    }

    private async Task WriteFirecrackerConnectAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {options.Port}\n"), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var line = await ReadLineAsciiAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!line.StartsWith("OK", StringComparison.Ordinal))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.NotReady, "Firecracker vsock endpoint rejected guest control connection: " + line));
        }
    }

    private static async Task<string> ReadLineAsciiAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < 256)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Firecracker vsock endpoint closed during handshake.");
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            bytes.Add(buffer[0]);
        }

        throw new InvalidDataException("Firecracker vsock handshake response was too long.");
    }
}

internal enum GuestControlTransportKind
{
    Vsock,
    Tcp
}

internal sealed record GuestControlTransport(
    GuestControlTransportKind Kind,
    string? VsockSocketPath,
    string? TcpHost,
    int TcpPort)
{
    public static GuestControlTransport Vsock(string socketPath, string tcpHost) =>
        new(GuestControlTransportKind.Vsock, socketPath, tcpHost, 0);

    public static GuestControlTransport Tcp(string host, int port) =>
        new(GuestControlTransportKind.Tcp, null, host, port);
}

public sealed class RecordingGuestControlClient : IGuestControlClient
{
    private readonly TinyCosmosError? _startupError;

    public RecordingGuestControlClient(TinyCosmosError? startupError = null)
    {
        _startupError = startupError;
    }

    public List<string> Operations { get; } = [];

    public List<GuestHelloRequest> HelloRequests { get; } = [];

    public List<GuestSshKeyRequest> SshKeyRequests { get; } = [];

    public List<GuestShutdownRequest> ShutdownRequests { get; } = [];

    public Task<GuestControlResult> EstablishAsync(
        GroupBundle target,
        BrokerProvisioningResult provisioning,
        CancellationToken cancellationToken)
    {
        if (_startupError is not null)
        {
            throw new TinyCosmosException(_startupError);
        }

        var bootNonce = TinyId.NewOperationId().Value;
        var hello = new GuestHelloRequest(
            target.PrimarySandbox.SandboxId.Value,
            bootNonce,
            ProtocolVersion.Current,
            "tinycosmos-manager-test");
        HelloRequests.Add(hello);
        Operations.Add(GuestControlOperations.Hello);

        var key = new GuestSshKeyRequest(
            bootNonce,
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeTinyCosmosManagerKey tinycosmos@test",
            "tinycosmos-manager");
        SshKeyRequests.Add(key);
        Operations.Add(GuestControlOperations.InstallSshKey);

        Operations.Add(GuestControlOperations.Ready);
        return Task.FromResult(new GuestControlResult(
            new GuestHelloResponse(
                target.PrimarySandbox.SandboxId.Value,
                bootNonce,
                ProtocolVersion.Current,
                "tinycosmos-guest-test",
                [GuestControlOperations.InstallSshKey, GuestControlOperations.Ready, GuestControlOperations.FlushShutdown]),
            new GuestSshKeyResponse(bootNonce, true, "/home/agent/.ssh/authorized_keys"),
            new GuestReadyReport(
                bootNonce,
                "agent",
                "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeTinyCosmosHostKey",
                true,
                true,
                DateTimeOffset.UnixEpoch)));
    }

    public Task<GuestShutdownResponse> FlushAndShutdownAsync(
        GroupBundle target,
        CancellationToken cancellationToken)
    {
        var request = new GuestShutdownRequest(TinyId.NewOperationId().Value, PowerOff: true);
        ShutdownRequests.Add(request);
        Operations.Add(GuestControlOperations.FlushShutdown);
        return Task.FromResult(new GuestShutdownResponse(request.BootNonce, FilesystemsFlushed: true, ShutdownRequested: true));
    }
}
