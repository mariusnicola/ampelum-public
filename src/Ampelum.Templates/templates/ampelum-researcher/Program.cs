using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Company.Research.Signatures;
using Spectre.Console;

// Researcher template — runs Predict<SummariseSignature> against a FakeChatClient
// for a smooth offline experience. Swap to a real IChatClient (env-driven) for
// live runs. BootstrapFewShot integration is sketched in the README.

var fake = new FakeChatClient();
fake.EnqueueText("Cats are domesticated felines kept as companions worldwide.");
using var ctx = AmpelumContext.ForTests(fake);

var predict = Predict.Create<SummariseSignature>();
var result = await predict.RunAsync(new SummariseSignature
{
    Article = "Cats (Felis catus) are small domesticated carnivorous mammals...",
});

AnsiConsole.MarkupLine("[bold]## Result[/]");
AnsiConsole.MarkupLine($"- summary: [green]{Markup.Escape(result.Summary)}[/]");
AnsiConsole.MarkupLine("- bootstrap-trace: [grey]see README to wire BootstrapFewShot[/]");
