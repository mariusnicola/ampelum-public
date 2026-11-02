using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Safety.Policy;
using SignalGrid.Ampelum.Safety.PromptInjection;

namespace Ampelum.Samples.SupportDesk;

/// <summary>
/// Ticket in, checked and audited reply out: triage, knowledge lookup, order lookup, verified reply.
/// Safety exceptions from the hooks propagate so the caller decides what a rejected ticket means.
/// </summary>
public sealed class SupportDeskPipeline : IDisposable
{
    readonly IChatClient _chat;
    readonly IRetriever _retriever;
    readonly IReadOnlyDictionary<string, Order> _orders;
    readonly IAuditSink _audit;
    readonly SupportDeskOptions _options;
    readonly ServiceProvider _provider;

    public SupportDeskPipeline(
        IChatClient chat,
        IReadOnlyList<RetrievedDocument> knowledgeBase,
        IReadOnlyDictionary<string, Order> orders,
        IBudget budget,
        IAuditSink audit,
        ContentPolicy replyPolicy,
        SupportDeskOptions? options = null)
    {
        _chat = chat;
        _retriever = new InMemoryRetriever(knowledgeBase);
        _orders = orders;
        _audit = audit;
        _options = options ?? new SupportDeskOptions();

        var services = new ServiceCollection();
        services.AddSingleton(chat);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(audit);
        services.AddAmpelum()
            .UseAdapter<JsonAdapter>()
            .AddAuditHook();
        services.AddSingleton<IHook>(new BudgetHook(budget));
        services.AddSingleton<IHook>(new PromptInjectionHook(new HeuristicDetector()));
        services.AddSingleton<IHook>(new ContentPolicyHook(replyPolicy));
        _provider = services.BuildServiceProvider();
    }

    public async Task<SupportDeskResult> HandleAsync(SupportTicket ticket, CancellationToken ct = default)
    {
        var auditStart = _audit.NextSequence;
        using var scope = _provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        // RagAdvisor reads the ambient context to run its call through the hooks.
        using var ambient = AmpelumContext.Push(ctx);

        var triage = await TriageAsync(scope.ServiceProvider, ticket, ct);

        var advisor = new RagAdvisor(_retriever, _chat, new RagAdvisorOptions { TopK = _options.KnowledgeTopK });
        var knowledge = await advisor.AskAsync(ticket.Text, ct);

        var orderStatus = await LookupOrderAsync(ctx, ticket, ct);

        var reply = await ReplyAsync(ctx, ticket, triage, knowledge.Answer, orderStatus, ct);

        var chain = await VerifyAuditAsync(ct);
        return new SupportDeskResult(
            ticket, triage, knowledge.Answer, knowledge.Citations.Select(d => d.Id).ToArray(),
            orderStatus, reply, (int)(_audit.NextSequence - auditStart), chain);
    }

    /// <summary>Checks the whole audit chain; a tampered or reordered entry makes it invalid.</summary>
    public async Task<AuditChainVerificationResult> VerifyAuditAsync(CancellationToken ct = default) =>
        await AuditChainVerifier.VerifyAsync(_audit, ct);

    static async Task<Triage> TriageAsync(IServiceProvider sp, SupportTicket ticket, CancellationToken ct)
    {
        var predict = sp.GetRequiredService<Predict<TriageSignature>>();
        var raw = (await predict.ForwardAsync(new TriageSignature { TicketText = ticket.Text }, ct: ct)).Value;
        return new Triage(
            ParseLabel<TicketCategory>(raw.Category),
            ParseLabel<TicketUrgency>(raw.Urgency),
            ParseLabel<TicketSentiment>(raw.Sentiment));
    }

    async Task<string> LookupOrderAsync(AmpelumContext ctx, SupportTicket ticket, CancellationToken ct)
    {
        var lookup = AIFunctionFactory.Create(FindOrder, "lookup_order", "Returns the status of an order by its id.");
        var react = new ReAct<OrderLookupSignature>(ctx, [lookup], _options.MaxToolIterations);
        var result = await react.ForwardAsync(
            new OrderLookupSignature { Question = $"Which order does this ticket refer to and what is its status? Ticket: {ticket.Text}" },
            ct: ct);
        return result.Value.Answer;
    }

    string FindOrder([Description("The order id, for example A-1001.")] string orderId) =>
        _orders.TryGetValue(orderId.Trim(), out var order)
            ? $"Order {order.Id}: {order.Status}, carrier {order.Carrier}, delivery in {order.Eta}."
            : $"No order with id {orderId}.";

    async Task<string> ReplyAsync(
        AmpelumContext ctx, SupportTicket ticket, Triage triage, string knowledge, string orderStatus, CancellationToken ct)
    {
        var generator = new Predict<ReplySignature>(ctx);
        var verified = new VerifiedPredict<ReplySignature>(
            generator,
            new SchemaCriticPolicy<ReplySignature>(),
            new VerifiedPredictOptions { MaxRetries = _options.ReplyRetries });
        var input = new Example(
            new Dictionary<string, object?>
            {
                [nameof(ReplySignature.TicketText)] = ticket.Text,
                [nameof(ReplySignature.Category)] = triage.Category.ToString(),
                [nameof(ReplySignature.KnowledgeAnswer)] = knowledge,
                [nameof(ReplySignature.OrderStatus)] = orderStatus,
            },
            new Dictionary<string, object?>());
        var approved = await verified.PredictAsync(input, ctx, ct);
        return (string)approved.Outputs[nameof(ReplySignature.Reply)]!;
    }

    static T ParseLabel<T>(string raw) where T : struct, Enum =>
        Enum.TryParse<T>(raw.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new InvalidOperationException($"Model returned '{raw}', which is not a valid {typeof(T).Name}.");

    public void Dispose() => _provider.Dispose();
}
