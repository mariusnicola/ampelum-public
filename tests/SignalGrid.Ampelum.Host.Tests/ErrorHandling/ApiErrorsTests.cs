#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.ErrorHandling;
using SignalGrid.Ampelum.Host.Internal;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.ErrorHandling;

public class ApiErrorsTests
{
    const string TestTraceId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
    const string TestCode = "test_code";
    const string TestMessage = "Test message";

    [Fact]
    public async Task NotFound_ProducesCorrectStatusCodeAndBody()
    {
        var result = ApiErrors.NotFound(TestCode, TestMessage, TestTraceId);
        var ctx = CreateContext();

        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(404);
        var body = await DeserializeResponse(ctx);
        body.Code.ShouldBe(TestCode);
        body.TraceId.ShouldBe(TestTraceId);
        body.Message.ShouldBe(TestMessage);
    }

    [Fact]
    public async Task BadRequest_ProducesCorrectStatusCodeAndBody()
    {
        var result = ApiErrors.BadRequest(TestCode, TestMessage, TestTraceId);
        var ctx = CreateContext();

        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(400);
        var body = await DeserializeResponse(ctx);
        body.Code.ShouldBe(TestCode);
        body.TraceId.ShouldBe(TestTraceId);
        body.Message.ShouldBe(TestMessage);
    }

    [Fact]
    public async Task Conflict_ProducesCorrectStatusCodeAndBody()
    {
        var result = ApiErrors.Conflict(TestCode, TestMessage, TestTraceId);
        var ctx = CreateContext();

        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(409);
        var body = await DeserializeResponse(ctx);
        body.Code.ShouldBe(TestCode);
        body.TraceId.ShouldBe(TestTraceId);
        body.Message.ShouldBe(TestMessage);
    }

    [Fact]
    public async Task Unprocessable_ProducesCorrectStatusCodeAndBody()
    {
        var result = ApiErrors.Unprocessable(TestCode, TestMessage, TestTraceId);
        var ctx = CreateContext();

        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(422);
        var body = await DeserializeResponse(ctx);
        body.Code.ShouldBe(TestCode);
        body.TraceId.ShouldBe(TestTraceId);
        body.Message.ShouldBe(TestMessage);
    }

    [Fact]
    public async Task TooManyRequests_ProducesCorrectStatusCodeAndBody()
    {
        var result = ApiErrors.TooManyRequests(TestCode, TestMessage, TestTraceId);
        var ctx = CreateContext();

        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(429);
        var body = await DeserializeResponse(ctx);
        body.Code.ShouldBe(TestCode);
        body.TraceId.ShouldBe(TestTraceId);
        body.Message.ShouldBe(TestMessage);
    }

    [Fact]
    public async Task ServiceUnavailable_ProducesCorrectStatusCodeAndBody()
    {
        var result = ApiErrors.ServiceUnavailable(TestCode, TestMessage, TestTraceId);
        var ctx = CreateContext();

        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(503);
        var body = await DeserializeResponse(ctx);
        body.Code.ShouldBe(TestCode);
        body.TraceId.ShouldBe(TestTraceId);
        body.Message.ShouldBe(TestMessage);
    }

    [Fact]
    public async Task ApiError_WithDetails_IncludesDetails()
    {
        var details = new Dictionary<string, JsonElement>
        {
            { "field", JsonSerializer.SerializeToElement("value") }
        };

        var result = ApiErrors.BadRequest(TestCode, TestMessage, TestTraceId, details);
        var ctx = CreateContext();

        await result.ExecuteAsync(ctx);

        var body = await DeserializeResponse(ctx);
        body.Details.ShouldNotBeNull();
        body.Details.ShouldContainKey("field");
    }

    static DefaultHttpContext CreateContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(AmpelumHostJsonContext.Default);
        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };
        return ctx;
    }

    static async Task<ErrorResponse> DeserializeResponse(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse)
            ?? throw new InvalidOperationException("Failed to deserialize ErrorResponse");
    }
}
