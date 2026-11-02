using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

#pragma warning disable AMPELUM_V09_PREVIEW

public sealed class ITaskAgentTests
{
    [Fact]
    public void ITaskAgent_InterfaceCompiles()
    {
        typeof(ITaskAgent).IsInterface.ShouldBeTrue();
        typeof(ITaskAgent).GetInterfaces().ShouldContain(typeof(IDisposable));
    }

    [Fact]
    public void ITaskAgent_HasExpectedProperties()
    {
        var props = typeof(ITaskAgent).GetProperties();
        props.Any(p => p is { Name: "Id", PropertyType: { Name: "String" } }).ShouldBeTrue();
        props.Any(p => p is { Name: "Status", PropertyType: { Name: "AgentStatus" } }).ShouldBeTrue();
        props.Any(p => p.Name == "Events" && p.PropertyType.Name == "ChannelReader`1").ShouldBeTrue();
    }

    [Fact]
    public void AgentStatus_HasExpectedValues()
    {
        var values = Enum.GetValues<AgentStatus>();
        values.ShouldContain(AgentStatus.Idle);
        values.ShouldContain(AgentStatus.Running);
        values.ShouldContain(AgentStatus.Completed);
        values.ShouldContain(AgentStatus.Error);
        values.ShouldContain(AgentStatus.Stopped);
    }

    [Fact]
    public void ITaskAgent_HasExpectedMethods()
    {
        var methodNames = typeof(ITaskAgent).GetMethods().Select(m => m.Name).ToArray();
        methodNames.ShouldContain("StartAsync");
        methodNames.ShouldContain("StopAsync");
        methodNames.ShouldContain("InterruptAsync");
    }

    [Fact]
    public void ITaskAgent_StartAsync_HasExpectedParameters()
    {
        var method = typeof(ITaskAgent).GetMethod("StartAsync");
        method.ShouldNotBeNull();
        var paramNames = method.GetParameters().Select(p => p.Name).ToArray();
        paramNames.ShouldContain("taskInstruction");
        paramNames.ShouldContain("ct");
    }

    [Fact]
    public void ITaskAgent_InterruptAsync_HasExpectedParameters()
    {
        var method = typeof(ITaskAgent).GetMethod("InterruptAsync");
        method.ShouldNotBeNull();
        var paramNames = method.GetParameters().Select(p => p.Name).ToArray();
        paramNames.ShouldContain("message");
        paramNames.ShouldContain("ct");
    }

    [Fact]
    public void ITaskAgent_IsDisposable()
    {
        typeof(ITaskAgent).IsAssignableTo(typeof(IDisposable)).ShouldBeTrue();
    }
}
