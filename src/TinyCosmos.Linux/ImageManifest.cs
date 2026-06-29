using System.Security.Cryptography;
using System.Text.Json;
using TinyCosmos.Core;

namespace TinyCosmos.Linux;

public sealed record ImageManifest(
    int SchemaVersion,
    string ImageId,
    string GuestOs,
    string Architecture,
    ImageManifestFirecracker Firecracker,
    ImageManifestArtifacts Artifacts,
    IReadOnlyList<string> Packages);

public sealed record ImageManifestFirecracker(
    string Version,
    string AssetName,
    string Url,
    string Sha256);

public sealed record ImageManifestArtifacts(
    ImageManifestArtifact Kernel,
    ImageManifestArtifact Rootfs,
    ImageManifestArtifact GuestSupervisor);

public sealed record ImageManifestArtifact(string Path, string Sha256);

public sealed record ImageManifestCheck(
    string Name,
    string Path,
    string ExpectedSha256,
    string? ActualSha256,
    bool Passed,
    string Message);

public sealed record ImageManifestVerificationResult(
    bool Passed,
    IReadOnlyList<ImageManifestCheck> Checks);

public static class ImageManifestVerifier
{
    private const string PlaceholderPrefix = "to-be-filled";
    private const string ComputedDuringPackage = "computed-during-package";

    public static async Task<ImageManifestVerificationResult> VerifyAsync(
        string manifestPath,
        string artifactRoot,
        bool allowPlaceholders = false,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync(stream, LinuxJsonContext.Default.ImageManifest, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Image manifest was empty."));

        var checks = new List<ImageManifestCheck>();
        ValidateManifestShape(manifest, checks, allowPlaceholders);
        await VerifyArtifactAsync("kernel", manifest.Artifacts.Kernel, artifactRoot, checks, allowPlaceholders, cancellationToken).ConfigureAwait(false);
        await VerifyArtifactAsync("rootfs", manifest.Artifacts.Rootfs, artifactRoot, checks, allowPlaceholders, cancellationToken).ConfigureAwait(false);
        await VerifyArtifactAsync("guestSupervisor", manifest.Artifacts.GuestSupervisor, artifactRoot, checks, allowPlaceholders, cancellationToken).ConfigureAwait(false);
        return new ImageManifestVerificationResult(checks.All(check => check.Passed), checks);
    }

    private static void ValidateManifestShape(ImageManifest manifest, List<ImageManifestCheck> checks, bool allowPlaceholders)
    {
        AddShapeCheck(checks, "schemaVersion", manifest.SchemaVersion == 1, "schemaVersion must be 1.");
        AddShapeCheck(checks, "imageId", !string.IsNullOrWhiteSpace(manifest.ImageId), "imageId must be present.");
        AddShapeCheck(checks, "guestOs", manifest.GuestOs == "ubuntu-24.04", "MVP guestOs must be ubuntu-24.04.");
        AddShapeCheck(checks, "architecture", manifest.Architecture == "x86_64", "MVP architecture must be x86_64.");
        AddShapeCheck(checks, "firecracker.version", !string.IsNullOrWhiteSpace(manifest.Firecracker.Version), "Firecracker version must be pinned.");
        AddShapeCheck(checks, "firecracker.assetName", manifest.Firecracker.AssetName.EndsWith("-x86_64.tgz", StringComparison.Ordinal), "Firecracker x86_64 release asset must be pinned.");
        AddShapeCheck(checks, "firecracker.url", Uri.TryCreate(manifest.Firecracker.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, "Firecracker release URL must be absolute HTTPS.");
        AddShapeCheck(
            checks,
            "firecracker.sha256",
            IsSha256(manifest.Firecracker.Sha256) || (allowPlaceholders && IsPlaceholder(manifest.Firecracker.Sha256)),
            "Firecracker sha256 must be a lowercase 64-character digest.");
    }

    private static async Task VerifyArtifactAsync(
        string name,
        ImageManifestArtifact artifact,
        string artifactRoot,
        List<ImageManifestCheck> checks,
        bool allowPlaceholders,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(artifact.Sha256))
        {
            var placeholderAllowed = allowPlaceholders && IsPlaceholder(artifact.Sha256);
            checks.Add(new ImageManifestCheck(name, artifact.Path, artifact.Sha256, null, placeholderAllowed, placeholderAllowed ? "Digest placeholder accepted in non-release verification mode." : "Artifact sha256 must be a lowercase 64-character digest."));
            return;
        }

        var resolved = ResolveArtifactPath(artifactRoot, artifact.Path);
        if (!File.Exists(resolved))
        {
            var missingAllowed = allowPlaceholders && IsPlaceholder(artifact.Sha256);
            checks.Add(new ImageManifestCheck(name, resolved, artifact.Sha256, null, missingAllowed, missingAllowed ? "Artifact file is not present yet; placeholder accepted." : "Artifact file is missing."));
            return;
        }

        await using var stream = File.OpenRead(resolved);
        using var sha = SHA256.Create();
        var actual = Convert.ToHexStringLower(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
        checks.Add(new ImageManifestCheck(name, resolved, artifact.Sha256, actual, string.Equals(actual, artifact.Sha256, StringComparison.Ordinal), "Artifact digest verification."));
    }

    private static string ResolveArtifactPath(string artifactRoot, string artifactPath)
    {
        var relative = Path.IsPathRooted(artifactPath) ? artifactPath.TrimStart(Path.DirectorySeparatorChar) : artifactPath;
        return Path.GetFullPath(Path.Combine(artifactRoot, relative));
    }

    private static void AddShapeCheck(List<ImageManifestCheck> checks, string name, bool passed, string message)
    {
        checks.Add(new ImageManifestCheck(name, string.Empty, string.Empty, null, passed, message));
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    private static bool IsPlaceholder(string value)
    {
        return value.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) || value == ComputedDuringPackage;
    }
}
