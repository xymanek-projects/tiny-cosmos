using TinyCosmos.Core;

namespace TinyCosmos.Linux;

public sealed record DiagnosticCheck(string Name, bool Passed, string Message, string? Remediation = null);

public sealed record HostDiagnosticReport(IReadOnlyList<DiagnosticCheck> Checks)
{
    public bool Passed => Checks.All(check => check.Passed);
}

public static class HostDiagnostics
{
    private static readonly string[] RequiredTools =
    [
        "ip",
        "nft",
        "git",
        "ssh",
        "ssh-keygen",
        "mkfs.ext4",
        "truncate",
        "mount",
        "curl",
        "tar",
        "dnsmasq"
    ];

    public static HostDiagnosticReport Run()
    {
        var checks = new List<DiagnosticCheck>
        {
            new("os", File.Exists("/etc/os-release"), ReadOsRelease()),
            new("kvm", File.Exists("/dev/kvm"), File.Exists("/dev/kvm") ? "/dev/kvm is present." : "/dev/kvm is missing.", "Enable KVM or use a KVM-capable runner."),
            new("cgroup-v2", GetFileSystemType("/sys/fs/cgroup") == "cgroup2fs", "cgroup v2 is required.", "Boot with unified cgroup hierarchy enabled.")
        };

        foreach (var tool in RequiredTools)
        {
            var path = Which(tool);
            checks.Add(new DiagnosticCheck("tool:" + tool, path is not null, path ?? $"{tool} not found.", $"Install {tool}."));
        }

        var os = ReadOsRelease();
        if (!os.Contains("Ubuntu 22.04", StringComparison.Ordinal) && !os.Contains("Ubuntu 24.04", StringComparison.Ordinal))
        {
            checks.Add(new DiagnosticCheck("host-version", true, "Host is outside the development support set; continuing with a warning."));
        }

        return new HostDiagnosticReport(checks);
    }

    public static TinyCosmosError? ValidateForVmStart()
    {
        var failed = Run().Checks.FirstOrDefault(check => !check.Passed);
        return failed is null ? null : new TinyCosmosError(TinyCosmosErrorCode.DependencyMissing, failed.Message, failed.Remediation);
    }

    private static string ReadOsRelease()
    {
        if (!File.Exists("/etc/os-release"))
        {
            return "Unknown Linux host.";
        }

        var pretty = File.ReadLines("/etc/os-release")
            .FirstOrDefault(line => line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
        return pretty?.Split('=', 2)[1].Trim('"') ?? "Linux host.";
    }

    private static string? Which(string tool)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries);
        return paths.Select(path => Path.Combine(path, tool)).FirstOrDefault(File.Exists);
    }

    private static string GetFileSystemType(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/stat",
                ArgumentList = { "-fc", "%T", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(1000);
            return process?.StandardOutput.ReadToEnd().Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
