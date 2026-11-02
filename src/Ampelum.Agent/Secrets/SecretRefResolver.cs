using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Resolves secret-ref fields in tool arguments using ISecretStore.</summary>
public sealed class SecretRefResolver
{
    private readonly ISecretStore _store;

    public SecretRefResolver(ISecretStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Extract secret-ref field names from a tool's args schema.
    /// Returns a set of argument names that have format: secret-ref.</summary>
    public static IReadOnlySet<string> ExtractSecretRefFields(JsonElement? argsSchema)
    {
        var secretRefFields = new HashSet<string>(StringComparer.Ordinal);

        if (argsSchema is null || argsSchema.Value.ValueKind != JsonValueKind.Object)
            return secretRefFields;

        // Look for properties section
        if (!argsSchema.Value.TryGetProperty("properties", out var propertiesElem) ||
            propertiesElem.ValueKind != JsonValueKind.Object)
            return secretRefFields;

        // Iterate through each property
        foreach (var property in propertiesElem.EnumerateObject())
        {
            var propName = property.Name;
            var propValue = property.Value;

            // Check if this property has format: secret-ref
            if (propValue.TryGetProperty("format", out var formatElem) &&
                formatElem.ValueKind == JsonValueKind.String)
            {
                var format = formatElem.GetString();
                if (format == "secret-ref")
                {
                    secretRefFields.Add(propName);
                }
            }
        }

        return secretRefFields;
    }

    /// <summary>Resolve all secret-ref fields in the args, replacing aliases with actual values.
    /// Returns a new dictionary with resolved values.
    /// Throws SecretNotFoundException if a secret-ref field contains a non-existent alias.</summary>
    public async Task<Dictionary<string, JsonElement>> ResolveSecretsAsync(
        IReadOnlySet<string> secretRefFields,
        IReadOnlyDictionary<string, JsonElement> args,
        string jti,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, JsonElement>(args.Count);

        // Copy all args
        foreach (var kvp in args)
        {
            result[kvp.Key] = kvp.Value;
        }

        // Resolve secret-ref fields
        foreach (var fieldName in secretRefFields)
        {
            if (!args.TryGetValue(fieldName, out var aliasElement) ||
                aliasElement.ValueKind == JsonValueKind.Null)
            {
                continue; // Field not provided — skip
            }

            if (aliasElement.ValueKind != JsonValueKind.String)
            {
                continue; // Not a string — skip (schema validation responsibility)
            }

            var alias = aliasElement.GetString();
            if (string.IsNullOrEmpty(alias))
                continue;

            // Resolve the secret value
            var value = await _store.ResolveAsync(alias, jti, ct);

            // Replace the alias with the resolved value in the result
            result[fieldName] = JsonSerializer.SerializeToElement(value);
        }

        return result;
    }

    /// <summary>Zero out (clear) resolved secret values from a dictionary.
    /// This is called after tool execution to prevent secrets from lingering in memory.
    /// Only clears fields that were secret-ref fields.</summary>
    public static void ClearResolvedSecrets(
        IReadOnlySet<string> secretRefFields,
        Dictionary<string, JsonElement> resolvedArgs)
    {
        foreach (var fieldName in secretRefFields)
        {
            if (resolvedArgs.TryGetValue(fieldName, out var elem) &&
                elem.ValueKind == JsonValueKind.String)
            {
                var value = elem.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    // Overwrite with empty string
                    resolvedArgs[fieldName] = JsonSerializer.SerializeToElement(string.Empty);
                }
            }
        }
    }
}
