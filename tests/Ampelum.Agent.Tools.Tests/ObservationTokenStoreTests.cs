using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tools.Tests;

public sealed class ObservationTokenStoreTests
{
    [Fact]
    public void Issue_Returns_Token_With_Valid_Path()
    {
        var store = new ObservationTokenStore();
        var token = store.Issue("/home/test/file.cs");
        token.TokenId.ShouldNotBeNullOrEmpty();
        token.ResourcePath.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Validate_With_Valid_Token_Returns_True()
    {
        var store = new ObservationTokenStore();
        var token = store.Issue("/home/test/file.cs");
        store.Validate(token.TokenId, "/home/test/file.cs").ShouldBeTrue();
    }

    [Fact]
    public void Validate_Without_Token_Returns_False()
    {
        var store = new ObservationTokenStore();
        store.Validate("fake-token", "/any/path").ShouldBeFalse();
    }

    [Fact]
    public void Validate_Wrong_Path_Returns_False()
    {
        var store = new ObservationTokenStore();
        var token = store.Issue("/home/test/file-a.cs");
        store.Validate(token.TokenId, "/home/test/file-b.cs").ShouldBeFalse();
    }

    [Fact]
    public void Validate_Expired_Token_Returns_False()
    {
        var store = new ObservationTokenStore();
        var token = store.Issue("/home/test/file.cs", expiryAfterCalls: 1);
        store.Validate(token.TokenId, "/home/test/file.cs").ShouldBeTrue(); // first use
        store.Validate(token.TokenId, "/home/test/file.cs").ShouldBeFalse(); // expired
    }

    [Fact]
    public void Canonical_Path_Matching_Ignores_Case_And_Slashes()
    {
        var store = new ObservationTokenStore();
        var token = store.Issue("C:\\Users\\test\\file.cs");
        store.Validate(token.TokenId, "c:/users/test/file.cs").ShouldBeTrue();
    }
}
