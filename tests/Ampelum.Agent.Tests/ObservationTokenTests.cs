using System.Text.Json;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

/// <summary>Comprehensive tests for the Observation Token system (Phase 20).
/// Covers: InMemoryTokenStore, ToolDispatcher, ToolLoader, schema injection,
/// safe defaults, path mismatch, expiry, session mismatch, exemption handling,
/// ListToken absent-file verification, and MissingObservationTokenException.</summary>
public sealed class ObservationTokenTests
{
    // ---- InMemoryTokenStore Tests ----

    [Fact]
    public void InMemoryTokenStore_StoreAndResolve_Roundtrips()
    {
        var store = new InMemoryTokenStore();
        var token = MakeToken("t1", "/path/file.txt", "read_file");

        store.Store(token);
        var resolved = store.Resolve("t1");

        resolved.ShouldNotBeNull();
        resolved!.ResourcePath.ShouldBe("/path/file.txt");
        resolved.IssuedByTool.ShouldBe("read_file");
    }

    [Fact]
    public void InMemoryTokenStore_Resolve_Missing_ReturnsNull()
    {
        var store = new InMemoryTokenStore();
        store.Resolve("nonexistent").ShouldBeNull();
    }

    [Fact]
    public void InMemoryTokenStore_PurgeSession_ClearsAll()
    {
        var store = new InMemoryTokenStore();
        store.Store(MakeToken("t1", "a", "read"));
        store.Store(MakeToken("t2", "b", "list"));

        store.PurgeSession();
        store.Resolve("t1").ShouldBeNull();
        store.Resolve("t2").ShouldBeNull();
    }

    [Fact]
    public void InMemoryTokenStore_NextCallIndex_Increments()
    {
        var store = new InMemoryTokenStore();
        store.NextCallIndex().ShouldBe(1);
        store.NextCallIndex().ShouldBe(2);
        store.NextCallIndex().ShouldBe(3);
        store.CallIndex.ShouldBe(3);
    }

    [Fact]
    public void InMemoryTokenStore_Consume_RemovesToken()
    {
        var store = new InMemoryTokenStore();
        store.Store(MakeToken("t1", "a", "read"));
        store.Consume("t1").ShouldBeTrue();
        store.Resolve("t1").ShouldBeNull();
        store.Consume("t1").ShouldBeFalse();
    }

    // ---- InMemoryTokenStore Edge Cases ----

    [Fact]
    public void InMemoryTokenStore_Store_NullToken_Throws()
    {
        var store = new InMemoryTokenStore();
        Should.Throw<ArgumentNullException>(() => store.Store(null!));
    }

    [Fact]
    public void InMemoryTokenStore_Resolve_NullTokenId_Throws()
    {
        var store = new InMemoryTokenStore();
        Should.Throw<ArgumentNullException>(() => store.Resolve(null!));
    }

    // ---- ToolDispatcher Tests ----

    [Fact]
    public async Task ToolDispatcher_SafeTool_NoTokenRequired()
    {
        var (store, identity) = CreateDefaultStore();
        var loader = new StubLoader(CreateSafeManifest("read_file"));
        var dispatcher = new SingleManifestDispatcher(loader, store, identity);
        dispatcher.RegisterHandler("read_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "content", null, PolicyDecision.Allowed)));

        var result = await dispatcher.InvokeAsync("read_file", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/path/file.txt")
        }, identity);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ToolDispatcher_SafeDefault_MutatingTool_RequiresToken()
    {
        var (store, identity) = CreateDefaultStore();
        var manifest = CreateManifest("edit_file", DangerTier.Destructive, Reversibility.Reversible,
            issuesToken: false, requiresToken: false);
        var loader = new StubLoader(manifest);
        var dispatcher = new SingleManifestDispatcher(loader, store, identity);
        dispatcher.RegisterHandler("edit_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "edited", null, PolicyDecision.Allowed)));

        Should.Throw<MissingObservationTokenException>(() =>
            dispatcher.InvokeAsync("edit_file", new Dictionary<string, JsonElement>
            {
                ["filePath"] = JsonSerializer.SerializeToElement("/path/file.txt")
            }, identity));
    }

    [Fact]
    public async Task ToolDispatcher_SafeDefault_InvalidToken_Fails()
    {
        var (store, identity) = CreateDefaultStore();
        var manifest = CreateManifest("edit_file", DangerTier.Destructive, Reversibility.Reversible,
            issuesToken: false, requiresToken: false);
        var loader = new StubLoader(manifest);
        var dispatcher = new SingleManifestDispatcher(loader, store, identity);
        dispatcher.RegisterHandler("edit_file", (args, _) =>
        {
            args.ContainsKey("observationToken").ShouldBeFalse();
            return Task.FromResult(new ToolResult(true, "edited", null, PolicyDecision.Allowed));
        });

        var result = await dispatcher.InvokeAsync("edit_file", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/path/file.txt"),
            ["observationToken"] = JsonSerializer.SerializeToElement("manual-token")
        }, identity);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("invalid");
    }

    [Fact]
    public async Task ToolDispatcher_ReadTool_IssuesToken()
    {
        var (store, identity) = CreateDefaultStore();
        var manifest = CreateManifest("read_file", DangerTier.Safe, Reversibility.Reversible,
            issuesToken: true, requiresToken: false,
            tokenType: TokenType.ReadToken, resourcePathArg: "filePath");
        var loader = new StubLoader(manifest);
        var dispatcher = new SingleManifestDispatcher(loader, store, identity);
        dispatcher.RegisterHandler("read_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "content", null, PolicyDecision.Allowed)));

        var result = await dispatcher.InvokeAsync("read_file", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/path/file.txt")
        }, identity);

        result.Success.ShouldBeTrue();

        store.CallIndex.ShouldBe(1);

        var allTokens = store.GetAllTokens();
        allTokens.Count.ShouldBe(1);
        allTokens[0].ResourcePath.ShouldBe("/path/file.txt");
        allTokens[0].IssuedByTool.ShouldBe("read_file");
    }

    [Fact]
    public async Task ToolDispatcher_PathMismatch_Blocks()
    {
        var (store, identity) = CreateDefaultStore();

        // Create both manifests and use a MultiManifestDispatcher
        var readManifest = CreateManifest("read_file", DangerTier.Safe, Reversibility.Reversible,
            issuesToken: true, requiresToken: false,
            tokenType: TokenType.ReadToken, resourcePathArg: "filePath");

        var editManifest = CreateManifest("edit_file", DangerTier.Destructive, Reversibility.Reversible,
            issuesToken: false, requiresToken: true,
            tokenType: TokenType.ReadToken, resourcePathArg: "filePath",
            argName: "readToken");

        // Use a single dispatcher that knows about both tools
        var dispatcher = new MultiManifestDispatcher(
            [("read_file", readManifest), ("edit_file", editManifest)],
            store, identity);
        dispatcher.RegisterHandler("read_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "content", null, PolicyDecision.Allowed)));
        dispatcher.RegisterHandler("edit_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "edited", null, PolicyDecision.Allowed)));

        // Issue token for /path/file.txt
        await dispatcher.InvokeAsync("read_file", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/path/file.txt")
        }, identity);

        // Find the issued token
        var tokens = store.GetAllTokens();
        var tokenId = tokens[0].TokenId;

        var result = await dispatcher.InvokeAsync("edit_file", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/different/path.txt"),
            ["readToken"] = JsonSerializer.SerializeToElement(tokenId)
        }, identity);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("path mismatch");
    }

    [Fact]
    public async Task ToolDispatcher_SessionMismatch_Blocks()
    {
        var (store, identity) = CreateDefaultStore();

        // Store a token from a different session
        var differentSession = new AgentIdentity("test", "session-999", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var token = MakeToken("external-token", "/path/file.txt", "read_file", differentSession.InstanceId);
        store.Store(token);

        var manifest = CreateManifest("edit_file", DangerTier.Destructive, Reversibility.Reversible,
            issuesToken: false, requiresToken: true,
            tokenType: TokenType.ReadToken, resourcePathArg: "filePath",
            argName: "readToken");
        var loader = new StubLoader(manifest);
        var dispatcher = new SingleManifestDispatcher(loader, store, identity);
        dispatcher.RegisterHandler("edit_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "edited", null, PolicyDecision.Allowed)));

        var result = await dispatcher.InvokeAsync("edit_file", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/path/file.txt"),
            ["readToken"] = JsonSerializer.SerializeToElement("external-token")
        }, identity);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("different session");
    }

    [Fact]
    public async Task ToolDispatcher_ExpiredToken_Blocks()
    {
        var (store, identity) = CreateDefaultStore();

        var expiredToken = new ObservationToken(
            "expired", "/path/file.txt", "read_file",
            identity.InstanceId, 0, 0);
        store.Store(expiredToken);
        store.NextCallIndex();

        var manifest = CreateManifest("edit_file", DangerTier.Destructive, Reversibility.Reversible,
            issuesToken: false, requiresToken: true,
            tokenType: TokenType.ReadToken, resourcePathArg: "filePath",
            argName: "readToken");
        var loader = new StubLoader(manifest);
        var dispatcher = new SingleManifestDispatcher(loader, store, identity);
        dispatcher.RegisterHandler("edit_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "edited", null, PolicyDecision.Allowed)));

        var result = await dispatcher.InvokeAsync("edit_file", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/path/file.txt"),
            ["readToken"] = JsonSerializer.SerializeToElement("expired")
        }, identity);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("expired");
    }

    [Fact]
    public async Task ToolDispatcher_ListToken_AbsentPathVerification()
    {
        var (store, identity) = CreateDefaultStore();

        var listManifest = CreateManifest("list_directory", DangerTier.Safe, Reversibility.Reversible,
            issuesToken: true, requiresToken: false,
            tokenType: TokenType.ListToken, resourcePathArg: "dirPath");

        var writeManifest = CreateManifest("write_file", DangerTier.Destructive, Reversibility.Reversible,
            issuesToken: false, requiresToken: true,
            tokenType: TokenType.ListToken, resourcePathArg: "dirPath",
            argName: "listToken", verifyPathAbsent: "filePath");

        var dispatcher = new MultiManifestDispatcher(
            [("list_directory", listManifest), ("write_file", writeManifest)],
            store, identity);
        dispatcher.RegisterHandler("list_directory", (_, _) =>
            Task.FromResult(new ToolResult(true, "existing.txt\nexisting2.txt", null, PolicyDecision.Allowed)));
        dispatcher.RegisterHandler("write_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "written", null, PolicyDecision.Allowed)));

        // Issue ListToken by listing /path
        await dispatcher.InvokeAsync("list_directory", new Dictionary<string, JsonElement>
        {
            ["dirPath"] = JsonSerializer.SerializeToElement("/path")
        }, identity);

        var tokens = store.GetAllTokens();
        var tokenId = tokens[0].TokenId;

        // write a file that's in the listing — should be blocked
        var result = await dispatcher.InvokeAsync("write_file", new Dictionary<string, JsonElement>
        {
            ["dirPath"] = JsonSerializer.SerializeToElement("/path"),
            ["filePath"] = JsonSerializer.SerializeToElement("existing.txt"),
            ["listToken"] = JsonSerializer.SerializeToElement(tokenId)
        }, identity);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("already exists");

        // write a NEW file not in the listing — should succeed
        var result2 = await dispatcher.InvokeAsync("write_file", new Dictionary<string, JsonElement>
        {
            ["dirPath"] = JsonSerializer.SerializeToElement("/path"),
            ["filePath"] = JsonSerializer.SerializeToElement("new_file.txt"),
            ["listToken"] = JsonSerializer.SerializeToElement(tokenId)
        }, identity);

        result2.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ToolDispatcher_SingleToken_DirectExpiryCheck()
    {
        // Direct test: token with issuedAt=1, expiresAt=2 should be valid when CallIndex <= 2
        var (store, identity) = CreateDefaultStore();

        // Issue a token with normal expiry (10)
        var readManifest = CreateManifest("read_config", DangerTier.Safe, Reversibility.Reversible,
            issuesToken: true, requiresToken: false,
            tokenType: TokenType.ReadToken, resourcePathArg: "path");

        // A token-requiring tool
        var modifyManifest = new ToolManifest
        {
            Name = "modify_config",
            Description = "Modifies a configuration file",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Reversible,
            RequiresObservationToken = new ObservationRequirement
            {
                ArgName = "readToken",
                AcceptedTypes = [TokenType.ReadToken],
                ResourcePathArg = "configPath"
            }
        };

        var dispatcher = new MultiManifestDispatcher(
            [("read_config", readManifest), ("modify_config", modifyManifest)],
            store, identity, defaultTokenExpiry: 10);
        dispatcher.RegisterHandler("read_config", (_, _) =>
            Task.FromResult(new ToolResult(true, "config", null, PolicyDecision.Allowed)));
        dispatcher.RegisterHandler("modify_config", (_, _) =>
            Task.FromResult(new ToolResult(true, "modified", null, PolicyDecision.Allowed)));

        // Issue a token (CallIndex = 1)
        await dispatcher.InvokeAsync("read_config", new Dictionary<string, JsonElement>
        {
            ["path"] = JsonSerializer.SerializeToElement("/etc/config.yaml")
        }, identity);

        var tokenId = store.GetAllTokens()[0].TokenId;

        // The token was issued at CallIndex=1, expires at 1+10=11.
        // Use it — should succeed
        var result = await dispatcher.InvokeAsync("modify_config", new Dictionary<string, JsonElement>
        {
            ["configPath"] = JsonSerializer.SerializeToElement("/etc/config.yaml"),
            ["readToken"] = JsonSerializer.SerializeToElement(tokenId)
        }, identity);

        result.Success.ShouldBeTrue();

        // Directly simulated expired token: create one with issuedAt=0, expiresAt=0
        var expiredId = "direct-expired";
        store.Store(new ObservationToken(expiredId, "/path", "read_file", identity.InstanceId, 0, 0));
        store.NextCallIndex(); // CallIndex = 3

        var result2 = await dispatcher.InvokeAsync("modify_config", new Dictionary<string, JsonElement>
        {
            ["configPath"] = JsonSerializer.SerializeToElement("/path"),
            ["readToken"] = JsonSerializer.SerializeToElement(expiredId)
        }, identity);

        result2.Success.ShouldBeFalse();
        result2.Error.ShouldNotBeNull();
        result2.Error.ShouldContain("expired");
    }

    [Fact]
    public async Task ToolDispatcher_Exemption_NewResourceCreation_SkipsValidation()
    {
        var (store, identity) = CreateDefaultStore();

        var manifest = new ToolManifest
        {
            Name = "create_file",
            Description = "Creates a new file",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Reversible,
            ObservationExemption = Agent.ObservationExemption.NewResourceCreation
        };

        var loader = new StubLoader(manifest);
        var dispatcher = new SingleManifestDispatcher(loader, store, identity);
        dispatcher.RegisterHandler("create_file", (_, _) =>
            Task.FromResult(new ToolResult(true, "created", null, PolicyDecision.Allowed)));

        var result = await dispatcher.InvokeAsync("create_file", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/new/path.txt")
        }, identity);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ToolDispatcher_Exemption_AtomicReadWrite_WithValidReadArg()
    {
        var (store, identity) = CreateDefaultStore();

        var manifest = new ToolManifest
        {
            Name = "find_and_replace",
            Description = "Finds and replaces text in a file atomically",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Reversible,
            ObservationExemption = Agent.ObservationExemption.AtomicReadWrite,
            AtomicReadArg = "filePath"
        };

        var loader = new StubLoader(manifest);
        var dispatcher = new SingleManifestDispatcher(loader, store, identity);
        dispatcher.RegisterHandler("find_and_replace", (_, _) =>
            Task.FromResult(new ToolResult(true, "replaced", null, PolicyDecision.Allowed)));

        var result = await dispatcher.InvokeAsync("find_and_replace", new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement("/path/file.txt"),
            ["oldString"] = JsonSerializer.SerializeToElement("foo"),
            ["newString"] = JsonSerializer.SerializeToElement("bar")
        }, identity);

        result.Success.ShouldBeTrue();
    }

    // ---- ToolLoader Tests ----

    [Fact]
    public async Task ToolLoader_SafeDefault_Applied()
    {
        var loader = new ToolLoader([new StubToolSource("test", new List<ToolManifest>
        {
            CreateManifest("delete", DangerTier.Destructive, Reversibility.Irreversible,
                issuesToken: false, requiresToken: false)
        })]);

        var manifest = await loader.TryUnpackAsync("test/delete");
        manifest.ShouldNotBeNull();
        manifest!.RequiresObservationToken.ShouldNotBeNull();
        manifest.RequiresObservationToken.ArgName.ShouldBe("observationToken");
    }

    [Fact]
    public async Task ToolLoader_SafeDefault_NotApplied_WhenExemptionPresent()
    {
        var loader = new ToolLoader([new StubToolSource("test", new List<ToolManifest>
        {
            new()
            {
                Name = "bootstrap",
                Description = "Bootstraps a new project",
                DangerTier = DangerTier.Destructive,
                Reversibility = Reversibility.Irreversible,
                ObservationExemption = Agent.ObservationExemption.ExternalSystemBootstrap,
                BootstrapJustification = "Required for initial CI/CD setup on fresh infrastructure."
            }
        })]);

        var manifest = await loader.TryUnpackAsync("test/bootstrap");
        manifest.ShouldNotBeNull();
        manifest!.RequiresObservationToken.ShouldBeNull();
    }

    [Fact]
    public async Task ToolLoader_ExemptionValidation_AtomicReadWrite_MissingReadArg_Rejected()
    {
        var loader = new ToolLoader([new StubToolSource("test", new List<ToolManifest>
        {
            new()
            {
                Name = "bad_tool",
                Description = "Missing AtomicReadArg",
                DangerTier = DangerTier.Destructive,
                Reversibility = Reversibility.Reversible,
                ObservationExemption = Agent.ObservationExemption.AtomicReadWrite,
            }
        })]);

        var manifest = await loader.TryUnpackAsync("test/bad_tool");
        manifest.ShouldBeNull();
    }

    [Fact]
    public async Task ToolLoader_ExemptionValidation_ExternalBootstrap_TooShort_Rejected()
    {
        var loader = new ToolLoader([new StubToolSource("test", new List<ToolManifest>
        {
            new()
            {
                Name = "short",
                Description = "Short justification",
                DangerTier = DangerTier.Destructive,
                Reversibility = Reversibility.Irreversible,
                ObservationExemption = Agent.ObservationExemption.ExternalSystemBootstrap,
                BootstrapJustification = "Short."
            }
        })]);

        var manifest = await loader.TryUnpackAsync("test/short");
        manifest.ShouldBeNull();
    }

    [Fact]
    public async Task ToolLoader_ExemptionValidation_InvalidExemption_Rejected()
    {
        var loader = new ToolLoader([new StubToolSource("test", new List<ToolManifest>
        {
            new()
            {
                Name = "invalid",
                Description = "Invalid exemption",
                DangerTier = DangerTier.Safe,
                Reversibility = Reversibility.Reversible,
                ObservationExemption = Agent.ObservationExemption.NewResourceCreation
            }
        })]);

        var manifest = await loader.TryUnpackAsync("test/invalid");
        manifest.ShouldBeNull();
    }

    [Fact]
    public async Task ToolLoader_SchemaInjection_InjectsTokenParameter()
    {
        var originalSchema = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["filePath"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["type"] = JsonSerializer.SerializeToElement("string"),
                    ["description"] = JsonSerializer.SerializeToElement("Path to the file")
                })
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "filePath" })
        });

        var manifest = new ToolManifest
        {
            Name = "edit_file",
            Description = "Edits a file",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Reversible,
            ArgsSchema = originalSchema,
            RequiresObservationToken = new ObservationRequirement
            {
                ArgName = "readToken",
                AcceptedTypes = [TokenType.ReadToken],
                ResourcePathArg = "filePath"
            }
        };

        var loader = new ToolLoader([new StubToolSource("test", new List<ToolManifest> { manifest })]);
        var loaded = await loader.TryUnpackAsync("test/edit_file");

        loaded.ShouldNotBeNull();
        loaded!.ArgsSchema.ShouldNotBeNull();

        var schema = loaded.ArgsSchema!.Value;
        schema.ValueKind.ShouldBe(JsonValueKind.Object);
        schema.GetProperty("properties").GetRawText().ShouldContain("readToken");

        var required = JsonSerializer.Deserialize<List<string>>(schema.GetProperty("required").GetRawText());
        required.ShouldNotBeNull();
        required.ShouldContain("readToken");
        required.ShouldContain("filePath");
    }

    [Fact]
    public async Task ToolLoader_SchemaInjection_NoSchema_StillPasses()
    {
        var manifest = new ToolManifest
        {
            Name = "simple_edit",
            Description = "Simple edit",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Reversible,
            ArgsSchema = null,
            RequiresObservationToken = new ObservationRequirement
            {
                ArgName = "readToken",
                AcceptedTypes = [TokenType.ReadToken]
            }
        };

        var loader = new ToolLoader([new StubToolSource("test", new List<ToolManifest> { manifest })]);
        var loaded = await loader.TryUnpackAsync("test/simple_edit");

        loaded.ShouldNotBeNull();
        loaded!.RequiresObservationToken.ShouldNotBeNull();
        loaded.RequiresObservationToken.ArgName.ShouldBe("readToken");
    }

    [Fact]
    public async Task ToolLoader_Survey_ReturnsPrefixedNames()
    {
        var loader = new ToolLoader([
            new StubToolSource("ampelum", new List<ToolManifest>
            {
                CreateSafeManifest("read_file"),
                CreateSafeManifest("list_dir")
            }),
            new StubToolSource("mcp", new List<ToolManifest>
            {
                CreateSafeManifest("github_read")
            })
        ]);

        var surveys = await loader.SurveyAsync();
        surveys.Count.ShouldBe(3);
        surveys.Select(s => s.Name).ShouldContain("ampelum/read_file");
        surveys.Select(s => s.Name).ShouldContain("ampelum/list_dir");
        surveys.Select(s => s.Name).ShouldContain("mcp/github_read");
    }

    // ---- PolicyEngine Tests ----

    [Fact]
    public void PolicyEngine_Catastrophic_AlwaysBlocked()
    {
        var engine = new PolicyEngine();
        var tool = new ToolManifest
        {
            Name = "rm_rf",
            Description = "Remove recursively",
            DangerTier = DangerTier.Catastrophic,
            Reversibility = Reversibility.Irreversible
        };
        var context = new PolicyContext
        {
            Caller = new AgentIdentity("test", "inst", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))
        };

        var decision = engine.Evaluate(tool, context);
        decision.Decision.ShouldBe(Decision.Block);
        decision.Reason.ShouldNotBeNull();
        decision.Reason.ShouldContain("Catastrophic");
    }

    [Fact]
    public void PolicyEngine_RequiresConfirmation_ReturnsConfirmation()
    {
        var engine = new PolicyEngine();
        var tool = new ToolManifest
        {
            Name = "deploy",
            Description = "Deploy to production",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Irreversible,
            RequiresConfirmation = true
        };
        var context = new PolicyContext
        {
            Caller = new AgentIdentity("test", "inst", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))
        };

        var decision = engine.Evaluate(tool, context);
        decision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    [Fact]
    public void PolicyEngine_SafeTool_AllowedByDefault()
    {
        var engine = new PolicyEngine();
        var tool = CreateSafeManifest("read_file");
        var context = new PolicyContext
        {
            Caller = new AgentIdentity("test", "inst", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))
        };

        var decision = engine.Evaluate(tool, context);
        decision.Decision.ShouldBe(Decision.Allow);
    }

    // ---- Helpers ----

    private static (InMemoryTokenStore store, AgentIdentity identity) CreateDefaultStore()
    {
        var store = new InMemoryTokenStore();
        var identity = new AgentIdentity("test-agent", "session-1", ["read", "write"],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        return (store, identity);
    }

    private static ObservationToken MakeToken(
        string tokenId,
        string resourcePath,
        string issuedByTool,
        string? sessionId = null)
    {
        return new ObservationToken(
            tokenId, resourcePath, issuedByTool,
            sessionId ?? "session-1",
            1, 10);
    }

    private static ToolManifest CreateSafeManifest(string name)
    {
        return new ToolManifest
        {
            Name = name,
            Description = $"Safe tool {name}",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };
    }

    private static ToolManifest CreateManifest(
        string name,
        DangerTier tier,
        Reversibility reversibility,
        bool issuesToken,
        bool requiresToken,
        TokenType? tokenType = null,
        string? resourcePathArg = null,
        string? argName = null,
        string? verifyPathAbsent = null)
    {
        var manifest = new ToolManifest
        {
            Name = name,
            Description = $"Tool {name}",
            DangerTier = tier,
            Reversibility = reversibility
        };

        if (issuesToken)
        {
            manifest = manifest with
            {
                IssuesObservationToken = new ObservationSource
                {
                    TokenType = tokenType ?? TokenType.ReadToken,
                    ResourcePathArg = resourcePathArg
                }
            };
        }

        if (requiresToken)
        {
            var requirement = new ObservationRequirement
            {
                ArgName = argName ?? "token",
                AcceptedTypes = tokenType is not null ? [tokenType.Value] : [],
                ResourcePathArg = resourcePathArg,
                VerifyPathAbsent = verifyPathAbsent
            };
            manifest = manifest with { RequiresObservationToken = requirement };
        }

        return manifest;
    }
}

// ---- Dispatchers that control which manifest is returned ----

/// <summary>Dispatcher using a StubLoader that returns one manifest for all tool names.</summary>
file sealed class SingleManifestDispatcher : ToolDispatcher
{
    public SingleManifestDispatcher(IToolLoader loader, InMemoryTokenStore store, AgentIdentity identity, int defaultTokenExpiry = 10)
        : base(loader, store, new PolicyEngine(), identity, defaultTokenExpiry) { }
}

/// <summary>Dispatcher that returns different manifests for different tool names.</summary>
file sealed class MultiManifestDispatcher : ToolDispatcher
{
    public MultiManifestDispatcher(
        (string name, ToolManifest manifest)[] manifests,
        InMemoryTokenStore store,
        AgentIdentity identity,
        int defaultTokenExpiry = 10)
        : base(new MultiManifestLoader(manifests), store, new PolicyEngine(), identity, defaultTokenExpiry)
    {
    }
}

file sealed class MultiManifestLoader : IToolLoader
{
    private readonly Dictionary<string, ToolManifest> _manifests;

    public MultiManifestLoader((string name, ToolManifest manifest)[] manifests)
    {
        _manifests = manifests.ToDictionary(m => m.name, m => m.manifest, StringComparer.Ordinal);
    }

    public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default)
    {
        var surveys = _manifests.Values
            .Select(m => new ToolSurvey { Name = m.Name, Description = m.Description, DangerTier = m.DangerTier, RequiresConfirmation = m.RequiresConfirmation })
            .ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<ToolSurvey>>(surveys);
    }

    public Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default)
    {
        return Task.FromResult(_manifests.GetValueOrDefault(toolName) ?? throw new KeyNotFoundException($"Tool '{toolName}' not found."));
    }

    public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default)
    {
        return Task.FromResult(_manifests.GetValueOrDefault(toolName));
    }

    public ValueTask EvictAsync(string toolName)
    {
        return default;
    }

    public Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default)
    {
        var result = SurveyAsync(ct).Result
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        return Task.FromResult<IReadOnlyList<ToolSurvey>>(result.AsReadOnly());
    }
}

// ---- Test Stubs ----

file sealed class StubLoader : IToolLoader
{
    private readonly ToolManifest _manifest;

    public StubLoader(ToolManifest manifest)
    {
        _manifest = manifest;
    }

    public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ToolSurvey>>([
            new ToolSurvey { Name = _manifest.Name, Description = _manifest.Description, DangerTier = _manifest.DangerTier, RequiresConfirmation = _manifest.RequiresConfirmation }
        ]);
    }

    public Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default)
    {
        return Task.FromResult(_manifest);
    }

    public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default)
    {
        return Task.FromResult<ToolManifest?>(_manifest);
    }

    public ValueTask EvictAsync(string toolName)
    {
        return default;
    }

    public Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default)
    {
        return SurveyAsync(ct);
    }
}

file sealed class StubToolSource : IToolSource
{
    private readonly Dictionary<string, ToolManifest> _manifests;
    private IReadOnlyList<ToolSurvey>? _surveys;

    public StubToolSource(string prefix, IEnumerable<ToolManifest> manifests)
    {
        SourcePrefix = prefix;
        _manifests = manifests.ToDictionary(m => m.Name, StringComparer.Ordinal);
    }

    public string SourcePrefix { get; }

    public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default)
    {
        _surveys ??= _manifests.Values
            .Select(m => new ToolSurvey { Name = m.Name, Description = m.Description, DangerTier = m.DangerTier, RequiresConfirmation = m.RequiresConfirmation })
            .ToList().AsReadOnly();
        return Task.FromResult(_surveys);
    }

    public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default)
    {
        return Task.FromResult(_manifests.GetValueOrDefault(toolName));
    }
}