using SignalGrid.Ampelum;

// Without this assembly attribute the generator emits only the metadata
// registration. With it, it also emits typed property getters.
[assembly: GenerateSignatureMetadata]

var metadata = SignatureMetadataCache.For<Summarize>();
Console.WriteLine($"instructions: {metadata.Instructions}");
Console.WriteLine($"inputs: {string.Join(", ", metadata.Inputs.Select(f => f.Name))}");
Console.WriteLine($"outputs: {string.Join(", ", metadata.Outputs.Select(f => f.Name))}");

if (!SignatureMetadataCache.TryGetAccessors(typeof(Summarize), out var accessors) || accessors is null)
{
    Console.Error.WriteLine("no generated accessors registered");
    return 1;
}

var input = new Summarize { Passage = "Generated code runs when the assembly loads." };
var read = ((Func<Summarize, object?>)accessors[0])(input);
Console.WriteLine($"accessor {metadata.Inputs[0].Name} -> {read}");

return read as string == input.Passage ? 0 : 1;
