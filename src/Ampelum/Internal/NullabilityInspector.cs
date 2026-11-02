using System.Reflection;

namespace SignalGrid.Ampelum.Internal;

internal static class NullabilityInspector
{
    static readonly NullabilityInfoContext _ctx = new();
    static readonly object _lock = new();

    /// <summary>True when the property can legitimately hold null (nullable value type or nullable reference).</summary>
    public static bool IsNullable(PropertyInfo property)
    {
        // NullabilityInfoContext is not thread-safe per docs â€” lock around it.
        lock (_lock)
        {
            var info = _ctx.Create(property);
            return info.WriteState == NullabilityState.Nullable;
        }
    }
}
