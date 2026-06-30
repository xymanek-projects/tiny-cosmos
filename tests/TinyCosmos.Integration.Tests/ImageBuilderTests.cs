namespace TinyCosmos.Integration.Tests;

public sealed class ImageBuilderTests
{
    [Fact]
    public async Task RootfsOverlayInstallsGuestSupervisorVsockService()
    {
        var root = FindRepositoryRoot();
        var servicePath = Path.Combine(root, "images", "linux", "rootfs", "etc", "systemd", "system", "tinycosmos-guest.service");
        var workspaceMountPath = Path.Combine(root, "images", "linux", "rootfs", "etc", "systemd", "system", "workspace-project.mount");
        var builderPath = Path.Combine(root, "images", "linux", "build-image.sh");

        var service = await File.ReadAllTextAsync(servicePath);
        var workspaceMount = await File.ReadAllTextAsync(workspaceMountPath);
        var builder = await File.ReadAllTextAsync(builderPath);

        Assert.Contains("ExecStartPre=-/bin/sh -c '/usr/sbin/modprobe -q vmw_vsock_virtio_transport || true'", service, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/lib/tiny-cosmos/bin/tinycosmos-guest serve 1024 41024 unknown", service, StringComparison.Ordinal);
        Assert.Contains("Requires=workspace-project.mount", service, StringComparison.Ordinal);
        Assert.Contains("After=systemd-modules-load.service workspace-project.mount", service, StringComparison.Ordinal);
        Assert.Contains("Before=ssh.service docker.service", service, StringComparison.Ordinal);
        Assert.Contains(@"Requires=dev-disk-by\x2dlabel-tinycosmos\x2dworkspace.device", workspaceMount, StringComparison.Ordinal);
        Assert.Contains("What=LABEL=tinycosmos-workspace", workspaceMount, StringComparison.Ordinal);
        Assert.Contains("x-systemd.device-timeout=30s", workspaceMount, StringComparison.Ordinal);
        Assert.Contains("Where=/workspace/project", workspaceMount, StringComparison.Ordinal);
        Assert.Contains("Before=tinycosmos-guest.service ssh.service docker.service", workspaceMount, StringComparison.Ordinal);
        Assert.Contains("\"$root/workspace/project\"", builder, StringComparison.Ordinal);
        Assert.Contains("systemctl enable workspace-project.mount", builder, StringComparison.Ordinal);
        Assert.Contains("systemctl enable tinycosmos-guest.service", builder, StringComparison.Ordinal);
        Assert.Contains("copy_rootfs_overlay", builder, StringComparison.Ordinal);
        Assert.Contains("--components=\"$components\"", builder, StringComparison.Ordinal);
        Assert.Contains("--modules-dir", builder, StringComparison.Ordinal);
        Assert.Contains("--initrd", builder, StringComparison.Ordinal);
        Assert.Contains("components=\"main,universe\"", builder, StringComparison.Ordinal);
        Assert.Contains("usr/lib/tiny-cosmos/bin/tinycosmos-guest", builder, StringComparison.Ordinal);
        Assert.Contains("/usr/lib/tiny-cosmos/images/kernel", builder, StringComparison.Ordinal);
        Assert.Contains("/usr/lib/tiny-cosmos/images/rootfs.ext4", builder, StringComparison.Ordinal);
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
}
