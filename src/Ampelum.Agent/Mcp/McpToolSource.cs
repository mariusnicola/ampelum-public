namespace SignalGrid.Ampelum.Agent;

/// <summary>Wraps an external MCP server as an IToolSource.
/// Tools are prefixed with the server URI to prevent shadow-tool attacks.
///
/// <para><b>Governance trust model:</b> a remote MCP server's self-declared
/// `_meta.dangerTier` / `_meta.requiresConfirmation` annotations (see
/// <see cref="HttpMcpTransport"/>) are ADVISORY ONLY unless an <see cref="IToolManifestVerifier"/>
/// is configured. Nothing stops a malicious or compromised server from declaring every tool
/// <see cref="DangerTier.Safe"/> with <c>requiresConfirmation: false</c>. Therefore:
/// <list type="bullet">
/// <item>When a discovered tool's declared tier is absent or fails to parse, it fails safe to
/// <see cref="DangerTier.Catastrophic"/> — this is the single place that decision is made (the
/// wire-level transport deliberately does not pick its own fallback; see
/// <see cref="HttpMcpTransport"/>'s <c>ReadGovernanceAnnotations</c>).</item>
/// <item>When live discovery (<see cref="IMcpTransport"/>) is used WITHOUT an
/// <see cref="IToolManifestVerifier"/>, every discovered tool's declared tier is untrusted
/// regardless of whether it parses — the server could just as easily declare a false "Safe" as
/// an unparseable string. In that configuration every discovered tool is capped to
/// <see cref="DangerTier.Catastrophic"/>, ignoring what the server claims. Attaching a verifier
/// restores trust in the server's self-declared tier for tools the verifier accepts.</item>
/// <item>Manifests registered directly via <see cref="RegisterManifest"/> are not affected by
/// this cap — they come from a caller inside this process, not an untrusted remote server.</item>
/// </list>
/// </para></summary>
public sealed class McpToolSource : IMcpToolSource
{
    private readonly McpServerConnection _connection;
    private readonly IMcpTransport? _transport;
    private readonly IToolManifestVerifier? _verifier;
    private readonly List<ToolManifest> _loadedManifests = [];
    private readonly Dictionary<string, ToolManifest> _discoveredManifests = new(StringComparer.Ordinal);

    // Which source registered each handler name on a dispatcher, so a second server on the same host
    // cannot silently take over the first one's handlers.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolDispatcher, Dictionary<string, McpToolSource>> HandlerOwners = new();

    public string SourcePrefix { get; }
    public TrustLevel ConnectionTrust => _connection.AssignedTrust;

    /// <summary>Manifest-only source: tools enter exclusively via <see cref="RegisterManifest"/>.
    /// No live transport is attached, so <see cref="SurveyAsync"/> never performs network I/O.</summary>
    public McpToolSource(McpServerConnection connection, IToolManifestVerifier? verifier = null)
        : this(connection, transport: null, verifier)
    {
    }

    /// <summary>Live source: <see cref="SurveyAsync"/> calls <paramref name="transport"/>'s
    /// `tools/list` in addition to any manifests registered via <see cref="RegisterManifest"/>.
    /// Manually registered manifests take precedence over discovered ones of the same name.</summary>
    public McpToolSource(McpServerConnection connection, IMcpTransport? transport, IToolManifestVerifier? verifier = null)
    {
        _connection = connection;
        _transport = transport;
        // Prefix: sanitize URI to a safe prefix
        SourcePrefix = "mcp/" + SanitizeHostFromUri(_connection.ServerUri);
        _verifier = verifier;
    }

    public async Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default)
    {
        var manifests = new List<ToolManifest>(_loadedManifests);
        if (_transport is not null)
        {
            var discovered = await _transport.ListToolsAsync(ct).ConfigureAwait(false);
            foreach (var definition in discovered)
            {
                // Explicitly registered manifests win over live discovery for the same name.
                if (_loadedManifests.Any(m => m.Name == definition.Name)) continue;

                var manifest = ToManifest(definition);
                if (_verifier is not null)
                {
                    var verification = _verifier.Verify(manifest, SourcePrefix);
                    if (!verification.Trusted) continue;
                }
                else
                {
                    // No verifier configured: the server's self-declared governance
                    // annotations are unverifiable, so they are advisory only. Do not trust
                    // a self-declared "Safe" (or any other) tier at face value — cap every
                    // discovered tool to the most conservative tier regardless of what the
                    // server claims. See the class-level doc comment for the full rationale.
                    manifest = manifest with { DangerTier = DangerTier.Catastrophic, RequiresConfirmation = true };
                }
                _discoveredManifests[manifest.Name] = manifest;
                manifests.Add(manifest);
            }
        }

        return manifests.Select(m => new ToolSurvey
        {
            Name = m.Name,
            Description = m.Description,
            DangerTier = m.DangerTier,
            RequiresConfirmation = m.RequiresConfirmation
        }).ToList();
    }

    public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default)
    {
        var manifest = _loadedManifests.FirstOrDefault(m => m.Name == toolName)
            ?? (_discoveredManifests.TryGetValue(toolName, out var discovered) ? discovered : null);
        return Task.FromResult(manifest);
    }

    /// <summary>Makes the tools of this source callable through <paramref name="dispatcher"/>: for every surveyed
    /// tool it registers a handler under <c>SourcePrefix/name</c> (the name <see cref="ToolLoader"/> gives the
    /// tool) that sends <c>tools/call</c> through the transport. The dispatcher runs policy, confirmation and audit
    /// first and reaches the handler only for an allowed call, so a tool whose tier is blocked never gets a call
    /// on the wire. Returns the registered names.
    /// <para>Call it once per connection. A second call registers the same names again but does not remove a
    /// handler for a tool the server has since dropped.</para>
    /// <para><see cref="SourcePrefix"/> is the host of the server address only, so two servers on one host (for
    /// example 127.0.0.1 on two ports) get the same prefix and the same tool names. The call refuses to replace a
    /// handler that was registered by another source or by hand; give such servers separate dispatchers.</para></summary>
    /// <exception cref="InvalidOperationException">The source has no transport, or a handler name is already taken.</exception>
    public async Task<IReadOnlyList<string>> RegisterHandlersAsync(ToolDispatcher dispatcher, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var transport = _transport ?? throw new InvalidOperationException("A manifest-only source has no transport to call.");
        var surveys = await SurveyAsync(ct).ConfigureAwait(false);
        var owners = HandlerOwners.GetOrCreateValue(dispatcher);
        lock (owners)
        {
            foreach (var survey in surveys)
            {
                var taken = $"{SourcePrefix}/{survey.Name}";
                if (owners.TryGetValue(taken, out var owner) && !ReferenceEquals(owner, this))
                    throw new InvalidOperationException($"Handler '{taken}' is already registered by the source for '{owner._connection.ServerUri}'; this source is '{_connection.ServerUri}'.");
                if (!owners.ContainsKey(taken) && dispatcher.HasHandler(taken))
                    throw new InvalidOperationException($"Handler '{taken}' is already registered on the dispatcher; this source is '{_connection.ServerUri}'.");
            }
        }
        var names = new List<string>();
        foreach (var survey in surveys)
        {
            var remoteName = survey.Name;
            var fullName = $"{SourcePrefix}/{remoteName}";
            lock (owners) owners[fullName] = this;
            dispatcher.RegisterHandler(fullName, async (args, token) =>
            {
                var started = DateTimeOffset.UtcNow;
                var response = await transport.CallToolAsync(
                    remoteName, args.ToDictionary(a => a.Key, a => (object?)a.Value, StringComparer.Ordinal), token).ConfigureAwait(false);
                return new ToolResult(
                    fullName,
                    response.Success,
                    response.Success ? McpContent.Text(response.Result) : null,
                    response.Success ? null : response.Error ?? "Tool call failed.",
                    started,
                    DateTimeOffset.UtcNow - started);
            });
            names.Add(fullName);
        }
        return names;
    }

    /// <summary>Maps a wire-level MCP tool definition to the internal governance manifest.
    /// Unparseable danger tiers fall back to the most conservative tier (Catastrophic, blocked)
    /// rather than silently admitting an unclassified external tool.</summary>
    private ToolManifest ToManifest(McpToolDefinition definition)
    {
        if (!Enum.TryParse<DangerTier>(definition.DangerTier, ignoreCase: true, out var tier))
        {
            tier = DangerTier.Catastrophic;
        }
        return new ToolManifest
        {
            Name = definition.Name,
            Description = definition.Description,
            DangerTier = tier,
            // Reversibility is not part of the MCP wire schema; an externally discovered
            // tool of unknown reversibility is treated as irreversible (conservative default).
            Reversibility = Reversibility.Irreversible,
            RequiresConfirmation = definition.RequiresConfirmation,
            ArgsSchema = definition.InputSchema
        };
    }

    /// <summary>Register a manifest (used for testing or pre-loading from external server).
    /// Applies manifest verification if a verifier is configured.</summary>
    /// <remarks>Deliberately not part of <see cref="IMcpToolSource"/>: it is a manual/test-only
    /// registration path for this concrete implementation, not something every tool source (e.g.
    /// a future SDK-backed one) is required to support. Callers who need it hold the concrete
    /// <see cref="McpToolSource"/> type, same as today.</remarks>
    public bool RegisterManifest(ToolManifest manifest)
    {
        if (_verifier != null)
        {
            var result = _verifier.Verify(manifest, SourcePrefix);
            if (!result.Trusted) return false;
        }
        _loadedManifests.Add(manifest);
        return true;
    }

    /// <summary>Sanitize a URI's host to a safe prefix (replace dots with dashes).</summary>
    private static string SanitizeHostFromUri(string uriString)
    {
        try
        {
            var uri = new Uri(uriString);
            return uri.Host.Replace(".", "-");
        }
        catch
        {
            // Fallback: sanitize the entire string
            return uriString.Replace(".", "-").Replace("://", "-").Replace("/", "-");
        }
    }
}
