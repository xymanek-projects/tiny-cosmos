using TinyCosmos.Core;
using TinyCosmos.Linux;

namespace TinyCosmos.Manager;

public interface IHostPrerequisiteChecker
{
    TinyCosmosError? ValidateForVmStart();
}

public sealed class LinuxHostPrerequisiteChecker : IHostPrerequisiteChecker
{
    public TinyCosmosError? ValidateForVmStart() => HostDiagnostics.ValidateForVmStart();
}

public sealed class PassingHostPrerequisiteChecker : IHostPrerequisiteChecker
{
    public TinyCosmosError? ValidateForVmStart() => null;
}
