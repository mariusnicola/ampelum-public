using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;

namespace Ampelum.Tests.Optimizers;

internal static class TestHelpers
{
    public static AmpelumContext MakeContext(IChatClient? chat = null, ITraceSink? sink = null) =>
        new(
            chat ?? new FakeChatClient(),
            new ChatAdapter(),
            sink ?? NullTraceSink.Instance,
            NullLoggerFactory.Instance);
}
