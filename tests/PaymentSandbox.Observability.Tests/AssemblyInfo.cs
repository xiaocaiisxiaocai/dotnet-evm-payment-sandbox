using Xunit.Sdk;
using Xunit.v3;

// ActivitySource and Meter listeners observe process-wide static instruments.
// Serial execution keeps each test's evidence isolated and deterministic.
[assembly: Parallelization(Mode = ParallelMode.None)]
