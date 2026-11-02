#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.Sse;

namespace SignalGrid.Ampelum.Host.Tests.Sse;

public sealed class SseWriterTests
{
    static (SseWriter writer, MemoryStream body) MakeSseWriter(TimeSpan? keepAlive = null)
    {
        var ctx = new DefaultHttpContext();
        var ms = new MemoryStream();
        ctx.Response.Body = ms;
        var log = NullLogger.Instance;
        var writer = new SseWriter(ctx.Response, log, keepAlive ?? TimeSpan.FromSeconds(60));
        return (writer, ms);
    }

    [Fact]
    public async Task WriteEventAsync_ProducesCorrectWireFormat()
    {
        var (writer, body) = MakeSseWriter();
        await writer.StartAsync(CancellationToken.None);
        await writer.WriteEventAsync("token", "{\"x\":1}", id: 5, CancellationToken.None);
        await writer.DisposeAsync();

        var content = Encoding.UTF8.GetString(body.ToArray());
        content.ShouldContain("event: token");
        content.ShouldContain("data: {\"x\":1}");
        content.ShouldContain("id: 5");
        content.ShouldEndWith("\n\n");
    }

    [Fact]
    public async Task WriteEventAsync_MultiLinePayload_EmitsMultipleDataLines()
    {
        var (writer, body) = MakeSseWriter();
        await writer.StartAsync(CancellationToken.None);
        await writer.WriteEventAsync("test", "a\nb", id: null, CancellationToken.None);
        await writer.DisposeAsync();

        var content = Encoding.UTF8.GetString(body.ToArray());
        content.ShouldContain("data: a");
        content.ShouldContain("data: b");
    }

    [Fact]
    public async Task WriteEventAsync_NoId_OmitsIdLine()
    {
        var (writer, body) = MakeSseWriter();
        await writer.StartAsync(CancellationToken.None);
        await writer.WriteEventAsync("done", "{}", id: null, CancellationToken.None);
        await writer.DisposeAsync();

        var content = Encoding.UTF8.GetString(body.ToArray());
        content.ShouldNotContain("id:");
    }

    [Fact]
    public async Task KeepAlive_EmitsCommentLine()
    {
        var (writer, body) = MakeSseWriter(TimeSpan.FromMilliseconds(50));
        await writer.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var content = Encoding.UTF8.GetString(body.ToArray());
        // Count how many times ": " appears (keep-alive comments)
        var count = 0;
        var idx = 0;
        while ((idx = content.IndexOf(": ", idx)) >= 0)
        {
            count++;
            idx += 2;
        }
        count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task WriteEventAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (writer, _) = MakeSseWriter();
        await writer.StartAsync(CancellationToken.None);
        await writer.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => writer.WriteEventAsync("test", "{}", null, CancellationToken.None).AsTask()
        );
    }

    [Fact]
    public async Task StartAsync_SetsCorrectContentType()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var writer = new SseWriter(ctx.Response, NullLogger.Instance, TimeSpan.FromSeconds(60));
        await writer.StartAsync(CancellationToken.None);

        ctx.Response.ContentType.ShouldBe("text/event-stream");
        await writer.DisposeAsync();
    }
}
