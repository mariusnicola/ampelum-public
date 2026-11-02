using SignalGrid.Ampelum.Context;
using Shouldly;
using System.Text.Json;
using Xunit;

namespace Ampelum.Tests.Context;

public class EventEntryTests
{
    [Fact]
    public void UserMessage_RoundTripsThroughSourceGenContext()
    {
        EventEntry msg = new UserMessage(
            Turn: 1, Seq: 0,
            Timestamp: DateTimeOffset.Parse("2026-05-06T00:00:00Z"),
            Key: "user_msg:1:0:abc12345",
            SupersededBy: null,
            Text: "hello");

        var json = JsonSerializer.Serialize(msg, EventLogJsonContext.Default.EventEntry);
        json.ShouldContain("\"$type\":\"user_msg\"");
        json.ShouldContain("\"Text\":\"hello\"");

        var roundTrip = JsonSerializer.Deserialize(json, EventLogJsonContext.Default.EventEntry);
        roundTrip.ShouldBeOfType<UserMessage>();
        ((UserMessage)roundTrip!).Text.ShouldBe("hello");
    }

    [Fact]
    public void AllElevenDerivedTypes_AreDeclaredOnBaseRecord()
    {
        var attrs = typeof(EventEntry).GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false);
        attrs.Length.ShouldBe(12);
    }
}
