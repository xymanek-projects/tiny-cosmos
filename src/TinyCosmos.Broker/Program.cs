using System.Text.Json;
using TinyCosmos.Core;
using TinyCosmos.Broker;
using TinyCosmos.Linux;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine("""
            tinycosmos-broker

            Commands:
              validate-firecracker-config <json-file>
              plan-storage <json-file>
              plan-network <json-file>
              plan-launch <json-file>
              plan-stop <json-file>
              plan-cleanup <json-file>
              verify-image-manifest <manifest-json> <artifact-root> [--allow-placeholders]
              execute-plan <json-file> [--execute]
              serve [--socket path] [--dry-run-execution]
              record-operation <ledger> <operation-id> <owner-uid> <operation-name> <request-text> <result-text>
              doctor

            The broker executable is intentionally narrow. The privileged socket service
            will accept typed Tiny Cosmos VM operations, not arbitrary host commands.
            """);
        return 0;
    }

    switch (args[0])
    {
        case "doctor":
            var report = HostDiagnostics.Run();
            foreach (var check in report.Checks)
            {
                Console.WriteLine($"{(check.Passed ? "ok" : "fail")} {check.Name}: {check.Message}");
            }

            return report.Passed ? 0 : 1;
        case "validate-firecracker-config" when args.Length == 2:
            var json = await File.ReadAllTextAsync(args[1]).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize(json, BrokerJsonContext.Default.FirecrackerVmConfig)
                ?? throw new InvalidOperationException("Config file did not contain a Firecracker VM config.");
            var error = BrokerRequestValidator.ValidateFirecrackerConfig(config);
            if (error is not null)
            {
                Console.Error.WriteLine($"{error.Code}: {error.Message}");
                return 2;
            }

            Console.WriteLine("ok");
            return 0;
        case "plan-storage" when args.Length == 2:
            var storage = await ReadJsonAsync(args[1], LinuxJsonContext.Default.BrokerStorageRequest).ConfigureAwait(false);
            WriteJson(BrokerCommandPlanner.PlanStorage(storage), LinuxJsonContext.Default.BrokerStoragePlan);
            return 0;
        case "plan-network" when args.Length == 2:
            var network = await ReadJsonAsync(args[1], LinuxJsonContext.Default.BrokerNetworkRequest).ConfigureAwait(false);
            WriteJson(BrokerCommandPlanner.PlanNetwork(network), LinuxJsonContext.Default.BrokerNetworkPlan);
            return 0;
        case "plan-launch" when args.Length == 2:
            var launch = await ReadJsonAsync(args[1], LinuxJsonContext.Default.BrokerLaunchRequest).ConfigureAwait(false);
            WriteJson(BrokerCommandPlanner.PlanLaunch(launch), LinuxJsonContext.Default.BrokerLaunchPlan);
            return 0;
        case "plan-stop" when args.Length == 2:
            var stop = await ReadJsonAsync(args[1], LinuxJsonContext.Default.BrokerStopRequest).ConfigureAwait(false);
            WriteJson(BrokerCommandPlanner.PlanStop(stop), LinuxJsonContext.Default.BrokerStopPlan);
            return 0;
        case "plan-cleanup" when args.Length == 2:
            var cleanup = await ReadJsonAsync(args[1], LinuxJsonContext.Default.BrokerCleanupRequest).ConfigureAwait(false);
            WriteJson(BrokerCommandPlanner.PlanCleanup(cleanup), LinuxJsonContext.Default.BrokerCleanupPlan);
            return 0;
        case "verify-image-manifest" when args.Length is 3 or 4:
            var verification = await ImageManifestVerifier.VerifyAsync(
                args[1],
                args[2],
                allowPlaceholders: HasFlag(args, "--allow-placeholders")).ConfigureAwait(false);
            WriteJson(verification, LinuxJsonContext.Default.ImageManifestVerificationResult);
            return verification.Passed ? 0 : 1;
        case "record-operation" when args.Length == 7:
            var record = BrokerCommandPlanner.RecordIdempotentResult(args[1], args[2], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), args[4], args[5], args[6]);
            WriteJson(record, LinuxJsonContext.Default.BrokerOperationRecord);
            return 0;
        case "execute-plan" when args.Length is 2 or 3:
            var commands = await ReadJsonAsync(args[1], LinuxJsonContext.Default.PlannedCommandArray).ConfigureAwait(false);
            var execute = args.Length == 3 && args[2] == "--execute";
            var results = await BrokerCommandExecutor.ExecuteAsync(commands, dryRun: !execute).ConfigureAwait(false);
            WriteJson(results.ToArray(), LinuxJsonContext.Default.BrokerCommandResultArray);
            return results.All(result => result.ExitCode == 0) ? 0 : 1;
        case "serve":
            var socketPath = GetOption(args, "--socket") ?? "/run/tiny-cosmos/broker.sock";
            var dryRunExecution = HasFlag(args, "--dry-run-execution");
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cts.Cancel();
                };
                await new BrokerProtocolServer(socketPath, dryRunExecution).RunAsync(cts.Token).ConfigureAwait(false);
            }

            return 0;
        default:
            Console.Error.WriteLine("Invalid broker command.");
            return 2;
    }
}
catch (TinyCosmosException ex)
{
    Console.Error.WriteLine($"{ex.Error.Code}: {ex.Error.Message}");
    return 2;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static async Task<T> ReadJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
{
    var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
    return JsonSerializer.Deserialize(json, typeInfo)
        ?? throw new InvalidOperationException("JSON file did not contain the expected payload.");
}

static void WriteJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
{
    Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}

static bool HasFlag(string[] args, string name)
{
    return args.Any(arg => string.Equals(arg, name, StringComparison.Ordinal));
}
