using NUnit.Framework;

// Parallel execution policy for the API suite.
//
// Reqnroll generates one NUnit fixture per feature file, so ParallelScope.Fixtures runs feature
// files concurrently while keeping the scenarios within a file sequential.
//
// Why fixture-level rather than ParallelScope.All:
//   * Scenarios inside a feature share a Background and are written in a deliberate order
//     (OrderPlacement's Background signs a trader in). Running them concurrently would work
//     today but makes the Background's meaning ambiguous, and that ambiguity is where
//     order-dependence creeps in later.
//   * Feature-level concurrency already gives most of the wall-clock benefit for a suite of
//     this size, because the features are of comparable length.
//
// Why 4 workers: the suite is I/O-bound against a single local application process, so more
// workers stop helping once the application is the bottleneck, and start producing timeouts
// that look like product flakiness. Four is a deliberate choice for a 2-core CI agent rather
// than an attempt to saturate a developer machine.
//
// The API suite is parallel-safe by construction: every scenario creates its own data with a
// unique generated reference and asserts only on that reference. No scenario asserts on a total
// row count, which is the assertion that forces a suite to run single-threaded.
[assembly: Parallelizable(ParallelScope.Fixtures)]
[assembly: LevelOfParallelism(4)]
