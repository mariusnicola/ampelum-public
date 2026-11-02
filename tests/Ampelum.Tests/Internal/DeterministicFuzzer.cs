using System.Text;

namespace Ampelum.Tests.Internal;

internal static class DeterministicFuzzer
{
    const int Seed = 424242;
    const int CorpusSize = 1024;

    public static IReadOnlyList<string> ChatCorpus() => Build(BuildChatSample);

    public static IReadOnlyList<string> JsonCorpus() => Build(BuildJsonSample);

    static IReadOnlyList<string> Build(Func<Random, int, string> factory)
    {
        var rng = new Random(Seed);
        var list = new List<string>(CorpusSize);
        for (var i = 0; i < CorpusSize; i++) list.Add(factory(rng, i));
        return list;
    }

    static string BuildChatSample(Random rng, int idx)
    {
        var sb = new StringBuilder();
        switch (idx % 8)
        {
            case 0: sb.Append("[[ ## Summary ## ]]\nA fox.\n[[ ## Sentiment ## ]]\nneutral\n[[ ## WordCount ## ]]\n9"); break;
            case 1: sb.Append("[[ ## Summary ## ]]\r\nWith CRLF.\r\n[[ ## Sentiment ## ]]\r\npos\r\n[[ ## WordCount ## ]]\r\n3"); break;
            case 2: sb.Append("Garbage prefix\n[[ ## Summary ## ]]\nbody\n[[ ## Sentiment ## ]]\nneg\n[[ ## WordCount ## ]]\n0"); break;
            case 3: sb.Append("[[ ## Summary ## ]]\n\nblank-line body\n\n[[ ## Sentiment ## ]]\nneutral\n[[ ## WordCount ## ]]\n2"); break;
            case 4: sb.Append("[[ ## Summary ## ]]\n[[ inline brackets ]] inside body\n[[ ## Sentiment ## ]]\nneutral\n[[ ## WordCount ## ]]\n5"); break;
            case 5: sb.Append("[[ ## Summary ## ]]\nrandom").Append(rng.Next(0, 10000)).Append("\n[[ ## Sentiment ## ]]\npos\n[[ ## WordCount ## ]]\n").Append(rng.Next(1, 100)); break;
            case 6: sb.Append("   [[ ## Summary ## ]]   \n  trimmed    \n[[ ## Sentiment ## ]]\nneutral\n[[ ## WordCount ## ]]\n7"); break;
            default: sb.Append("[[ ## Summary ## ]]\nfinal\n[[ ## Sentiment ## ]]\npos\n[[ ## WordCount ## ]]\n").Append(idx); break;
        }
        return sb.ToString();
    }

    static string BuildJsonSample(Random rng, int idx)
    {
        var word = rng.Next(0, 100);
        return (idx % 6) switch
        {
            0 => $$"""{"summary":"A fox.","sentiment":"neutral","wordCount":{{word}}}""",
            1 => $$"""{"Summary":"PascalCase","Sentiment":"pos","WordCount":{{word}}}""",
            2 => $$"""prose around {"summary":"lenient","sentiment":"neg","wordCount":{{word}}} trailing prose""",
            3 => $$"""{"summary":"with \"quotes\"","sentiment":"neutral","wordCount":{{word}}}""",
            4 => $$"""{"summary":"unicode é ñ ✓","sentiment":"pos","wordCount":{{word}}}""",
            _ => $$"""{"summary":"row{{idx}}","sentiment":"neutral","wordCount":{{word}}}""",
        };
    }
}
