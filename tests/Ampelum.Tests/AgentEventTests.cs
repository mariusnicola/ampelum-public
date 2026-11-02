using System.Text.Json;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

#pragma warning disable AMPELUM_V09_PREVIEW

public sealed class AgentEventTests
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static string SerializeAsBase(AgentEvent evt) =>
        JsonSerializer.Serialize<AgentEvent>(evt, JsonOptions);

    static T DeserializeAsBase<T>(string json) where T : AgentEvent =>
        (T)JsonSerializer.Deserialize<AgentEvent>(json, JsonOptions)!;

    [Fact]
    public void TokenEvent_SerializationRoundtrip()
    {
        var evt = new TokenEvent("Hello, world!");
        var json = SerializeAsBase(evt);
        json.ShouldContain("\"$type\":\"token\"");
        json.ShouldContain("\"text\":\"Hello, world!\"");

        var deserialized = DeserializeAsBase<TokenEvent>(json);
        deserialized.Text.ShouldBe("Hello, world!");
    }

    [Fact]
    public void ToolCallEvent_SerializationRoundtrip()
    {
        var evt = new ToolCallEvent("read_file", "{\"path\":\"/tmp/test.txt\"}");
        var json = SerializeAsBase(evt);
        json.ShouldContain("\"$type\":\"tool_call\"");

        var deserialized = DeserializeAsBase<ToolCallEvent>(json);
        deserialized.Tool.ShouldBe("read_file");
        deserialized.Args.ShouldBe("{\"path\":\"/tmp/test.txt\"}");
    }

    [Fact]
    public void ToolResultEvent_SerializationRoundtrip()
    {
        var evt = new ToolResultEvent("read_file", "file contents", false);
        var json = SerializeAsBase(evt);
        json.ShouldContain("\"$type\":\"tool_result\"");

        var deserialized = DeserializeAsBase<ToolResultEvent>(json);
        deserialized.Tool.ShouldBe("read_file");
        deserialized.Result.ShouldBe("file contents");
        deserialized.IsError.ShouldBeFalse();
    }

    [Fact]
    public void StatusEvent_SerializationRoundtrip()
    {
        var evt = new StatusEvent(AgentStatus.Running, "starting");
        var json = SerializeAsBase(evt);
        json.ShouldContain("\"$type\":\"status\"");

        var deserialized = DeserializeAsBase<StatusEvent>(json);
        deserialized.Status.ShouldBe(AgentStatus.Running);
        deserialized.Message.ShouldBe("starting");
    }

    [Fact]
    public void ErrorEvent_SerializationRoundtrip()
    {
        var evt = new ErrorEvent("ERR_TIMEOUT", "The operation timed out");
        var json = SerializeAsBase(evt);
        json.ShouldContain("\"$type\":\"error\"");

        var deserialized = DeserializeAsBase<ErrorEvent>(json);
        deserialized.Code.ShouldBe("ERR_TIMEOUT");
        deserialized.Message.ShouldBe("The operation timed out");
    }

    [Fact]
    public void CompleteEvent_SerializationRoundtrip()
    {
        var evt = new CompleteEvent("Task done", 100, 50, 0.002m);
        var json = SerializeAsBase(evt);
        json.ShouldContain("\"$type\":\"complete\"");

        var deserialized = DeserializeAsBase<CompleteEvent>(json);
        deserialized.Summary.ShouldBe("Task done");
        deserialized.InputTokens.ShouldBe(100L);
        deserialized.OutputTokens.ShouldBe(50L);
        deserialized.Cost.ShouldBe(0.002m);
    }

    [Fact]
    public void PatternMatch_ExhaustiveSwitch()
    {
        AgentEvent evt = new TokenEvent("test");
        var type = evt switch
        {
            TokenEvent => "token",
            ToolCallEvent => "tool_call",
            ToolResultEvent => "tool_result",
            StatusEvent => "status",
            ErrorEvent => "error",
            CompleteEvent => "complete",
            _ => "unknown"
        };
        type.ShouldBe("token");
    }

    [Fact]
    public void DiscriminatorDeserialization_AllTypes()
    {
        var testCases = new (string Json, Type ExpectedType)[]
        {
            ("""{"$type":"token","text":"hi"}""", typeof(TokenEvent)),
            ("""{"$type":"tool_call","tool":"read","args":null}""", typeof(ToolCallEvent)),
            ("""{"$type":"tool_result","tool":"read","result":"ok","isError":false}""", typeof(ToolResultEvent)),
            ("""{"$type":"status","status":0,"message":"idle"}""", typeof(StatusEvent)),
            ("""{"$type":"error","code":"ERR","message":"fail"}""", typeof(ErrorEvent)),
            ("""{"$type":"complete","summary":"done","inputTokens":0,"outputTokens":0,"cost":null}""", typeof(CompleteEvent)),
        };

        foreach (var (json, expectedType) in testCases)
        {
            var deserialized = JsonSerializer.Deserialize<AgentEvent>(json, JsonOptions);
            deserialized.ShouldNotBeNull();
            deserialized.ShouldBeOfType(expectedType);
        }
    }
}
