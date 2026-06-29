using System.Security.Cryptography;
using TinyCosmos.Linux;

namespace TinyCosmos.Integration.Tests;

public sealed class ImageManifestTests
{
    [Fact]
    public async Task ImageManifestVerifierPassesWhenAllArtifactDigestsMatch()
    {
        using var temp = new TempDir();
        await WriteArtifactAsync(temp.Path, "kernel", "kernel-bytes");
        await WriteArtifactAsync(temp.Path, "rootfs.ext4", "rootfs-bytes");
        await WriteArtifactAsync(temp.Path, "usr/lib/tiny-cosmos/bin/tinycosmos-guest", "guest-bytes");
        var manifest = Path.Combine(temp.Path, "manifest.json");
        await File.WriteAllTextAsync(manifest, $$"""
            {
              "schemaVersion": 1,
              "imageId": "ubuntu-24.04-dev",
              "guestOs": "ubuntu-24.04",
              "architecture": "x86_64",
              "firecracker": {
                "version": "v1.0.0",
                "assetName": "firecracker-v1.0.0-x86_64.tgz",
                "url": "https://github.com/firecracker-microvm/firecracker/releases/download/v1.0.0/firecracker-v1.0.0-x86_64.tgz",
                "sha256": "{{Sha256("firecracker")}}"
              },
              "artifacts": {
                "kernel": { "path": "kernel", "sha256": "{{Sha256("kernel-bytes")}}" },
                "rootfs": { "path": "rootfs.ext4", "sha256": "{{Sha256("rootfs-bytes")}}" },
                "guestSupervisor": { "path": "/usr/lib/tiny-cosmos/bin/tinycosmos-guest", "sha256": "{{Sha256("guest-bytes")}}" }
              },
              "packages": ["openssh-server", "git", "sudo", "docker.io"]
            }
            """);

        var result = await ImageManifestVerifier.VerifyAsync(manifest, temp.Path);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Checks.Where(check => !check.Passed).Select(check => check.Message)));
    }

    [Fact]
    public async Task ImageManifestVerifierFailsOnDigestMismatch()
    {
        using var temp = new TempDir();
        await WriteArtifactAsync(temp.Path, "kernel", "wrong");
        await WriteArtifactAsync(temp.Path, "rootfs.ext4", "rootfs-bytes");
        await WriteArtifactAsync(temp.Path, "usr/lib/tiny-cosmos/bin/tinycosmos-guest", "guest-bytes");
        var manifest = Path.Combine(temp.Path, "manifest.json");
        await File.WriteAllTextAsync(manifest, $$"""
            {
              "schemaVersion": 1,
              "imageId": "ubuntu-24.04-dev",
              "guestOs": "ubuntu-24.04",
              "architecture": "x86_64",
              "firecracker": {
                "version": "v1.0.0",
                "assetName": "firecracker-v1.0.0-x86_64.tgz",
                "url": "https://github.com/firecracker-microvm/firecracker/releases/download/v1.0.0/firecracker-v1.0.0-x86_64.tgz",
                "sha256": "{{Sha256("firecracker")}}"
              },
              "artifacts": {
                "kernel": { "path": "kernel", "sha256": "{{Sha256("kernel-bytes")}}" },
                "rootfs": { "path": "rootfs.ext4", "sha256": "{{Sha256("rootfs-bytes")}}" },
                "guestSupervisor": { "path": "/usr/lib/tiny-cosmos/bin/tinycosmos-guest", "sha256": "{{Sha256("guest-bytes")}}" }
              },
              "packages": ["openssh-server", "git", "sudo", "docker.io"]
            }
            """);

        var result = await ImageManifestVerifier.VerifyAsync(manifest, temp.Path);

        Assert.False(result.Passed);
        Assert.Contains(result.Checks, check => check.Name == "kernel" && check.ActualSha256 is not null && !check.Passed);
    }

    [Fact]
    public async Task CheckedInPlaceholderManifestPassesOnlyInPlaceholderMode()
    {
        var manifest = Path.Combine(FindRepositoryRoot(), "images", "linux", "manifest.json");

        var strict = await ImageManifestVerifier.VerifyAsync(manifest, Path.GetDirectoryName(manifest)!);
        var placeholder = await ImageManifestVerifier.VerifyAsync(manifest, Path.GetDirectoryName(manifest)!, allowPlaceholders: true);

        Assert.False(strict.Passed);
        Assert.True(placeholder.Passed, string.Join(Environment.NewLine, placeholder.Checks.Where(check => !check.Passed).Select(check => check.Message)));
        Assert.Contains(placeholder.Checks, check => check.Name == "firecracker.sha256" && check.Passed);
        Assert.DoesNotContain(placeholder.Checks, check => check.Name.StartsWith("firecracker.", StringComparison.Ordinal) && !check.Passed);
    }

    private static async Task WriteArtifactAsync(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TinyCosmos.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tinycosmos-image-manifest-" + Guid.NewGuid().ToString("N"));

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
