using SignalGrid.Ampelum.Streaming;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Streaming;

public class IStreamListenerTests
{
    private sealed class TestListener : IStreamListener<string>
    {
        public string? LastPartialText { get; set; }
        public bool LastIsFinal { get; set; }
        public string? LastFinalValue { get; set; }
        public int CallCount { get; set; }

        public void OnUpdate(string partialText, bool isFinal, string? finalValue)
        {
            LastPartialText = partialText;
            LastIsFinal = isFinal;
            LastFinalValue = finalValue;
            CallCount++;
        }
    }

    [Fact]
    public void OnUpdate_PartialUpdate_StoresValues()
    {
        // Arrange
        var listener = new TestListener();

        // Act
        listener.OnUpdate("hello", false, null);

        // Assert
        listener.LastPartialText.ShouldBe("hello");
        listener.LastIsFinal.ShouldBeFalse();
        listener.LastFinalValue.ShouldBeNull();
        listener.CallCount.ShouldBe(1);
    }

    [Fact]
    public void OnUpdate_FinalUpdate_StoresFinalValue()
    {
        // Arrange
        var listener = new TestListener();

        // Act
        listener.OnUpdate("hello world", true, "complete");

        // Assert
        listener.LastPartialText.ShouldBe("hello world");
        listener.LastIsFinal.ShouldBeTrue();
        listener.LastFinalValue.ShouldBe("complete");
        listener.CallCount.ShouldBe(1);
    }

    [Fact]
    public void OnUpdate_MultiplePartialUpdates_TracksLatest()
    {
        // Arrange
        var listener = new TestListener();

        // Act
        listener.OnUpdate("h", false, null);
        listener.OnUpdate("he", false, null);
        listener.OnUpdate("hel", false, null);
        listener.OnUpdate("hello", true, "hello");

        // Assert
        listener.LastPartialText.ShouldBe("hello");
        listener.LastIsFinal.ShouldBeTrue();
        listener.LastFinalValue.ShouldBe("hello");
        listener.CallCount.ShouldBe(4);
    }

    [Fact]
    public void IStreamListener_WithIntType_Works()
    {
        // Arrange
        var listener = new TestListener<int>();

        // Act
        listener.OnUpdate("42", true, 42);

        // Assert
        listener.LastPartialText.ShouldBe("42");
        listener.LastFinalValue.ShouldBe(42);
    }

    private sealed class TestListener<T> : IStreamListener<T>
    {
        public string? LastPartialText { get; set; }
        public bool LastIsFinal { get; set; }
        public T? LastFinalValue { get; set; }

        public void OnUpdate(string partialText, bool isFinal, T? finalValue)
        {
            LastPartialText = partialText;
            LastIsFinal = isFinal;
            LastFinalValue = finalValue;
        }
    }
}
