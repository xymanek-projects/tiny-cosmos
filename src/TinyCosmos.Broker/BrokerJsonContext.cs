using System.Text.Json.Serialization;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

namespace TinyCosmos.Broker;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FirecrackerVmConfig))]
public partial class BrokerJsonContext : JsonSerializerContext;
