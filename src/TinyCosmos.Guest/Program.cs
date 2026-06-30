using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

var command = args.Length == 0 ? "ready" : args[0];
switch (command)
{
    case "hello" when args.Length is 2 or 3:
        WriteJson(new GuestHelloResponse(
            SandboxId: args.Length == 3 ? args[2] : "unknown",
            BootNonce: args[1],
            ProtocolVersion: ProtocolVersion.Current,
            SupervisorVersion: "tinycosmos-guest-supervisor-mvp",
            Capabilities:
            [
                GuestControlOperations.InstallSshKey,
                GuestControlOperations.Ready,
                GuestControlOperations.FlushShutdown
            ]));
        return 0;
    case "ready":
        var hostKey = File.Exists("/etc/ssh/ssh_host_ed25519_key.pub")
            ? await File.ReadAllTextAsync("/etc/ssh/ssh_host_ed25519_key.pub").ConfigureAwait(false)
            : null;
        WriteJson(new GuestReadyReport(
            BootNonce: args.Length >= 2 ? args[1] : string.Empty,
            SshUser: "agent",
            SshHostKey: hostKey?.Trim(),
            SudoAvailable: File.Exists("/usr/bin/sudo") || File.Exists("/bin/sudo"),
            DockerAvailable: File.Exists("/usr/bin/docker") || File.Exists("/bin/docker"),
            ReportedAt: DateTimeOffset.UtcNow));
        return 0;
    case "install-key" when args.Length is 2 or 3:
        var sshDir = "/home/agent/.ssh";
        Directory.CreateDirectory(sshDir);
        await File.AppendAllTextAsync(Path.Combine(sshDir, "authorized_keys"), args[1] + Environment.NewLine).ConfigureAwait(false);
        WriteJson(new GuestSshKeyResponse(
            BootNonce: args.Length == 3 ? args[2] : string.Empty,
            Installed: true,
            AuthorizedKeysPath: Path.Combine(sshDir, "authorized_keys")));
        return 0;
    case "flush-shutdown":
        WriteJson(new GuestShutdownResponse(
            BootNonce: args.Length >= 2 ? args[1] : string.Empty,
            FilesystemsFlushed: true,
            ShutdownRequested: true));
        return 0;
    case "serve-vsock" when args.Length is 2 or 3:
        if (!uint.TryParse(args[1], out var vsockPort))
        {
            Console.Error.WriteLine("serve-vsock requires an integer port.");
            return 2;
        }

        try
        {
            await RunVsockServerAsync(vsockPort, args.Length == 3 ? args[2] : "unknown").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("tinycosmos-guest: vsock supervisor failed: " + ex);
            return 1;
        }
    case "serve-tcp" when args.Length is 2 or 3:
        if (!int.TryParse(args[1], out var tcpPort))
        {
            Console.Error.WriteLine("serve-tcp requires an integer port.");
            return 2;
        }

        await RunTcpServerAsync(tcpPort, args.Length == 3 ? args[2] : "unknown").ConfigureAwait(false);
        return 0;
    case "serve" when args.Length is 3 or 4:
        if (!uint.TryParse(args[1], out var combinedVsockPort) ||
            !int.TryParse(args[2], out var combinedTcpPort))
        {
            Console.Error.WriteLine("serve requires integer vsock and tcp ports.");
            return 2;
        }

        await RunCombinedServerAsync(combinedVsockPort, combinedTcpPort, args.Length == 4 ? args[3] : "unknown").ConfigureAwait(false);
        return 0;
    case "serve-unix" when args.Length is 2 or 3:
        await RunUnixServerAsync(args[1], args.Length == 3 ? args[2] : "unknown").ConfigureAwait(false);
        return 0;
    default:
        Console.Error.WriteLine("Unknown guest supervisor command.");
        return 2;
}

static void WriteJson<T>(T value)
{
    var typeInfo = TinyCosmosJsonContext.Default.Options.GetTypeInfo(typeof(T));
    Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
}

static async Task RunVsockServerAsync(uint port, string sandboxId)
{
    Console.Error.WriteLine($"tinycosmos-guest: starting vsock supervisor on port {port}; /dev/vsock exists={File.Exists("/dev/vsock")}");
    using var socket = new Socket((AddressFamily)VsockEndPoint.AddressFamilyValue, SocketType.Stream, ProtocolType.Unspecified);
    socket.Bind(new VsockEndPoint(VsockEndPoint.CidAny, port));
    socket.Listen(16);
    Console.Error.WriteLine($"tinycosmos-guest: listening on vsock port {port}");
    await AcceptLoopAsync(socket, sandboxId).ConfigureAwait(false);
}

static async Task RunUnixServerAsync(string socketPath, string sandboxId)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(socketPath))!);
    if (File.Exists(socketPath))
    {
        File.Delete(socketPath);
    }

    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    socket.Bind(new UnixDomainSocketEndPoint(socketPath));
    socket.Listen(16);
    await AcceptLoopAsync(socket, sandboxId).ConfigureAwait(false);
}

static async Task RunTcpServerAsync(int port, string sandboxId)
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Bind(new IPEndPoint(IPAddress.Any, port));
    socket.Listen(16);
    Console.Error.WriteLine($"tinycosmos-guest: listening on tcp port {port}");
    await AcceptLoopAsync(socket, sandboxId).ConfigureAwait(false);
}

static async Task RunCombinedServerAsync(uint vsockPort, int tcpPort, string sandboxId)
{
    var tcpTask = RunTcpServerAsync(tcpPort, sandboxId);
    var vsockTask = Task.Run(async () =>
    {
        try
        {
            await RunVsockServerAsync(vsockPort, sandboxId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("tinycosmos-guest: vsock listener unavailable; tcp guest control remains active: " + ex.Message);
        }
    });

    _ = vsockTask;
    await tcpTask.ConfigureAwait(false);
}

static async Task AcceptLoopAsync(Socket socket, string sandboxId)
{
    while (true)
    {
        var accepted = await socket.AcceptAsync().ConfigureAwait(false);
        Console.Error.WriteLine("tinycosmos-guest: accepted guest control connection");
        _ = Task.Run(async () =>
        {
            using var _ = accepted;
            await using var stream = new NetworkStream(accepted, ownsSocket: false);
            await HandleGuestControlStreamAsync(stream, sandboxId).ConfigureAwait(false);
        });
    }
}

static async Task HandleGuestControlStreamAsync(Stream stream, string sandboxId)
{
    while (true)
    {
        ProtocolEnvelope envelope;
        try
        {
            envelope = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolEnvelope).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return;
        }

        var response = await DispatchGuestControlAsync(envelope, sandboxId).ConfigureAwait(false);
        await FrameCodec.WriteAsync(stream, response, TinyCosmosJsonContext.Default.ProtocolResponse).ConfigureAwait(false);
    }
}

static async Task<ProtocolResponse> DispatchGuestControlAsync(ProtocolEnvelope envelope, string sandboxId)
{
    if (envelope.Version != ProtocolVersion.Current)
    {
        return Failure(envelope, new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Unsupported guest control protocol version."));
    }

    try
    {
        return envelope.Operation switch
        {
            GuestControlOperations.Hello => Success(envelope, HandleHello(envelope.Payload, sandboxId)),
            GuestControlOperations.InstallSshKey => Success(envelope, await InstallKeyAsync(envelope.Payload).ConfigureAwait(false)),
            GuestControlOperations.Ready => Success(envelope, await ReadyAsync(envelope.Payload).ConfigureAwait(false)),
            GuestControlOperations.FlushShutdown => Success(envelope, await FlushShutdownAsync(envelope.Payload).ConfigureAwait(false)),
            _ => Failure(envelope, new TinyCosmosError(TinyCosmosErrorCode.UnsupportedOperation, "Unknown guest control operation."))
        };
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or UnauthorizedAccessException)
    {
        return Failure(envelope, new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, ex.Message));
    }
}

static GuestHelloResponse HandleHello(JsonElement payload, string sandboxId)
{
    var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.GuestHelloRequest);
    if (request.ProtocolVersion != ProtocolVersion.Current)
    {
        throw new ArgumentException("Unsupported manager protocol version.");
    }

    if (!string.Equals(sandboxId, "unknown", StringComparison.Ordinal) &&
        !string.Equals(request.SandboxId, sandboxId, StringComparison.Ordinal))
    {
        throw new ArgumentException("Sandbox id did not match guest supervisor identity.");
    }

    return new GuestHelloResponse(
        string.Equals(sandboxId, "unknown", StringComparison.Ordinal) ? request.SandboxId : sandboxId,
        request.BootNonce,
        ProtocolVersion.Current,
        "tinycosmos-guest-supervisor-mvp",
        [GuestControlOperations.InstallSshKey, GuestControlOperations.Ready, GuestControlOperations.FlushShutdown]);
}

static async Task<GuestSshKeyResponse> InstallKeyAsync(JsonElement payload)
{
    var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.GuestSshKeyRequest);
    var sshDir = "/home/agent/.ssh";
    Directory.CreateDirectory(sshDir);
    var authorizedKeys = Path.Combine(sshDir, "authorized_keys");
    await File.AppendAllTextAsync(authorizedKeys, request.PublicKey.Trim() + Environment.NewLine).ConfigureAwait(false);
    return new GuestSshKeyResponse(request.BootNonce, Installed: true, authorizedKeys);
}

static async Task<GuestReadyReport> ReadyAsync(JsonElement payload)
{
    var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.GuestReadyRequest);
    var hostKey = File.Exists("/etc/ssh/ssh_host_ed25519_key.pub")
        ? await File.ReadAllTextAsync("/etc/ssh/ssh_host_ed25519_key.pub").ConfigureAwait(false)
        : null;
    return new GuestReadyReport(
        request.BootNonce,
        "agent",
        hostKey?.Trim(),
        File.Exists("/usr/bin/sudo") || File.Exists("/bin/sudo"),
        File.Exists("/usr/bin/docker") || File.Exists("/bin/docker"),
        DateTimeOffset.UtcNow);
}

static async Task<GuestShutdownResponse> FlushShutdownAsync(JsonElement payload)
{
    var request = ProtocolJson.FromElement(payload, TinyCosmosJsonContext.Default.GuestShutdownRequest);
    var flush = await ProcessRunner.RunAsync(
        "/bin/sync",
        [],
        timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    if (flush.ExitCode != 0 || flush.TimedOut)
    {
        throw new IOException("Guest filesystem sync did not complete.");
    }

    if (request.PowerOff)
    {
        try
        {
            Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", "(sleep 0.25; /usr/bin/systemctl poweroff || /sbin/poweroff -f) >/dev/null 2>&1 &" },
                UseShellExecute = false
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("tinycosmos-guest: could not schedule poweroff: " + ex.Message);
        }
    }

    return new GuestShutdownResponse(request.BootNonce, FilesystemsFlushed: true, ShutdownRequested: true);
}

static ProtocolResponse Success<T>(ProtocolEnvelope envelope, T payload)
{
    return new ProtocolResponse(
        ProtocolVersion.Current,
        envelope.RequestId,
        true,
        JsonSerializer.SerializeToElement(payload, TinyCosmosJsonContext.Default.Options.GetTypeInfo(typeof(T))),
        null);
}

static ProtocolResponse Failure(ProtocolEnvelope envelope, TinyCosmosError error)
{
    return new ProtocolResponse(ProtocolVersion.Current, envelope.RequestId, false, null, error);
}
