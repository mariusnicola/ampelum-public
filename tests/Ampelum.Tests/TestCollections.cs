using Xunit;

/// <summary>
/// Tests that register global <see cref="System.Diagnostics.ActivityListener"/> instances must run
/// serially to prevent parallel tests from receiving each other's activities and corrupting
/// captured-tag assertions.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ActivityListenerCollection : ICollectionFixture<object>
{
    public const string Name = "ActivityListenerTests";
}
