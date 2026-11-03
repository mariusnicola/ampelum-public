using Microsoft.Extensions.AI;

namespace Shop;

public sealed class Notifier(IChatClient chat)
{
    public async Task<string> SummariseAsync(string orderText)
    {
        var reply = await chat.GetResponseAsync(orderText);
        return reply.Text;
    }
}
