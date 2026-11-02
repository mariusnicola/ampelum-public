using System.Diagnostics.CodeAnalysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using LuceneDocument = Lucene.Net.Documents.Document;
using LuceneDirectory = Lucene.Net.Store.Directory;
using LuceneField = Lucene.Net.Documents.Field;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>BM25 keyword retriever backed by Lucene.NET (Apache-2.0). AOT-hostile; gated by <see cref="RequiresUnreferencedCodeAttribute"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
[RequiresUnreferencedCode("Lucene.Net uses reflection that NativeAOT cannot statically resolve.")]
[RequiresDynamicCode("Lucene.Net constructs analyzers at runtime.")]
public sealed class BM25Retriever : IRetriever, IDisposable
{
    private const LuceneVersion LV = LuceneVersion.LUCENE_48;
    private readonly BM25IndexOptions _options;
    private readonly LuceneDirectory _directory;
    private readonly StandardAnalyzer _analyzer;
    private IndexSearcher? _searcher;
    private DirectoryReader? _reader;
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _metadata = new();

    /// <summary>Creates a BM25 retriever.</summary>
    public BM25Retriever(BM25IndexOptions options)
    {
        _options = options;
        _directory = options.IndexDirectory is null
            ? new RAMDirectory()
            : FSDirectory.Open(options.IndexDirectory);
        _analyzer = new StandardAnalyzer(LV);
    }

    /// <summary>Indexes the given documents (replaces existing data on subsequent calls if directory is fresh).</summary>
    public Task IndexAsync(IReadOnlyList<RetrievedDocument> documents, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var config = new IndexWriterConfig(LV, _analyzer)
            {
                OpenMode = OpenMode.CREATE_OR_APPEND,
            };
            config.Similarity = new BM25Similarity(_options.K1, _options.B);
            using (var writer = new IndexWriter(_directory, config))
            {
                foreach (var d in documents)
                {
                    var ld = new LuceneDocument
                    {
                        new StringField("id", d.Id, LuceneField.Store.YES),
                        new TextField("text", d.Text, LuceneField.Store.YES),
                    };
                    foreach (var kv in d.Metadata)
                    {
                        ld.Add(new StringField($"meta_{kv.Key}", kv.Value?.ToString() ?? "", LuceneField.Store.YES));
                    }
                    writer.AddDocument(ld);
                    _metadata[d.Id] = d.Metadata;
                }
                writer.Commit();
            }
            _reader?.Dispose();
            _reader = DirectoryReader.Open(_directory);
            _searcher = new IndexSearcher(_reader)
            {
                Similarity = new BM25Similarity(_options.K1, _options.B),
            };
        }, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        if (_searcher is null)
        {
            return Task.FromResult<IReadOnlyList<RetrievedDocument>>(Array.Empty<RetrievedDocument>());
        }
        return Task.Run<IReadOnlyList<RetrievedDocument>>(
            () =>
            {
                using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.query");
                activity?.SetTag("SignalGrid.Ampelum.retriever.kind", "bm25");
                var parser = new QueryParser(LV, "text", _analyzer);
                Query q;
                try
                {
                    q = parser.Parse(QueryParserBase.Escape(query));
                }
                catch (ParseException)
                {
                    return Array.Empty<RetrievedDocument>();
                }
                var combined = new BooleanQuery { { q, Occur.MUST } };
                if (filters is not null)
                {
                    foreach (var f in filters)
                    {
                        combined.Add(new TermQuery(new Term($"meta_{f.Key}", f.Value?.ToString() ?? "")), Occur.MUST);
                    }
                }
                var hits = _searcher.Search(combined, topK).ScoreDocs;
                var results = new List<RetrievedDocument>(hits.Length);
                foreach (var h in hits)
                {
                    var doc = _searcher.Doc(h.Doc);
                    var id = doc.Get("id");
                    var text = doc.Get("text");
                    results.Add(new RetrievedDocument(id, text, h.Score, _metadata[id]));
                }
                return results;
            },
            ct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _reader?.Dispose();
        _directory.Dispose();
        _analyzer.Dispose();
    }
}
