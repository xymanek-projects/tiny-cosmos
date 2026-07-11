using TinyCosmos.Core;
using TinyCosmos.Manager;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("linux")]

var socketPath = GetOption(args, "--socket") ?? Path.Combine(
    Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
    "tiny-cosmos",
    "manager.sock");
var statePath = GetOption(args, "--state") ?? ManagerPaths.DefaultStatePath();
var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(statePath))!;
var brokerSocketPath = GetOption(args, "--broker-socket") ?? "/run/tiny-cosmos/broker.sock";
var idleStopMinutes = double.TryParse(GetOption(args, "--idle-stop-minutes"), out var configuredIdleStopMinutes)
    ? configuredIdleStopMinutes
    : 30;
var sshDefaults = GuestSshOptions.CreateDefault(stateDirectory);
var sshOptions = sshDefaults with
{
    User = GetOption(args, "--ssh-user") ?? sshDefaults.User,
    Port = int.TryParse(GetOption(args, "--ssh-port"), out var sshPort) ? sshPort : sshDefaults.Port,
    IdentityFile = GetOption(args, "--ssh-identity") ?? sshDefaults.IdentityFile,
    KnownHostsFile = GetOption(args, "--ssh-known-hosts") ?? sshDefaults.KnownHostsFile,
    ControlDirectory = GetOption(args, "--ssh-control-dir") ?? sshDefaults.ControlDirectory,
    TempDirectory = GetOption(args, "--guest-io-temp") ?? sshDefaults.TempDirectory
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

Console.Error.WriteLine($"tinycosmos-manager listening on {socketPath}");
var server = new ManagerProtocolServer(
    socketPath,
    new StateStore(statePath),
    new UnixBrokerClient(brokerSocketPath),
    guestExecutor: new OpenSshGuestExecutor(sshOptions),
    guestControlClient: new FirecrackerVsockGuestControlClient(GuestControlOptions.FromSshOptions(sshOptions)),
    guestFileService: new OpenSftpGuestFileService(sshOptions),
    idleStopAfter: idleStopMinutes <= 0 ? null : TimeSpan.FromMinutes(idleStopMinutes));
await server.RunAsync(cts.Token).ConfigureAwait(false);

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
        {
            return args[i + 1];
        }
    }

    return null;
}
