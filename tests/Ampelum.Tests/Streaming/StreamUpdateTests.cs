using SignalGrid.Ampelum.Streaming;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Streaming;

public class StreamUpdateTests
{
    [Fact]
    public void Ctor_WithValidArgs_CreatesRecord()
    {
        // Arrange
        const string fieldName = "answer";
        const string partialText = "hello";
        const bool isFinal = false;
        const int finalValue = 42;

        // Act
        var update = new StreamUpdate<int>(fieldName, partialText, isFinal, finalValue);

        // Assert
        update.FieldName.ShouldBe(fieldName);
        update.PartialText.ShouldBe(partialText);
        update.IsFinal.ShouldBe(isFinal);
        update.FinalValue.ShouldBe(finalValue);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        // Arrange
        var update1 = new StreamUpdate<string>("field", "text", true, "final");
        var update2 = new StreamUpdate<string>("field", "text", true, "final");

        // Act & Assert
        update1.ShouldBe(update2);
        update1.Equals(update2).ShouldBeTrue();
        (update1 == update2).ShouldBeTrue();
    }

    [Fact]
    public void RecordEquality_DifferentFieldName_NotEqual()
    {
        // Arrange
        var update1 = new StreamUpdate<string>("field1", "text", true, "final");
        var update2 = new StreamUpdate<string>("field2", "text", true, "final");

        // Act & Assert
        update1.ShouldNotBe(update2);
        (update1 != update2).ShouldBeTrue();
    }

    [Fact]
    public void RecordEquality_DifferentPartialText_NotEqual()
    {
        // Arrange
        var update1 = new StreamUpdate<string>("field", "text1", true, "final");
        var update2 = new StreamUpdate<string>("field", "text2", true, "final");

        // Act & Assert
        update1.ShouldNotBe(update2);
    }

    [Fact]
    public void RecordEquality_DifferentIsFinal_NotEqual()
    {
        // Arrange
        var update1 = new StreamUpdate<string>("field", "text", true, "final");
        var update2 = new StreamUpdate<string>("field", "text", false, "final");

        // Act & Assert
        update1.ShouldNotBe(update2);
    }

    [Fact]
    public void RecordEquality_DifferentFinalValue_NotEqual()
    {
        // Arrange
        var update1 = new StreamUpdate<string>("field", "text", true, "final1");
        var update2 = new StreamUpdate<string>("field", "text", true, "final2");

        // Act & Assert
        update1.ShouldNotBe(update2);
    }

    [Fact]
    public void RecordEquality_NullFinalValues_Equal()
    {
        // Arrange
        var update1 = new StreamUpdate<string>("field", "text", false, null);
        var update2 = new StreamUpdate<string>("field", "text", false, null);

        // Act & Assert
        update1.ShouldBe(update2);
    }

    [Fact]
    public void RecordEquality_WithExpression_CreatesNewInstance()
    {
        // Arrange
        var original = new StreamUpdate<int>("field", "text", false, 0);

        // Act
        var modified = original with { PartialText = "new text" };

        // Assert
        modified.FieldName.ShouldBe("field");
        modified.PartialText.ShouldBe("new text");
        modified.IsFinal.ShouldBe(false);
        modified.FinalValue.ShouldBe(0);
        original.ShouldNotBe(modified);
    }

    [Fact]
    public void RecordEquality_WithExpressionMultipleMembers_CreatesNewInstance()
    {
        // Arrange
        var original = new StreamUpdate<string>("field", "text", false, null);

        // Act
        var modified = original with { IsFinal = true, FinalValue = "done" };

        // Assert
        modified.FieldName.ShouldBe("field");
        modified.PartialText.ShouldBe("text");
        modified.IsFinal.ShouldBeTrue();
        modified.FinalValue.ShouldBe("done");
    }

    [Fact]
    public void ToString_ContainsAllMembers()
    {
        // Arrange
        var update = new StreamUpdate<int>("answer", "partial", false, 42);

        // Act
        var str = update.ToString();

        // Assert
        str.ShouldContain("answer");
        str.ShouldContain("partial");
        str.ShouldContain("False");
        str.ShouldContain("42");
    }

    [Fact]
    public void GetHashCode_SameValues_SameHash()
    {
        // Arrange
        var update1 = new StreamUpdate<string>("f", "t", true, "v");
        var update2 = new StreamUpdate<string>("f", "t", true, "v");

        // Act & Assert
        update1.GetHashCode().ShouldBe(update2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentValues_DifferentHash()
    {
        // Arrange
        var update1 = new StreamUpdate<string>("f1", "t", true, "v");
        var update2 = new StreamUpdate<string>("f2", "t", true, "v");

        // Act & Assert
        update1.GetHashCode().ShouldNotBe(update2.GetHashCode());
    }

    [Fact]
    public void RecordWithNullableReferenceType_Stores()
    {
        // Arrange & Act
        var update = new StreamUpdate<object?>("field", "text", true, null);

        // Assert
        update.FinalValue.ShouldBeNull();
        update.IsFinal.ShouldBeTrue();
    }

    [Fact]
    public void RecordWithValueType_Stores()
    {
        // Arrange & Act
        var update = new StreamUpdate<double>("field", "3.14", true, 3.14);

        // Assert
        update.FinalValue.ShouldBe(3.14);
    }
}
