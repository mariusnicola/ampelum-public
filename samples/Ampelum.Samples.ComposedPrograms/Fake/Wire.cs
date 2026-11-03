using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ampelum.Samples.ComposedPrograms.Fake;

/// <summary>Reads input fields out of prompts and writes replies in the formats the adapters expect.</summary>
public static partial class Wire
{
    [GeneratedRegex(@"\[\[ ## (?<name>\w+) ## \]\]\s*(?<value>.*?)(?=\[\[ ## |\z)", RegexOptions.Singleline)]
    private static partial Regex Marker();

    /// <summary>Input fields of a ChatAdapter user message.</summary>
    public static IReadOnlyDictionary<string, string> ChatFields(string text) =>
        Marker().Matches(text).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value.Trim());

    /// <summary>Input fields of an XMLAdapter user message.</summary>
    public static IReadOnlyDictionary<string, string> XmlFields(string text) =>
        XElement.Parse(text).Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);

    /// <summary>A ChatAdapter reply.</summary>
    public static string Chat(params (string Field, string Value)[] fields)
    {
        var sb = new StringBuilder();
        foreach (var (field, value) in fields)
            sb.Append("[[ ## ").Append(field).Append(" ## ]]\n").Append(value).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>An XMLAdapter reply.</summary>
    public static string Xml(params (string Field, string Value)[] fields) =>
        new XElement("Outputs", fields.Select(f => new XElement(f.Field, f.Value))).ToString(SaveOptions.DisableFormatting);
}
