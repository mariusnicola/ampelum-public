using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Filtering;

/// <summary>
/// Composable post-retrieval metadata predicate. Provides four combinators (Equals, In, And, Or)
/// and a <see cref="ToFlatEqualsDictionary"/> projection for vector-store backends that only
/// support equality-and-conjunction filters server-side.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public abstract class FilterPredicate
{
    /// <summary>Returns true if <paramref name="metadata"/> matches the predicate.</summary>
    public abstract bool Matches(IReadOnlyDictionary<string, object?> metadata);

    /// <summary>If the predicate is a flat conjunction of equality clauses, return them as a dict; otherwise null.</summary>
    public abstract IReadOnlyDictionary<string, object?>? ToFlatEqualsDictionary();

    /// <summary>Equality predicate.</summary>
    public static FilterPredicate Equals(string key, object? value) => new EqualsPredicate(key, value);

    /// <summary>Set-membership predicate.</summary>
    public static FilterPredicate In(string key, IReadOnlyList<object?> values) => new InPredicate(key, values);

    /// <summary>Conjunction.</summary>
    public static FilterPredicate And(params FilterPredicate[] children) => new AndPredicate(children);

    /// <summary>Disjunction.</summary>
    public static FilterPredicate Or(params FilterPredicate[] children) => new OrPredicate(children);

    private sealed class EqualsPredicate(string key, object? value) : FilterPredicate
    {
        public override bool Matches(IReadOnlyDictionary<string, object?> metadata)
            => metadata.TryGetValue(key, out var v) && object.Equals(v, value);

        public override IReadOnlyDictionary<string, object?> ToFlatEqualsDictionary()
            => new Dictionary<string, object?> { [key] = value };
    }

    private sealed class InPredicate(string key, IReadOnlyList<object?> values) : FilterPredicate
    {
        public override bool Matches(IReadOnlyDictionary<string, object?> metadata)
            => metadata.TryGetValue(key, out var v) && values.Any(x => object.Equals(x, v));

        public override IReadOnlyDictionary<string, object?>? ToFlatEqualsDictionary() => null;
    }

    private sealed class AndPredicate(IReadOnlyList<FilterPredicate> children) : FilterPredicate
    {
        public override bool Matches(IReadOnlyDictionary<string, object?> metadata)
            => children.All(c => c.Matches(metadata));

        public override IReadOnlyDictionary<string, object?>? ToFlatEqualsDictionary()
        {
            var result = new Dictionary<string, object?>();
            foreach (var c in children)
            {
                var d = c.ToFlatEqualsDictionary();
                if (d is null)
                {
                    return null;
                }
                foreach (var kv in d)
                {
                    result[kv.Key] = kv.Value;
                }
            }
            return result;
        }
    }

    private sealed class OrPredicate(IReadOnlyList<FilterPredicate> children) : FilterPredicate
    {
        public override bool Matches(IReadOnlyDictionary<string, object?> metadata)
            => children.Any(c => c.Matches(metadata));

        public override IReadOnlyDictionary<string, object?>? ToFlatEqualsDictionary() => null;
    }
}
