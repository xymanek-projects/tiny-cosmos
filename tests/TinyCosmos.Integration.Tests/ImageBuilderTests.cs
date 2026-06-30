namespace TinyCosmos.Integration.Tests;

public sealed class ImageBuilderTests
{
    [Fact]
    public async Task RootfsOverlayInstallsGuestSupervisorVsockService()
    {
        var root = FindRepositoryRoot();
        var servicePath = Path.Combine(root, "images", "linux", "rootfs", "etc", "systemd", "system", "tinycosmos-guest.service");
        var workspaceServicePath = Path.Combine(root, "images", "linux", "rootfs", "etc", "systemd", "system", "workspace-project.service");
        var builderPath = Path.Combine(root, "images", "linux", "build-image.sh");

        var service = await File.ReadAllTextAsync(servicePath);
        var workspaceService = await File.ReadAllTextAsync(workspaceServicePath);
        var builder = await File.ReadAllTextAsync(builderPath);

        Assert.Contains("ExecStartPre=-/bin/sh -c '/usr/sbin/modprobe -q vmw_vsock_virtio_transport || true'", service, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/lib/tiny-cosmos/bin/tinycosmos-guest serve 1024 41024 unknown", service, StringComparison.Ordinal);
        Assert.Contains("After=systemd-modules-load.service", service, StringComparison.Ordinal);
        Assert.Contains("Before=ssh.service docker.service", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Requires=workspace-project.service", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Before=tinycosmos-guest.service", workspaceService, StringComparison.Ordinal);
        Assert.DoesNotContain("Before=ssh.service", workspaceService, StringComparison.Ordinal);
        Assert.Contains("Type=oneshot", workspaceService, StringComparison.Ordinal);
        Assert.Contains("mount -t ext4 LABEL=tinycosmos-workspace /workspace/project", workspaceService, StringComparison.Ordinal);
        Assert.Contains("/dev/vdb /dev/vdc /dev/sdb /dev/xvdb", workspaceService, StringComparison.Ordinal);
        Assert.Contains("TimeoutStartSec=75", workspaceService, StringComparison.Ordinal);
        Assert.Contains("\"$root/workspace/project\"", builder, StringComparison.Ordinal);
        Assert.Contains("systemctl enable workspace-project.service", builder, StringComparison.Ordinal);
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
