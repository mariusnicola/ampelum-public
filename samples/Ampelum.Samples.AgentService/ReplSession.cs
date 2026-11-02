using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Repl;

namespace Ampelum.Samples.AgentService;

/// <summary>Runs scripted input through the REPL library against a chat client.</summary>
public static class ReplSession
{
    /// <summary>The script a person would type: ask the model one question and read its text.</summary>
    public const string AskScript =
        "var reply = await Ctx.Chat.GetResponseAsync(new[] { new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, \"Where is order A-1001?\") });\n" +
        "reply.Text\n" +
        ":quit\n";

    public static async Task<(string Output, string Error)> RunAsync(
        IChatClient chat, string script, CancellationToken ct = default)
    {
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var host = new ReplHost(new ReplGlobals(ctx));
        var output = new StringWriter();
        var error = new StringWriter();
        await new ReplLoop(host, new StringReader(script), output, error).RunAsync(ct);
        return (output.ToString(), error.ToString());
    }

    /// <summary>The last value the REPL printed, without the prompts that precede it on the same line.</summary>
    public static string LastValue(string replOutput) =>
        replOutput.Split('\n')
            .Skip(1) // banner
            .Select(line => line.Trim().TrimStart('>', ' '))
            .LastOrDefault(line => line.Length > 0) ?? "";
}
