using Xunit;

// Several test classes call SqliteConnection.ClearAllPools(), which is process-wide; a class running in parallel
// could have its open SQLite handle closed under it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
