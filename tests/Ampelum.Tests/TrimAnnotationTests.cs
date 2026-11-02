using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Optimizers;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

public sealed class TrimAnnotationTests
{
    [Fact]
    public void OptimizedProgram_TProgram_HasDynamicallyAccessedMembers()
    {
        var typeParam = typeof(OptimizedProgram<>).GetGenericArguments()[0];
        var attr = typeParam.GetCustomAttributes(typeof(DynamicallyAccessedMembersAttribute), false)
            .Cast<DynamicallyAccessedMembersAttribute>()
            .SingleOrDefault();

        attr.ShouldNotBeNull("TProgram must be annotated for AOT trim safety (PSQ-6).");
        attr.MemberTypes.HasFlag(DynamicallyAccessedMemberTypes.PublicMethods).ShouldBeTrue();
        attr.MemberTypes.HasFlag(DynamicallyAccessedMemberTypes.NonPublicMethods).ShouldBeTrue();
    }
}
