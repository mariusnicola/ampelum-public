using System.Text;
using System.Xml;
using System.Xml.Linq;
using SignalGrid.Ampelum.Exceptions;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Adapters;

/// <summary>
/// Adapter that formats inputs and parses outputs as XML sections.
/// Does NOT implement <see cref="IStreamingAdapter"/> â€” use <see cref="ChatAdapter"/> for streaming.
/// </summary>
/// <remarks>
/// <b>Spec reconciliation:</b> spec Â§5.6.1 shows <c>Parse(SignatureMetadata, string)</c>;
/// this implementation conforms to the shipped <see cref="IAdapter"/> contract which takes
/// <see cref="ChatResponse"/>. Text is extracted via <see cref="ChatResponse.Text"/>.
/// </remarks>
public sealed class XMLAdapter : IAdapter
{
    /// <summary>Controls how missing output fields are handled during <see cref="Parse"/>.</summary>
    public enum ParseMode
    {
        /// <summary>Missing output field raises <see cref="AdapterParseException"/>.</summary>
        Strict,

        /// <summary>Missing output field is returned as <see langword="null"/>.</summary>
        Lenient,
    }

    readonly ParseMode _mode;

    /// <summary>Initialises a new <see cref="XMLAdapter"/>.</summary>
    /// <param name="parseMode">
    /// When <see cref="ParseMode.Strict"/> (default), a missing output field raises <see cref="AdapterParseException"/>.
    /// When <see cref="ParseMode.Lenient"/>, a missing output field is returned as <see langword="null"/>.
    /// </param>
    public XMLAdapter(ParseMode parseMode = ParseMode.Lenient) => _mode = parseMode;

    /// <inheritdoc/>
    public IReadOnlyList<ChatMessage> Format(
        SignatureMetadata signature,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyList<Example> demos)
    {
        var system = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(signature.Instructions))
            system.AppendLine(signature.Instructions).AppendLine();
        system.AppendLine("Respond with XML in the format: <Outputs><FieldName>value</FieldName>...</Outputs>");

        var msgs = new List<ChatMessage> { new(ChatRole.System, system.ToString()) };

        foreach (var demo in demos)
        {
            msgs.Add(new ChatMessage(ChatRole.User,
                RenderXml("Inputs", signature.Inputs.Select(f => f.Name), demo.Inputs)));
            msgs.Add(new ChatMessage(ChatRole.Assistant,
                RenderXml("Outputs", signature.Outputs.Select(f => f.Name), demo.Outputs)));
        }

        msgs.Add(new ChatMessage(ChatRole.User,
            RenderXml("Inputs", signature.Inputs.Select(f => f.Name), inputs)));
        return msgs;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata signature, ChatResponse response)
    {
        var text = response.Text ?? "";
        XDocument doc;
        try
        {
            // Find the first <Outputs>...</Outputs> occurrence â€” providers may prefix commentary.
            var start = text.IndexOf("<Outputs>", StringComparison.Ordinal);
            var end = text.LastIndexOf("</Outputs>", StringComparison.Ordinal);
            if (start < 0 || end < 0 || end < start)
                throw new AdapterParseException(signature.SignatureType.Name, nameof(XMLAdapter), text,
                    "response did not contain a well-formed <Outputs>...</Outputs> block");
            var xml = text[start..(end + "</Outputs>".Length)];
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new AdapterParseException(signature.SignatureType.Name, nameof(XMLAdapter), text,
                $"malformed XML: {ex.Message}");
        }

        var root = doc.Root!;
        var result = new Dictionary<string, object?>(signature.Outputs.Count);
        foreach (var field in signature.Outputs)
        {
            var el = root.Elements().FirstOrDefault(e => e.Name.LocalName == field.Name);
            if (el is null)
            {
                if (_mode == ParseMode.Strict)
                    throw new AdapterParseException(signature.SignatureType.Name, nameof(XMLAdapter), text,
                        $"missing required output field '{field.Name}'");
                result[field.Name] = null;
            }
            else
            {
                result[field.Name] = ConvertTo(el.Value, field.ClrType);
            }
        }
        return result;
    }

    static string RenderXml(string root, IEnumerable<string> fieldNames, IReadOnlyDictionary<string, object?> values)
    {
        var sb = new StringBuilder();
        sb.Append('<').Append(root).Append('>');
        foreach (var name in fieldNames)
        {
            sb.Append('<').Append(name).Append('>');
            if (values.TryGetValue(name, out var v) && v is not null)
                sb.Append(System.Security.SecurityElement.Escape(v.ToString() ?? ""));
            sb.Append("</").Append(name).Append('>');
        }
        sb.Append("</").Append(root).Append('>');
        return sb.ToString();
    }

    static object? ConvertTo(string raw, Type t)
    {
        if (t == typeof(string)) return raw;
        if (t == typeof(int)) return int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (t == typeof(long)) return long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (t == typeof(double)) return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (t == typeof(bool)) return bool.Parse(raw);
        return Convert.ChangeType(raw, t, System.Globalization.CultureInfo.InvariantCulture);
    }
}
