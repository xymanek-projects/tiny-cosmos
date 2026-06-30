using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Manager;
using TinyCosmos.Protocol;

namespace TinyCosmos.Integration.Tests;

public sealed class GuestControlTransportTests
{
    [Fact]
    public async Task FirecrackerVsockGuestControlClientUsesConnectHandshakeAndFramedProtocol()
    {
        using var temp = new TempDir();
        var vsockPath = Path.Combine(temp.Path, "vsock.sock");
        var identity = Path.Combine(temp.Path, "id_ed25519");
        await File.WriteAllTextAsync(identity, "private");
        await File.WriteAllTextAsync(identity + ".pub", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITest tinycosmos@test\n");
        var group = Group();
        var server = RunFakeFirecrackerVsockAsync(vsockPath, group.PrimarySandbox.SandboxId.Value, expectedConnections: 3);
        var runner = new RecordingProcessRunner((fileName, _, _, _) =>
            new ProcessRunResult(fileName == "/usr/bin/ssh" ? 0 : 1, string.Empty, string.Empty, TimedOut: false));
        var client = new FirecrackerVsockGuestControlClient(new GuestControlOptions(1024, identity, "tinycosmos-test"), runner);

        var result = await client.EstablishAsync(group, Provisioning(vsockPath), CancellationToken.None);

        Assert.Equal(group.PrimarySandbox.SandboxId.Value, result.Hello.SandboxId);
        Assert.Equal("agent", result.Ready.SshUser);
        Assert.True(result.Ready.SudoAvailable);
        Assert.True(result.Ready.DockerAvailable);
        var observed = await server;
        Assert.Equal(
            [GuestControlOperations.Hello, GuestControlOperations.InstallSshKey, GuestControlOperations.Ready],
            observed.Operations);
        Assert.All(observed.ConnectLines, line => Assert.Equal("CONNECT 1024", line));
        Assert.Contains(
            "172.31.42.2 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHostKey",
            await File.ReadAllTextAsync(Path.Combine(temp.Path, "known_hosts")));
        var probe = Assert.Single(runner.Calls);
        Assert.Equal("/usr/bin/ssh", probe.FileName);
        Assert.Contains("agent@172.31.42.2", probe.Arguments);
        Assert.DoesNotContain(probe.Arguments, argument => argument.StartsWith("ControlPath=", StringComparison.Ordinal));
        Assert.Contains("/usr/bin/mountpoint -q /workspace/project", probe.Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirecrackerVsockGuestControlClientFallsBackToTcpGuestControl()
    {
        using var temp = new TempDir();
        var identity = Path.Combine(temp.Path, "id_ed25519");
        await File.WriteAllTextAsync(identity, "private");
        await File.WriteAllTextAsync(identity + ".pub", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITest tinycosmos@test\n");
        var group = Group(primaryAddress: "127.0.0.1");
        using var portReservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        portReservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var tcpPort = ((IPEndPoint)portReservation.LocalEndPoint!).Port;
        portReservation.Close();
        var server = RunFakeTcpGuestControlAsync(tcpPort, group.PrimarySandbox.SandboxId.Value, expectedConnections: 3);
        var runner = new RecordingProcessRunner((fileName, _, _, _) =>
            new ProcessRunResult(fileName == "/usr/bin/ssh" ? 0 : 1, string.Empty, string.Empty, TimedOut: false));
        var client = new FirecrackerVsockGuestControlClient(new GuestControlOptions(
            1024,
            identity,
            "tinycosmos-test",
            tcpPort,
            TimeSpan.FromMilliseconds(50)),
            runner);

        var result = await client.EstablishAsync(group, Provisioning(Path.Combine(temp.Path, "missing-vsock.sock")), CancellationToken.None);

        Assert.Equal(group.PrimarySandbox.SandboxId.Value, result.Hello.SandboxId);
        Assert.Equal("agent", result.Ready.SshUser);
        Assert.Equal(
            [GuestControlOperations.Hello, GuestControlOperations.InstallSshKey, GuestControlOperations.Ready],
            await server);
        Assert.Contains(
            "127.0.0.1 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHostKey",
            await File.ReadAllTextAsync(Path.Combine(temp.Path, "known_hosts")));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task GuestSupervisorServeUnixAdoptsSandboxIdFromFramedHelloWhenBootIdentityIsUnknown()
    {
        using var temp = new TempDir();
        var socketPath = Path.Combine(temp.Path, "guest.sock");
        var sandboxId = "sbx_guestcontroltest";
        var guestExecutable = FindGuestExecutable();
        var start = new System.Diagnostics.ProcessStartInfo(guestExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("serve-unix");
        start.ArgumentList.Add(socketPath);
        start.ArgumentList.Add("unknown");
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start guest supervisor.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            for (var i = 0; i < 100 && !File.Exists(socketPath) && !process.HasExited; i++)
            {
                await Task.Delay(50);
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await ConnectWithRetryAsync(socket, socketPath, process, stdoutTask, stderrTask);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var ioTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var envelope = new ProtocolEnvelope(
                ProtocolVersion.Current,
                TinyId.NewOperationId().Value,
                GuestControlOperations.Hello,
                JsonSerializer.SerializeToElement(
                    new GuestHelloRequest(sandboxId, "op_bootnonce", ProtocolVersion.Current, "test-manager"),
                    TinyCosmosJsonContext.Default.GuestHelloRequest));

            await FrameCodec.WriteAsync(stream, envelope, TinyCosmosJsonContext.Default.ProtocolEnvelope, ioTimeout.Token);
            var response = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolResponse, ioTimeout.Token);

            Assert.True(response.Success, response.Error?.Message);
            var hello = ProtocolJson.FromElement(response.Payload!.Value, TinyCosmosJsonContext.Default.GuestHelloResponse);
            Assert.Equal(sandboxId, hello.SandboxId);
            Assert.Equal("op_bootnonce", hello.BootNonce);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task ConnectWithRetryAsync(
        Socket socket,
        string socketPath,
        System.Diagnostics.Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        Exception? last = null;
        for (var i = 0; i < 100; i++)
        {
            if (process.HasExited)
            {
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                throw new InvalidOperationException($"Guest supervisor exited early with {process.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
            }

            try
            {
                await socket.ConnectAsync(endpoint);
                return;
            }
            catch (SocketException ex)
            {
                last = ex;
                await Task.Delay(50);
            }
        }

        throw new InvalidOperationException("Could not connect to guest supervisor Unix socket.", last);
    }

    private static async Task<FakeVsockObservation> RunFakeFirecrackerVsockAsync(string socketPath, string sandboxId, int expectedConnections)
    {
        var operations = new List<string>();
        var connectLines = new List<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(16);
        for (var i = 0; i < expectedConnections; i++)
        {
            using var accepted = await listener.AcceptAsync();
            await using var stream = new NetworkStream(accepted, ownsSocket: false);
            connectLines.Add(await ReadLineAsciiAsync(stream));
            await stream.WriteAsync("OK 1073741824\n"u8.ToArray());
            await stream.FlushAsync();
            var envelope = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolEnvelope);
            operations.Add(envelope.Operation);
            var response = FakeGuestResponse(envelope, sandboxId);
            await FrameCodec.WriteAsync(stream, response, TinyCosmosJsonContext.Default.ProtocolResponse);
        }

        return new FakeVsockObservation(connectLines, operations);
    }

    private static async Task<IReadOnlyList<string>> RunFakeTcpGuestControlAsync(int port, string sandboxId, int expectedConnections)
    {
        var operations = new List<string>();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(16);
        for (var i = 0; i < expectedConnections; i++)
        {
            using var accepted = await listener.AcceptAsync();
            await using var stream = new NetworkStream(accepted, ownsSocket: false);
            var envelope = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolEnvelope);
            operations.Add(envelope.Operation);
            await FrameCodec.WriteAsync(stream, FakeGuestResponse(envelope, sandboxId), TinyCosmosJsonContext.Default.ProtocolResponse);
        }

        return operations;
    }

    private static ProtocolResponse FakeGuestResponse(ProtocolEnvelope envelope, string sandboxId)
    {
        return envelope.Operation switch
        {
            GuestControlOperations.Hello => Success(envelope, new GuestHelloResponse(
                sandboxId,
                ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.GuestHelloRequest).BootNonce,
                ProtocolVersion.Current,
                "fake-guest",
                [GuestControlOperations.InstallSshKey, GuestControlOperations.Ready, GuestControlOperations.FlushShutdown])),
            GuestControlOperations.InstallSshKey => Success(envelope, new GuestSshKeyResponse(
                ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.GuestSshKeyRequest).BootNonce,
                Installed: true,
                "/home/agent/.ssh/authorized_keys")),
            GuestControlOperations.Ready => Success(envelope, new GuestReadyReport(
                ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.GuestReadyRequest).BootNonce,
                "agent",
                "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHostKey",
                SudoAvailable: true,
                DockerAvailable: true,
                DateTimeOffset.UnixEpoch)),
            _ => new ProtocolResponse(
                ProtocolVersion.Current,
                envelope.RequestId,
                Success: false,
                Payload: null,
                new TinyCosmosError(TinyCosmosErrorCode.UnsupportedOperation, "unexpected operation"))
        };
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

    private static async Task<string> ReadLineAsciiAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            bytes.Add(buffer[0]);
        }
    }

    private static GroupBundle Group(string primaryAddress = "172.31.42.2")
    {
        var groupId = new GroupId("grp_guestcontroltest");
        var sandboxId = new SandboxId("sbx_guestcontroltest");
        return new GroupBundle(
            new GroupRecord(groupId, new LogicalGroupName("opencode:guest-control"), 1000, new GroupMetadata(null, "/workspace/project", null), sandboxId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new SandboxRecord(
                sandboxId,
                groupId,
                LifecycleState.Starting,
                IsPrimary: true,
                "image",
                ResourceAllocation.Default,
                new NetworkAllocation("172.31.42.0/24", "172.31.42.1", primaryAddress),
                "system",
                "workspace",
                StopReason.None,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch));
    }

    private static BrokerProvisioningResult Provisioning(string vsockPath)
    {
        var config = new FirecrackerVmConfig(
            "/usr/lib/tiny-cosmos/bin/firecracker",
            "/usr/lib/tiny-cosmos/bin/jailer",
            "/usr/lib/tiny-cosmos/images/kernel",
            null,
            "/var/lib/tiny-cosmos/groups/grp_guestcontroltest/sandboxes/sbx_guestcontroltest/system.ext4",
            "/var/lib/tiny-cosmos/groups/grp_guestcontroltest/sandboxes/sbx_guestcontroltest/workspace.ext4",
            "/run/tiny-cosmos/sandboxes/sbx_guestcontroltest/api.sock",
            vsockPath,
            2,
            4096);
        return new BrokerProvisioningResult(
            new BrokerStoragePlan(config.RootDrivePath, config.WorkspaceDrivePath, []),
            new BrokerNetworkPlan("tc", "br", "tap", "host", "guest", []),
            new BrokerLaunchPlan(config, "{}", [], []),
            []);
    }

    private static string FindGuestExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "TinyCosmos.Guest", "bin", "Debug", "net10.0", "linux-x64", "TinyCosmos.Guest");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate TinyCosmos.Guest executable from the test output directory.");
    }

    private sealed record FakeVsockObservation(IReadOnlyList<string> ConnectLines, IReadOnlyList<string> Operations);

    private sealed class RecordingProcessRunner(Func<string, IReadOnlyList<string>, string?, TimeSpan?, ProcessRunResult> handler) : IProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            var capturedArguments = arguments.ToArray();
            Calls.Add(new ProcessCall(fileName, capturedArguments, workingDirectory, timeout));
            return Task.FromResult(handler(fileName, capturedArguments, workingDirectory, timeout));
        }
    }

    private sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory, TimeSpan? Timeout);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-guest-control-" + Guid.NewGuid().ToString("N"));

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
