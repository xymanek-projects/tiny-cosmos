using System.Text;
using System.Runtime.Versioning;
using TinyCosmos.Core;
using TinyCosmos.Linux;
using TinyCosmos.Protocol;

namespace TinyCosmos.Core.Tests;

public sealed class ProtocolAndSafetyTests
{
    [Fact]
    public async Task FrameCodecRoundTripsEnvelope()
    {
        await using var stream = new MemoryStream();
        var payload = ProtocolJson.ToElement(new ListGroupsPayload(1000), TinyCosmosJsonContext.Default.ListGroupsPayload);
        var envelope = new ProtocolEnvelope(ProtocolVersion.Current, "op_test", ManagerOperations.ListGroups, payload);

        await FrameCodec.WriteAsync(stream, envelope, TinyCosmosJsonContext.Default.ProtocolEnvelope);
        stream.Position = 0;
        var decoded = await FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolEnvelope);

        Assert.Equal(envelope.RequestId, decoded.RequestId);
        Assert.Equal(1000, ProtocolJson.FromElement(decoded.Payload, TinyCosmosJsonContext.Default.ListGroupsPayload).OwnerUid);
    }

    [Fact]
    public async Task FrameCodecRejectsOversizedLength()
    {
        await using var stream = new MemoryStream();
        var bytes = new byte[] { 0x7f, 0xff, 0xff, 0xff };
        await stream.WriteAsync(bytes);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(stream, TinyCosmosJsonContext.Default.ProtocolEnvelope).AsTask());
    }

    [Fact]
    public void GuestControlContractsRoundTripWithSourceGeneratedMetadata()
    {
        var ready = new GuestReadyReport(
            "op_bootnonce",
            "agent",
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITest",
            SudoAvailable: true,
            DockerAvailable: true,
            DateTimeOffset.UnixEpoch);

        var element = ProtocolJson.ToElement(ready, TinyCosmosJsonContext.Default.GuestReadyReport);
        var decoded = ProtocolJson.FromElement(element, TinyCosmosJsonContext.Default.GuestReadyReport);

        Assert.Equal("op_bootnonce", decoded.BootNonce);
        Assert.Equal("agent", decoded.SshUser);
        Assert.True(decoded.SudoAvailable);
        Assert.True(decoded.DockerAvailable);
    }

    [Theory]
    [InlineData("capabilities.request.json", ManagerOperations.Capabilities)]
    [InlineData("group-create.request.json", ManagerOperations.GetOrCreateGroup)]
    [InlineData("exec.request.json", ManagerOperations.Exec)]
    [InlineData("file-read.request.json", ManagerOperations.FileRead)]
    [InlineData("diagnostics.request.json", ManagerOperations.Diagnostics)]
    [InlineData("group-fork.request.json", ManagerOperations.ForkGroup)]
    [InlineData("group-replace.request.json", ManagerOperations.ReplacePrimarySandbox)]
    public async Task ProtocolFixturesDeserializeAsManagerEnvelopes(string fileName, string operation)
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "integrations", "protocol", "v1", fileName);
        await using var stream = File.OpenRead(fixturePath);
        var envelope = await System.Text.Json.JsonSerializer.DeserializeAsync(stream, TinyCosmosJsonContext.Default.ProtocolEnvelope);

        Assert.NotNull(envelope);
        Assert.Equal(ProtocolVersion.Current, envelope!.Version);
        Assert.Equal(operation, envelope.Operation);
        switch (operation)
        {
            case ManagerOperations.Capabilities:
            case ManagerOperations.Diagnostics:
                _ = ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.EmptyPayload);
                break;
            case ManagerOperations.GetOrCreateGroup:
                _ = ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.GroupCreatePayload);
                break;
            case ManagerOperations.ForkGroup:
                _ = ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.ForkGroupPayload);
                break;
            case ManagerOperations.ReplacePrimarySandbox:
                _ = ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.ReplacePrimarySandboxPayload);
                break;
            case ManagerOperations.Exec:
                _ = ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.ExecPayload);
                break;
            case ManagerOperations.FileRead:
                _ = ProtocolJson.FromElement(envelope.Payload, TinyCosmosJsonContext.Default.FileReadPayload);
                break;
            default:
                throw new InvalidOperationException("Unhandled fixture operation.");
        }
    }

    [Fact]
    public async Task GuestReadyFixtureDeserializesWithGuestControlMetadata()
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "integrations", "protocol", "v1", "guest-ready.report.json");
        await using var stream = File.OpenRead(fixturePath);
        var report = await System.Text.Json.JsonSerializer.DeserializeAsync(stream, TinyCosmosJsonContext.Default.GuestReadyReport);

        Assert.NotNull(report);
        Assert.Equal("agent", report!.SshUser);
        Assert.True(report.SudoAvailable);
        Assert.True(report.DockerAvailable);
    }

    [Fact]
    public async Task DiagnosticsResponseFixtureDeserializesWithManagerMetadata()
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "integrations", "protocol", "v1", "diagnostics.response.json");
        await using var stream = File.OpenRead(fixturePath);
        var response = await System.Text.Json.JsonSerializer.DeserializeAsync(stream, TinyCosmosJsonContext.Default.ProtocolResponse);

        Assert.NotNull(response);
        Assert.True(response!.Success);
        var diagnostics = ProtocolJson.FromElement(response.Payload!.Value, TinyCosmosJsonContext.Default.ManagerDiagnosticsResponse);
        Assert.Contains(diagnostics.Checks, check => check.Name == "kvm");
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task BrokerLaunchFixtureDeserializesWithLinuxMetadata()
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "integrations", "protocol", "v1", "broker-plan-launch.response.json");
        await using var stream = File.OpenRead(fixturePath);
        var launch = await System.Text.Json.JsonSerializer.DeserializeAsync(stream, LinuxJsonContext.Default.BrokerLaunchPlan);

        Assert.NotNull(launch);
        Assert.Contains(launch!.ApiCalls, call => call.Path == "/actions" && call.Body.Contains("InstanceStart", StringComparison.Ordinal));
        Assert.Contains(launch.Commands, command => command.FileName == "/usr/bin/curl");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("safe/.git/config")]
    [InlineData("safe\\windows")]
    public void ArchiveSafetyRejectsUnsafePaths(string path)
    {
        var error = ArchiveSafety.ValidateEntry(new ArchiveEntrySpec(path, ArchiveEntryKind.RegularFile));
        Assert.NotNull(error);
        Assert.Equal(TinyCosmosErrorCode.ExportRejected, error.Code);
    }

    [Fact]
    public void ArchiveSafetyRejectsDuplicateNormalizedPaths()
    {
        var error = ArchiveSafety.ValidateNoDuplicateNormalizedPaths(
        [
            new ArchiveEntrySpec("a/b", ArchiveEntryKind.RegularFile),
            new ArchiveEntrySpec("a//b", ArchiveEntryKind.RegularFile)
        ]);
        Assert.NotNull(error);
    }

    [Fact]
    public void OutputSanitizerEscapesControlAndBidiCharacters()
    {
        var input = "ok\u001b]52;c;bad\u0007\u202erev";
        var sanitized = OutputSanitizer.SanitizeStructuredText(input);
        Assert.Contains("\\u001b", sanitized, StringComparison.Ordinal);
        Assert.Contains("\\u202e", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', sanitized);
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
