using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Agent.Execution;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Execution;

public sealed class SubAgentRunnerToolTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "ampelum-subagent-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _work;

    public SubAgentRunnerToolTests()
    {
        _work = Path.Combine(_base, "work");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    static SubAgentTask Task1() => new() { Id = "t1", Description = "do the thing" };

    static ChatResponse Call(string name, params (string Key, object? Value)[] args) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("c" + Interlocked.Increment(ref _callIds), name, args.ToDictionary(a => a.Key, a => a.Value))]));

    static int _callIds;

    static ChatResponse WriteCall(string path, string content) =>
        Call("write_file", ("path", path), ("content", content), ("append", null));

    static ChatResponse ReadCall(string path) =>
        Call("read_file", ("path", path), ("offset", null), ("limit", null));

    static string[] ToolResults(FakeChatClient chat) =>
        [.. chat.RecordedCalls.SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>().DistinctBy(r => r.CallId).Select(r => r.Result?.ToString() ?? "")];

    [Fact]
    public async Task ToolCalls_AreRunByTheRunner_EvenOnAPlainClient()
    {
        var chat = new FakeChatClient().EnqueueRaw(WriteCall("a.txt", "hello")).EnqueueText("DONE: wrote it");
        var runner = new SubAgentRunner(chat, _work, "s1");

        var result = await runner.RunAsync(Task1(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_work, "a.txt")).ShouldBe("hello");
        result.FilesChanged.ShouldBe([Path.Combine(_work, "a.txt")]);
    }

    [Fact]
    public async Task ToolCalls_AClientThatAlreadyInvokesFunctions_IsNotWrappedTwice()
    {
        var inner = new FakeChatClient().EnqueueRaw(WriteCall("b.txt", "once")).EnqueueText("DONE: ok");
        var runner = new SubAgentRunner(new FunctionInvokingChatClient(inner), _work, "s1");

        var result = await runner.RunAsync(Task1(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_work, "b.txt")).ShouldBe("once");
        inner.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WriteFile_OutsideTheWorkingDirectory_IsRefused_ByAbsolutePathAndByDotDot()
    {
        var absolute = Path.Combine(_base, "absolute.txt");
        var chat = new FakeChatClient()
            .EnqueueRaw(WriteCall(absolute, "x"))
            .EnqueueRaw(WriteCall("../dotdot.txt", "x"))
            .EnqueueText("DONE: tried");
        var runner = new SubAgentRunner(new FunctionInvokingChatClient(chat), _work, "s1");

        var result = await runner.RunAsync(Task1(), TestContext.Current.CancellationToken);

        File.Exists(absolute).ShouldBeFalse();
        File.Exists(Path.Combine(_base, "dotdot.txt")).ShouldBeFalse();
        ToolResults(chat).ShouldAllBe(r => r.StartsWith("ERROR:", StringComparison.Ordinal) && r.Contains("outside the working directory", StringComparison.Ordinal));
        result.FilesChanged.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadListGlobAndGrep_OutsideTheWorkingDirectory_AreRefused()
    {
        var secret = Path.Combine(_base, "secret.txt");
        File.WriteAllText(secret, "needle-outside");
        var chat = new FakeChatClient()
            .EnqueueRaw(ReadCall(secret))
            .EnqueueRaw(Call("list_directory", ("path", _base), ("pattern", null)))
            .EnqueueRaw(Call("glob", ("pattern", "*.txt"), ("basePath", _base)))
            .EnqueueRaw(Call("grep", ("pattern", "needle"), ("basePath", _base), ("fileGlob", null)))
            .EnqueueText("DONE: tried");
        var runner = new SubAgentRunner(new FunctionInvokingChatClient(chat), _work, "s1");

        await runner.RunAsync(Task1(), TestContext.Current.CancellationToken);

        var results = ToolResults(chat);
        results.Length.ShouldBe(4);
        results.ShouldAllBe(r => r.StartsWith("ERROR:", StringComparison.Ordinal) && !r.Contains("needle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchPatternsWithDotDot_NeverReturnANameOrContentFromOutsideTheWorkingDirectory()
    {
        var outside = Path.Combine(_base, "out");
        var sibling = Path.Combine(_base, "work2"); // same name prefix as the working directory
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(outside, "o.txt"), "SECRET-OUT");
        File.WriteAllText(Path.Combine(sibling, "s.txt"), "SECRET-SIB");
        File.WriteAllText(Path.Combine(_work, "in.txt"), "SECRET-IN");
        var toOut = Path.Combine("..", "out", "*.txt");
        var toSibling = Path.Combine("..", "work2", "*.txt");
        var chat = new FakeChatClient()
            .EnqueueRaw(Call("grep", ("pattern", "SECRET"), ("basePath", null), ("fileGlob", toOut)))
            .EnqueueRaw(Call("grep", ("pattern", "SECRET"), ("basePath", null), ("fileGlob", toSibling)))
            .EnqueueRaw(Call("glob", ("pattern", toOut), ("basePath", null)))
            .EnqueueRaw(Call("glob", ("pattern", toSibling), ("basePath", null)))
            .EnqueueRaw(Call("list_directory", ("path", "."), ("pattern", Path.Combine("..", "out", "*"))))
            .EnqueueRaw(Call("list_directory", ("path", "."), ("pattern", Path.Combine("..", "work2", "*"))))
            .EnqueueRaw(Call("grep", ("pattern", "SECRET"), ("basePath", null), ("fileGlob", "*.txt")))
            .EnqueueText("DONE: tried");
        var runner = new SubAgentRunner(chat, _work, "s1");

        await runner.RunAsync(Task1(), TestContext.Current.CancellationToken);

        var results = ToolResults(chat);
        results.Length.ShouldBe(7);
        foreach (var result in results[..6])
        {
            result.ShouldNotContain("SECRET-OUT");
            result.ShouldNotContain("SECRET-SIB");
            result.ShouldNotContain("o.txt");
            result.ShouldNotContain("s.txt");
        }
        // A search that stays inside still finds the working directory's own file.
        results[6].ShouldContain("in.txt:1: SECRET-IN");
    }

    [Fact]
    public async Task APathInsideTheWorkingDirectory_IsAllowed_AbsoluteOrWithADotDotThatStaysInside()
    {
        Directory.CreateDirectory(Path.Combine(_work, "sub"));
        var chat = new FakeChatClient()
            .EnqueueRaw(WriteCall(Path.Combine(_work, "sub", "abs.txt"), "1"))
            .EnqueueRaw(WriteCall("sub/../rel.txt", "2"))
            .EnqueueText("DONE: ok");
        var runner = new SubAgentRunner(chat, _work, "s1");

        await runner.RunAsync(Task1(), TestContext.Current.CancellationToken);

        File.ReadAllText(Path.Combine(_work, "sub", "abs.txt")).ShouldBe("1");
        File.ReadAllText(Path.Combine(_work, "rel.txt")).ShouldBe("2");
    }

    [Fact]
    public async Task AFailedWrite_IsNotListedAsAChangedFile()
    {
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        var chat = new FakeChatClient().EnqueueRaw(WriteCall("src", "cannot be written")).EnqueueText("DONE: tried");
        var runner = new SubAgentRunner(new FunctionInvokingChatClient(chat), _work, "s1");

        var result = await runner.RunAsync(Task1(), TestContext.Current.CancellationToken);

        ToolResults(chat).ShouldAllBe(r => r.StartsWith("ERROR:", StringComparison.Ordinal));
        result.FilesChanged.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheTasksTier_PicksTheClient_AndATieredClientAlsoRunsToolCalls()
    {
        var fast = new FakeChatClient().EnqueueText("DONE: fast");
        var capable = new FakeChatClient().EnqueueRaw(WriteCall("t.txt", "tiered")).EnqueueText("DONE: capable");
        var asked = new List<ModelTier>();
        var runner = new SubAgentRunner(fast, _work, "s1", clientForTier: tier =>
        {
            asked.Add(tier);
            return tier == ModelTier.Capable ? capable : fast;
        });

        var result = await runner.RunAsync(Task1() with { Tier = ModelTier.Capable }, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe("capable");
        asked.ShouldBe([ModelTier.Capable]);
        fast.RecordedCalls.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(_work, "t.txt")).ShouldBe("tiered");
    }

    [Fact]
    public async Task WithoutATierPicker_EveryTierUsesTheGivenClient()
    {
        var chat = new FakeChatClient().EnqueueText("DONE: same");
        var runner = new SubAgentRunner(chat, _work, "s1");

        var result = await runner.RunAsync(Task1() with { Tier = ModelTier.Capable }, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe("same");
    }

    [Fact]
    public async Task ThePrompt_NamesOnlyTheToolsThatExist()
    {
        var chat = new FakeChatClient().EnqueueText("DONE: nothing");
        var runner = new SubAgentRunner(chat, _work, "s1");

        await runner.RunAsync(Task1(), TestContext.Current.CancellationToken);

        var prompt = chat.RecordedCalls[0].Messages[0].Text;
        prompt.ShouldContain("Tools: read_file, write_file, list_directory, glob, grep");
        prompt.ShouldNotContain("edit_file");
        prompt.ShouldNotContain("run_tests");
    }
}
