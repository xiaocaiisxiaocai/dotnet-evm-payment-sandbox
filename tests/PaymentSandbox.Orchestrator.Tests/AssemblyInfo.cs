using Xunit.Sdk;
using Xunit.v3;

// Temporary database cleanup calls SqliteConnection.ClearAllPools(), which is
// process-wide. Keep test cases serial so one cleanup cannot dispose another
// case's opening connection; explicit Task.WhenAll concurrency tests still run.
[assembly: Parallelization(Mode = ParallelMode.None)]
