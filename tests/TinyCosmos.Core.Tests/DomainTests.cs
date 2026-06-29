using System.Text;
using TinyCosmos.Core;

namespace TinyCosmos.Core.Tests;

public sealed class DomainTests
{
    [Fact]
    public void LogicalGroupNameAcceptsExactNamespaceOpaqueForm()
    {
        Assert.True(LogicalGroupName.TryParse("opencode:session%201", out var name, out var error));
        Assert.Equal("opencode:session%201", name.Value);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("OpenCode:abc")]
    [InlineData("opencode:")]
    [InlineData(":abc")]
    [InlineData("open-code:abc")]
    public void LogicalGroupNameRejectsInvalidShape(string value)
    {
        Assert.False(LogicalGroupName.TryParse(value, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void LogicalGroupNameRejectsMoreThan512Utf8Bytes()
    {
        var oversized = "pi:" + new string('x', 510);
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > 512);
        Assert.False(LogicalGroupName.TryParse(oversized, out _, out _));
    }

    [Fact]
    public void IdsUseExpectedPrefixes()
    {
        Assert.True(TinyId.IsValid(TinyId.NewGroupId().Value, "grp"));
        Assert.True(TinyId.IsValid(TinyId.NewSandboxId().Value, "sbx"));
        Assert.True(TinyId.IsValid(TinyId.NewOperationId().Value, "op"));
    }

    [Fact]
    public void LifecycleRulesPermitMvpStartStopPath()
    {
        Assert.True(LifecycleRules.CanTransition(LifecycleState.Stopped, LifecycleState.Starting));
        Assert.True(LifecycleRules.CanTransition(LifecycleState.Starting, LifecycleState.Ready));
        Assert.True(LifecycleRules.CanTransition(LifecycleState.Ready, LifecycleState.Stopping));
        Assert.True(LifecycleRules.CanTransition(LifecycleState.Stopping, LifecycleState.Stopped));
    }

    [Fact]
    public void LifecycleRulesRejectRetiredRestart()
    {
        Assert.False(LifecycleRules.CanTransition(LifecycleState.Retired, LifecycleState.Starting));
        Assert.Throws<InvalidOperationException>(() => LifecycleRules.RequireTransition(LifecycleState.Retired, LifecycleState.Starting));
    }
}
