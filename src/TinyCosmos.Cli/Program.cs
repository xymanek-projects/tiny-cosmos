using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

var exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        PrintHelp();
        return 0;
    }

    try
    {
        return args[0] switch
        {
            "doctor" => Doctor(),
            "diagnostics" => await SendAndPrintAsync(ManagerOperations.Diagnostics, new EmptyPayload(), args).ConfigureAwait(false),
            "capabilities" => await SendAndPrintAsync(ManagerOperations.Capabilities, new EmptyPayload(), args).ConfigureAwait(false),
            "group" => await GroupAsync(args[1..]).ConfigureAwait(false),
            "sandbox" => await SandboxAsync(args[1..]).ConfigureAwait(false),
            "exec" => await ExecAsync(args[1..]).ConfigureAwait(false),
            "pty" => await ExecAsync(args[1..], forcePty: true).ConfigureAwait(false),
            "file" => await FileAsync(args[1..]).ConfigureAwait(false),
            "seed" => await SeedAsync(args[1..]).ConfigureAwait(false),
            "export" => Export(args[1..]),
            "vscode" => await VscodeAsync(args[1..]).ConfigureAwait(false),
            "setup" => await SetupAsync(args[1..]).ConfigureAwait(false),
            "cleanup" => await CleanupAsync(args[1..]).ConfigureAwait(false),
            _ => UsageError("Unknown command.")
        };
    }
    catch (TinyCosmosException ex)
    {
        Console.Error.WriteLine($"{ex.Error.Code}: {ex.Error.Message}");
        if (!string.IsNullOrWhiteSpace(ex.Error.Remediation))
        {
            Console.Error.WriteLine(ex.Error.Remediation);
        }

        return 2;
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or SocketException or InvalidOperationException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

static int Doctor()
{
    var report = HostDiagnostics.Run();
    foreach (var check in report.Checks)
    {
        Console.WriteLine($"{(check.Passed ? "ok" : "fail")} {check.Name}: {check.Message}");
        if (!check.Passed && !string.IsNullOrWhiteSpace(check.Remediation))
        {
            Console.WriteLine($"  remediation: {check.Remediation}");
        }
    }

    return report.Passed ? 0 : 1;
}

static async Task<int> GroupAsync(string[] args)
{
    if (args.Length == 0)
    {
        return UsageError("Missing group subcommand.");
    }

    var uid = await CurrentUidAsync().ConfigureAwait(false);
    return args[0] switch
    {
        "get-or-create" => await SendAndPrintAsync(ManagerOperations.GetOrCreateGroup, CreatePayload(args, uid), args).ConfigureAwait(false),
        "create" => await SendAndPrintAsync(ManagerOperations.CreateGroup, CreatePayload(args, uid), args).ConfigureAwait(false),
        "list" => await SendAndPrintAsync(ManagerOperations.ListGroups, new ListGroupsPayload(uid), args).ConfigureAwait(false),
        "inspect" when args.Length >= 2 => await SendAndPrintAsync(ManagerOperations.InspectGroup, new GroupTargetPayload(uid, args[1]), args).ConfigureAwait(false),
        "delete" when args.Length >= 2 => await SendAndPrintAsync(ManagerOperations.DeleteGroup, new GroupTargetPayload(uid, args[1]), args).ConfigureAwait(false),
        "fork" when args.Length >= 3 => await SendAndPrintAsync(
            ManagerOperations.ForkGroup,
            new ForkGroupPayload(uid, args[1], args[2]),
            args).ConfigureAwait(false),
        "replace" when args.Length >= 2 => await SendAndPrintAsync(
            ManagerOperations.ReplacePrimarySandbox,
            new ReplacePrimarySandboxPayload(uid, args[1], GetOption(args, "--image"), null),
            args).ConfigureAwait(false),
        "update" when args.Length >= 2 => await SendAndPrintAsync(
            ManagerOperations.UpdateGroupMetadata,
            new UpdateGroupMetadataPayload(
                uid,
                args[1],
                GetOption(args, "--name"),
                GetOption(args, "--workspace"),
                GetOption(args, "--guest-path"),
                GetOption(args, "--archived-at") is { } archivedAt ? DateTimeOffset.Parse(archivedAt) : null),
            args).ConfigureAwait(false),
        _ => UsageError("Invalid group command.")
    };
}

static GroupCreatePayload CreatePayload(string[] args, int uid)
{
    if (args.Length < 2)
    {
        throw new ArgumentException("Group command requires a logical group name.");
    }

    return new GroupCreatePayload(
        args[1],
        uid,
        GetOption(args, "--workspace"),
        GetOption(args, "--guest-path") ?? "/workspace/project",
        GetOption(args, "--image") ?? "ubuntu-24.04-dev",
        ResourceAllocation.Default);
}

static async Task<int> SandboxAsync(string[] args)
{
    if (args.Length < 2)
    {
        return UsageError("Usage: tinycosmos sandbox <start|stop> <sandbox-id>");
    }

    var uid = await CurrentUidAsync().ConfigureAwait(false);
    var operation = args[0] switch
    {
        "start" => ManagerOperations.StartSandbox,
        "stop" => ManagerOperations.StopSandbox,
        _ => throw new ArgumentException("Sandbox command must be start or stop.")
    };
    return await SendAndPrintAsync(operation, new SandboxTransitionPayload(uid, args[1]), args).ConfigureAwait(false);
}

static async Task<int> ExecAsync(string[] args, bool forcePty = false)
{
    var separator = Array.IndexOf(args, "--");
    if (args.Length < 2 || separator < 0 || separator == args.Length - 1)
    {
        return UsageError("Usage: tinycosmos exec [--pty] <group-id> -- <command> [args...]");
    }

    var groupId = args[0] == "--pty" && args.Length > 1 ? args[1] : args[0];
    if (groupId.StartsWith("--", StringComparison.Ordinal))
    {
        return UsageError("Exec command requires a group id before --.");
    }

    var uid = await CurrentUidAsync().ConfigureAwait(false);
    var payload = new ExecPayload(
        uid,
        groupId,
        GetOption(args, "--cwd") ?? "/workspace/project",
        args[(separator + 1)..],
        int.TryParse(GetOption(args, "--timeout"), out var timeout) ? timeout : 30,
        int.TryParse(GetOption(args, "--output-limit"), out var limit) ? limit : 1024 * 1024,
        forcePty || GetFlag(args, "--pty"));
    return await SendAndPrintAsync(ManagerOperations.Exec, payload, args).ConfigureAwait(false);
}

static async Task<int> FileAsync(string[] args)
{
    if (args.Length < 3)
    {
        return UsageError("Usage: tinycosmos file <read|write|list|search> <group-id> <path> [...]");
    }

    var uid = await CurrentUidAsync().ConfigureAwait(false);
    return args[0] switch
    {
        "read" => await SendAndPrintAsync(
            ManagerOperations.FileRead,
            new FileReadPayload(
                uid,
                args[1],
                args[2],
                int.TryParse(GetOption(args, "--max-bytes"), out var maxBytes) ? maxBytes : 1024 * 1024),
            args).ConfigureAwait(false),
        "write" => await SendAndPrintAsync(
            ManagerOperations.FileWrite,
            new FileWritePayload(
                uid,
                args[1],
                args[2],
                Convert.ToBase64String(Encoding.UTF8.GetBytes(GetOption(args, "--text") ?? await Console.In.ReadToEndAsync().ConfigureAwait(false))),
                GetFlag(args, "--overwrite"),
                GetFlag(args, "--executable")),
            args).ConfigureAwait(false),
        "list" => await SendAndPrintAsync(
            ManagerOperations.FileList,
            new FileListPayload(
                uid,
                args[1],
                args[2],
                GetFlag(args, "--recursive"),
                int.TryParse(GetOption(args, "--max-entries"), out var maxEntries) ? maxEntries : 1000),
            args).ConfigureAwait(false),
        "search" when args.Length >= 4 => await SendAndPrintAsync(
            ManagerOperations.FileSearch,
            new FileSearchPayload(
                uid,
                args[1],
                args[2],
                args[3],
                int.TryParse(GetOption(args, "--max-matches"), out var maxMatches) ? maxMatches : 1000),
            args).ConfigureAwait(false),
        _ => UsageError("Invalid file command.")
    };
}

static async Task<int> SeedAsync(string[] args)
{
    if (args.Length != 2 || args[0] != "manifest")
    {
        return UsageError("Usage: tinycosmos seed manifest <source-root>");
    }

    var manifest = await WorkspaceSeeder.CreateManifestAsync(args[1]).ConfigureAwait(false);
    WriteLinuxJson(manifest, LinuxJsonContext.Default.SeedManifest);
    return 0;
}

static int Export(string[] args)
{
    if (args.Length != 4 || args[0] != "worktree")
    {
        return UsageError("Usage: tinycosmos export worktree <host-repository> <tiny-cosmos/branch> <worktree-path>");
    }

    var plan = GitHandoff.Plan(args[1], args[2], args[3]);
    WriteLinuxJson(plan, LinuxJsonContext.Default.GitHandoffPlan);
    return 0;
}

static async Task<int> VscodeAsync(string[] args)
{
    if (args.Length >= 1 && args[0] == "open")
    {
        if (args.Length < 3)
        {
            return UsageError("Usage: tinycosmos vscode open <alias> <remote-path> [--code path] [--dry-run]");
        }

        var plan = VscodeSshConfig.PlanOpen(args[1], args[2], GetOption(args, "--code") ?? "code");
        if (GetFlag(args, "--dry-run"))
        {
            WriteLinuxJson(plan, LinuxJsonContext.Default.VscodeOpenPlan);
            return 0;
        }

        var result = await ProcessRunner.RunAsync(plan.FileName, plan.Arguments, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine(result.Stderr);
            return result.ExitCode;
        }

        return 0;
    }

    if (args.Length != 6 || args[0] != "ssh-config")
    {
        return UsageError("Usage: tinycosmos vscode <ssh-config|open> ...");
    }

    if (!int.TryParse(args[3], out var port))
    {
        return UsageError("VS Code SSH port must be an integer.");
    }

    Console.WriteLine(VscodeSshConfig.RenderManagedBlock(new VscodeSshHostEntry(args[1], args[2], port, args[4], args[5])));
    return 0;
}

static async Task<int> SetupAsync(string[] args)
{
    var commands = new[]
    {
        new LifecycleCommand("/usr/bin/install", ["-d", "-m", "0755", "/var/lib/tiny-cosmos"]),
        new LifecycleCommand("/usr/bin/install", ["-d", "-m", "0755", "/run/tiny-cosmos"]),
        new LifecycleCommand("/usr/bin/systemctl", ["daemon-reload"]),
        new LifecycleCommand("/usr/bin/systemctl", ["enable", "--now", "tinycosmos-broker.socket"]),
        new LifecycleCommand("/usr/bin/systemctl", ["--global", "enable", "tinycosmos-manager.service"])
    };

    return await RunLifecycleCommandsAsync(commands, GetFlag(args, "--dry-run")).ConfigureAwait(false);
}

static async Task<int> CleanupAsync(string[] args)
{
    var purge = GetFlag(args, "--purge");
    var commands = new List<LifecycleCommand>
    {
        new("/usr/bin/systemctl", ["disable", "--now", "tinycosmos-broker.socket"]),
        new("/usr/bin/systemctl", ["stop", "tinycosmos-broker.service"]),
        new("/usr/bin/systemctl", ["--global", "disable", "tinycosmos-manager.service"]),
        new("/usr/bin/systemctl", ["daemon-reload"]),
        new("/usr/bin/rm", ["-rf", "/run/tiny-cosmos"])
    };
    if (purge)
    {
        commands.Add(new LifecycleCommand("/usr/bin/rm", ["-rf", "/var/lib/tiny-cosmos"]));
    }

    return await RunLifecycleCommandsAsync(commands, GetFlag(args, "--dry-run")).ConfigureAwait(false);
}

static async Task<int> RunLifecycleCommandsAsync(IEnumerable<LifecycleCommand> commands, bool dryRun)
{
    foreach (var command in commands)
    {
        if (dryRun)
        {
            Console.WriteLine(command);
            continue;
        }

        var result = await ProcessRunner.RunAsync(command.FileName, command.Arguments, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine(result.Stderr);
            return result.ExitCode;
        }
    }

    return 0;
}

static async Task<int> SendAndPrintAsync<T>(string operation, T payload, string[] args)
{
    var socketPath = GetOption(args, "--socket") ?? DefaultSocketPath();
    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).ConfigureAwait(false);
    await using var stream = new NetworkStream(socket, ownsSocket: false);
    var envelope = new ProtocolEnvelope(
        ProtocolVersion.Current,
        TinyId.NewOperationId().Value,
        operation,
        JsonSerializer.SerializeToElement(payload, TinyCosmosJsonContext.Default.Options.GetTypeInfo(typeof(T))));
    await FrameCodec.WriteAsync(stream, envelope, TinyCosmosJsonContext.Default.ProtocolEnvelope).ConfigureAwait(false);
    var response = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolResponse).ConfigureAwait(false);
    if (!response.Success)
    {
        var error = response.Error ?? new TinyCosmosError(TinyCosmosErrorCode.ProtocolError, "Unknown protocol error.");
        Console.Error.WriteLine($"{error.Code}: {error.Message}");
        if (!string.IsNullOrWhiteSpace(error.Remediation))
        {
            Console.Error.WriteLine(error.Remediation);
        }

        return 2;
    }

    Console.WriteLine(response.Payload?.GetRawText() ?? "{}");
    return 0;
}

static void WriteLinuxJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
{
    Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
}

static async Task<int> CurrentUidAsync()
{
    var result = await ProcessRunner.RunAsync("/usr/bin/id", ["-u"], timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    if (result.ExitCode != 0 || !int.TryParse(result.Stdout.Trim(), out var uid))
    {
        throw new InvalidOperationException("Could not determine current UID.");
    }

    return uid;
}

static string DefaultSocketPath() => Path.Combine(
    Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
    "tiny-cosmos",
    "manager.sock");

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

static bool GetFlag(string[] args, string name)
{
    return args.Any(arg => string.Equals(arg, name, StringComparison.Ordinal));
}

static int UsageError(string message)
{
    Console.Error.WriteLine(message);
    PrintHelp();
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        tinycosmos <command>

        Commands:
          doctor
          diagnostics [--socket path]
          capabilities [--socket path]
          group get-or-create <namespace:opaque> [--workspace path] [--guest-path path] [--image id] [--socket path]
          group create <namespace:opaque> [--workspace path] [--guest-path path] [--image id] [--socket path]
          group list [--socket path]
          group inspect <group-id> [--socket path]
          group update <group-id> [--name namespace:opaque] [--workspace path] [--guest-path path] [--socket path]
          group delete <group-id> [--socket path]
          group fork <source-group-id> <namespace:opaque> [--socket path]
          group replace <group-id> [--image id] [--socket path]
          sandbox start <sandbox-id> [--socket path]
          sandbox stop <sandbox-id> [--socket path]
          exec [--pty] <group-id> -- <command> [args...]
          pty <group-id> -- <command> [args...]
          file read <group-id> <path> [--max-bytes bytes] [--socket path]
          file write <group-id> <path> [--text text|stdin] [--overwrite] [--executable] [--socket path]
          file list <group-id> <path> [--recursive] [--max-entries count] [--socket path]
          file search <group-id> <path> <pattern> [--max-matches count] [--socket path]
          seed manifest <source-root>
          export worktree <host-repository> <tiny-cosmos/branch> <worktree-path>
          vscode ssh-config <alias> <host> <port> <user> <identity-file>
          vscode open <alias> <remote-path> [--code path] [--dry-run]
          setup [--dry-run]
          cleanup [--purge] [--dry-run]
        """);
}

sealed record LifecycleCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public override string ToString() => FileName + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string value) =>
        value.IndexOfAny([' ', '\t', '\n', '\'', '"', '$', ';', '&', '|', '<', '>', '(', ')']) < 0
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
