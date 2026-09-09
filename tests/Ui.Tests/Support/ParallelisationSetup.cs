using NUnit.Framework;

// Parallel execution policy for the UI suite.
//
// Reqnroll generates one NUnit fixture per feature file, so this runs feature files
// concurrently while keeping scenarios within a file sequential.
//
// Why 2 workers rather than the API suite's 4: each worker owns a browser, and a Chromium
// instance costs far more memory than an HTTP client. On a 2-core CI agent, four browsers
// contend for CPU, page loads slow down, and timeouts start firing - producing failures that
// look like product flakiness but are entirely self-inflicted. Two is a deliberate ceiling
// chosen for the smallest agent this is expected to run on, and it is the first number to
// revisit if the suite grows.
//
// What makes concurrency safe here:
//   * A fresh browser context per scenario - no shared cookies or session storage.
//   * Every scenario asserts on its own order, addressed by generated reference, never on a
//     total row count. Row-count assertions are what force a UI suite to run single-threaded.
//   * Sign-in happens per scenario via the API, so no scenario depends on another's session.
//
// The Selenium comparison fixture is marked [NonParallelizable] separately: it manages its own
// driver outside this framework's lifecycle and is an illustration rather than coverage.
[assembly: Parallelizable(ParallelScope.Fixtures)]
[assembly: LevelOfParallelism(2)]
