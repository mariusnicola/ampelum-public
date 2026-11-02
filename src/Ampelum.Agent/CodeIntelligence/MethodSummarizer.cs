using System.Threading.Channels;

namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>Background LLM-based method summarizer with bounded channel and throttled concurrency.</summary>
public sealed class MethodSummarizer : IDisposable
{
    private readonly Channel<Symbol> _inChannel = Channel.CreateBounded<Symbol>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = false
    });
    private readonly Channel<Symbol> _outChannel = Channel.CreateUnbounded<Symbol>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, string, Task<string?>> _summarizeFunc;
    private readonly SemaphoreSlim _throttle;
    private int _totalSummarized;

    public int TotalSummarized => _totalSummarized;
    public event Action<Symbol>? OnSummarized;

    public MethodSummarizer(Func<string, string, Task<string?>> summarizeFunc, int maxConcurrency = 5)
    {
        _summarizeFunc = summarizeFunc;
        _throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public bool Enqueue(Symbol symbol) => _inChannel.Writer.TryWrite(symbol);

    public Task StartAsync()
    {
        return Task.Run(async () =>
        {
            var readers = Enumerable.Range(0, 5).Select(_ => ReaderLoop()).ToArray();
            await Task.WhenAll(readers);
        });
    }

    private async Task ReaderLoop()
    {
        var reader = _inChannel.Reader;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(_cts.Token)) break;
                while (reader.TryRead(out var symbol))
                {
                    await _throttle.WaitAsync(_cts.Token);
                    _ = SummarizeOneAsync(symbol);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SummarizeOneAsync(Symbol symbol)
    {
        try
        {
            var prompt = $"Summarize in one sentence what this does: {symbol.Signature}";
            var summary = await _summarizeFunc(symbol.Name, prompt);
            if (summary is not null)
            {
                Interlocked.Increment(ref _totalSummarized);
                var updated = symbol.WithSummary(summary.Trim());
                _outChannel.Writer.TryWrite(updated);
                OnSummarized?.Invoke(updated);
            }
        }
        finally { _throttle.Release(); }
    }

    public bool TryReadCompleted(out Symbol? symbol) => _outChannel.Reader.TryRead(out symbol);

    public Symbol[] DrainCompleted()
    {
        var results = new List<Symbol>();
        while (_outChannel.Reader.TryRead(out var s)) results.Add(s);
        return [.. results];
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _throttle.Dispose();
    }
}
