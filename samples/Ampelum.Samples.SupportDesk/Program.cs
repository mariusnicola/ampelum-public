using Ampelum.Samples.Shared;
using Ampelum.Samples.SupportDesk;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Budget;

IChatClient chat = args.Contains("--fake")
    ? FakeScript.HappyPath()
    : SampleConfig.Load().CreateChatClient();

using var desk = new SupportDeskPipeline(
    chat,
    SupportDeskData.KnowledgeBase,
    SupportDeskData.Orders,
    Budget.Create().WithCallLimit(12).Build(),
    new InMemoryAuditSink(),
    SupportDeskData.ReplyPolicy);

var result = await desk.HandleAsync(SupportDeskData.SampleTicket);

Console.WriteLine($"Ticket:  {result.Ticket.Id} {result.Ticket.Text}");
Console.WriteLine($"Triage:  {result.Triage.Category} / {result.Triage.Urgency} / {result.Triage.Sentiment}");
Console.WriteLine($"Sources: {string.Join(", ", result.KnowledgeSources)}");
Console.WriteLine($"Order:   {result.OrderStatus}");
Console.WriteLine($"Reply:   {result.Reply}");
Console.WriteLine($"Audit:   {result.AuditEntries} entries, chain valid = {result.AuditChain.IsValid}");
return result.AuditChain.IsValid ? 0 : 1;
