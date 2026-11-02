using System.Text.Json;
using SignalGrid.Ampelum.Persistence;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Persistence;

/// <summary>
/// Closes coverage on the <see cref="JsonValueKind.Null"/> arm of
/// <c>JsonProgramPersistence.UnboxJsonElement</c>. The public load path generally
/// surfaces <c>null</c> as a C# null reference rather than a <c>JsonElement</c>
/// of <c>ValueKind.Null</c>, so this defensive arm is exercised directly.
/// </summary>
public sealed class JsonProgramPersistenceUnboxNullCoverageTests
{
    [Fact]
    public void UnboxJsonElement_NullKind_ReturnsNull()
    {
        var doc = JsonDocument.Parse("null");
        JsonProgramPersistence.UnboxJsonElement(doc.RootElement).ShouldBeNull();
    }
}
