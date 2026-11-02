using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using Company.WebApi.Signatures;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAmpelum();

var app = builder.Build();

app.MapPost("/ask", async (Predict<QASignature> predict, AskRequest request, CancellationToken ct) =>
{
    var result = await predict.RunAsync(new QASignature { Question = request.Question }, ct);
    return Results.Ok(new { answer = result.Answer });
});

app.Run();

public sealed record AskRequest(string Question);

public partial class Program;
