#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class PromptsEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GET_prompts_EmptyStore_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _factory.HttpClient.GetAsync("/prompts", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<PromptIndexResponse>(json, AmpelumHostContractsJsonContext.Default.PromptIndexResponse);
        body.ShouldNotBeNull();
        body.ModuleNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_prompts_create_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new CreatePromptVersionRequest(
            DemoAssignments: new Dictionary<string, IReadOnlyList<DemoExample>>(),
            InstructionOverrides: new Dictionary<string, string>());

        var response = await _factory.HttpClient.PostAsJsonAsync("/prompts/DoesNotExist", request,
            AmpelumHostContractsJsonContext.Default.CreatePromptVersionRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("module_not_found");
    }

    async Task<string> CreateFakeVersionAsync(string? parent, string? note, CancellationToken ct)
    {
        var request = new CreatePromptVersionRequest(
            new Dictionary<string, IReadOnlyList<DemoExample>>(),
            new Dictionary<string, string> { ["default"] = "Be brief." },
            parent, note);
        var response = await _factory.HttpClient.PostAsJsonAsync("/prompts/Fake", request,
            AmpelumHostContractsJsonContext.Default.CreatePromptVersionRequest, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.PromptVersion, ct))!.VersionId;
    }

    async Task PromoteFakeAsync(string versionId, CancellationToken ct) =>
        (await _factory.HttpClient.PostAsJsonAsync("/prompts/Fake/promote", new PromoteRequest(versionId),
            AmpelumHostContractsJsonContext.Default.PromoteRequest, cancellationToken: ct)).EnsureSuccessStatusCode();

    async Task<JsonElement> ReadMetaAsync(string versionId, CancellationToken ct)
    {
        var store = _factory.Services.GetRequiredService<SignalGrid.Ampelum.Host.Persistence.IPromptVersionStore>();
        var row = (await store.TryGetByIdAsync(versionId, ct))!;
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(row.FilePath, ct));
        doc.RootElement.GetProperty("instructionOverrides").GetProperty("default").GetString().ShouldBe("Be brief.");
        Directory.GetFiles(Path.GetDirectoryName(row.FilePath)!, "*.tmp").ShouldBeEmpty();
        return doc.RootElement.GetProperty("_meta").Clone();
    }

    const string FilesNotUpdatedHeader = "X-Ampelum-Prompt-Files-Not-Updated";

    async Task<string> FilePathAsync(string versionId, CancellationToken ct) =>
        (await _factory.Services.GetRequiredService<SignalGrid.Ampelum.Host.Persistence.IPromptVersionStore>()
            .TryGetByIdAsync(versionId, ct))!.FilePath;

    async Task<HttpResponseMessage> PromoteRawAsync(string versionId, CancellationToken ct) =>
        await _factory.HttpClient.PostAsJsonAsync("/prompts/Fake/promote", new PromoteRequest(versionId),
            AmpelumHostContractsJsonContext.Default.PromoteRequest, cancellationToken: ct);

    [Theory]
    [InlineData("not json")]
    [InlineData(null)]
    public async Task POST_prompts_promote_WhenThePreviousActivesFileIsUnreadableOrGone_StillPromotes_AndSaysWhichFileWasNotUpdated(string? content)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await CreateFakeVersionAsync(null, null, ct);
        var second = await CreateFakeVersionAsync(first, null, ct);
        await PromoteFakeAsync(first, ct);
        var firstFile = await FilePathAsync(first, ct);
        if (content is null) File.Delete(firstFile); else await File.WriteAllTextAsync(firstFile, content, ct);

        using var response = await PromoteRawAsync(second, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.PromoteResponse, ct);
        body!.ActiveVersionId.ShouldBe(second);
        body.PreviousActiveVersionId.ShouldBe(first);
        response.Headers.GetValues(FilesNotUpdatedHeader).ShouldBe([first]);
        (await ReadMetaAsync(second, ct)).GetProperty("status").GetString().ShouldBe("active");
        var store = _factory.Services.GetRequiredService<SignalGrid.Ampelum.Host.Persistence.IPromptVersionStore>();
        (await store.GetActiveAsync("Fake", ct))!.VersionId.ShouldBe(second);
        (await store.TryGetByIdAsync(first, ct))!.Status.ShouldBe("retired");
    }

    [Fact]
    public async Task POST_prompts_promote_WhenTheTargetsFileIsUnreadable_AnswersAnErrorBody_AndPromotesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateFakeVersionAsync(null, null, ct);
        await File.WriteAllTextAsync(await FilePathAsync(id, ct), "not json", ct);

        using var response = await PromoteRawAsync(id, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        response.Headers.Contains("X-Ampelum-Trace-Id").ShouldBeTrue();
        var error = await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ErrorResponse, ct);
        error!.Message.ShouldStartWith("Failed to update prompt file");
        var store = _factory.Services.GetRequiredService<SignalGrid.Ampelum.Host.Persistence.IPromptVersionStore>();
        (await store.TryGetByIdAsync(id, ct))!.Status.ShouldBe("challenger");
        (await store.GetActiveAsync("Fake", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task POST_prompts_promote_WhenTheTargetsFileIsGone_StillPromotes_AndSaysWhichFileWasNotUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateFakeVersionAsync(null, null, ct);
        File.Delete(await FilePathAsync(id, ct));

        using var response = await PromoteRawAsync(id, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Headers.GetValues(FilesNotUpdatedHeader).ShouldBe([id]);
    }

    [Fact]
    public async Task POST_prompts_promote_WritesThroughAUniqueTempName_SoAHeldTempFileDoesNotBlockIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateFakeVersionAsync(null, null, ct);
        // Another writer holds the temp name the old code always used.
        using var held = new FileStream(await FilePathAsync(id, ct) + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);

        using var response = await PromoteRawAsync(id, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(await FilePathAsync(id, ct), ct));
        doc.RootElement.GetProperty("_meta").GetProperty("status").GetString().ShouldBe("active");
    }

    [Fact]
    public async Task POST_prompts_create_WritesTheVersionsStateIntoTheFilesMeta()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await CreateFakeVersionAsync(null, null, ct);

        var id = await CreateFakeVersionAsync(parent, "a note", ct);

        var meta = await ReadMetaAsync(id, ct);
        meta.GetProperty("versionId").GetString().ShouldBe(id);
        meta.GetProperty("moduleName").GetString().ShouldBe("Fake");
        meta.GetProperty("status").GetString().ShouldBe("challenger");
        meta.GetProperty("parentVersionId").GetString().ShouldBe(parent);
        meta.GetProperty("note").GetString().ShouldBe("a note");
        meta.GetProperty("createdAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task POST_prompts_promote_WritesActiveIntoTheTargetsFile_AndRetiredIntoThePreviousActivesFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await CreateFakeVersionAsync(null, null, ct);
        var second = await CreateFakeVersionAsync(first, null, ct);

        await PromoteFakeAsync(first, ct);
        (await ReadMetaAsync(first, ct)).GetProperty("status").GetString().ShouldBe("active");
        (await ReadMetaAsync(second, ct)).GetProperty("status").GetString().ShouldBe("challenger");

        await PromoteFakeAsync(second, ct);
        (await ReadMetaAsync(first, ct)).GetProperty("status").GetString().ShouldBe("retired");
        (await ReadMetaAsync(second, ct)).GetProperty("status").GetString().ShouldBe("active");
    }

    [Fact]
    public async Task POST_prompts_promote_WritesPromotedAtIntoTheTargetsMeta_AndKeepsItWhenTheVersionIsRetired()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await CreateFakeVersionAsync(null, null, ct);
        var second = await CreateFakeVersionAsync(first, null, ct);
        (await ReadMetaAsync(first, ct)).TryGetProperty("promotedAt", out _).ShouldBeFalse();

        await PromoteFakeAsync(first, ct);
        var firstPromoted = (await ReadMetaAsync(first, ct)).GetProperty("promotedAt").GetDateTimeOffset();
        firstPromoted.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        (await ReadMetaAsync(second, ct)).TryGetProperty("promotedAt", out _).ShouldBeFalse();

        await PromoteFakeAsync(second, ct);
        (await ReadMetaAsync(first, ct)).GetProperty("promotedAt").GetDateTimeOffset().ShouldBe(firstPromoted);
        (await ReadMetaAsync(second, ct)).GetProperty("promotedAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(firstPromoted);
    }

    [Fact]
    public async Task POST_prompts_promote_AddsMetaToAnOlderFileWithoutOne_AndKeepsTheRestOfTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateFakeVersionAsync(null, "old", ct);
        var store = _factory.Services.GetRequiredService<SignalGrid.Ampelum.Host.Persistence.IPromptVersionStore>();
        var path = (await store.TryGetByIdAsync(id, ct))!.FilePath;
        await File.WriteAllTextAsync(path, """{"demoAssignments":{},"instructionOverrides":{"default":"Be brief."},"version":3}""", ct);

        await PromoteFakeAsync(id, ct);

        var meta = await ReadMetaAsync(id, ct);
        meta.GetProperty("status").GetString().ShouldBe("active");
        meta.GetProperty("note").GetString().ShouldBe("old");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        doc.RootElement.GetProperty("version").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task POST_prompts_promote_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new PromoteRequest("v-some-id");

        var response = await _factory.HttpClient.PostAsJsonAsync("/prompts/DoesNotExist/promote", request,
            AmpelumHostContractsJsonContext.Default.PromoteRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("module_not_found");
    }
}
