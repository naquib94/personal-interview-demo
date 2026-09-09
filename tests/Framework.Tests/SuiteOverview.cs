// ==============================================================================================
// WHY THIS PROJECT EXISTS
// ==============================================================================================
//
// A test framework is production code for the QA team, and it is production code with an
// unusually nasty failure mode. If Wait.ForValueAsync has an off-by-one, or the configuration
// precedence chain silently ignores an environment variable, the result is not a broken test -
// it is a suite that reports the wrong answer confidently. A red test gets investigated within
// the hour. A green test that verified nothing can survive for a year.
//
// None of the four reference frameworks this project learned from had a single test of their
// own framework code. Every one of them had hundreds of tests of the application, written on
// top of helpers that nobody had ever exercised in isolation. This project has tests for the
// framework, and that gap is one of its deliberate differentiators.
//
// ----------------------------------------------------------------------------------------------
// WHAT IS COVERED, AND WHY THOSE THINGS
// ----------------------------------------------------------------------------------------------
//
// The selection criterion throughout is "what breaks silently if it is wrong", not "what is
// easy to reach". Trivial getters are not tested; a getter that returns the wrong field fails
// loudly in the first scenario that uses it.
//
//   Configuration/    The precedence chain and the fail-fast guards. Highest value in the
//                     project. A missed environment variable points the whole suite at the
//                     wrong system, and an empty default password turns an unset CI secret into
//                     "authentication is broken" - a defect report against the product for a
//                     problem in the pipeline.
//
//   Synchronisation/  Wait's deadline arithmetic, and above all the shape of its failure
//                     messages. The entire justification for this helper over Thread.Sleep is
//                     that its timeout says what it last observed. If that message regresses to
//                     "timed out waiting for condition", the helper has lost its reason to
//                     exist while still passing every application test.
//
//   TestData/         Reproducibility (a seed that is not honoured makes a failure
//                     untriageable) and cleanup ordering (reverse order is the whole reason
//                     ResourceTracker works against a schema it knows nothing about).
//
//   Database/         The read-only guard. This is a safety property: if it regresses, a test
//                     suite gains the ability to write to the database it is supposed to be
//                     observing, and from then on the tests verify a world the application
//                     cannot itself produce.
//
//   Api/              Null-dropping, invariant number formatting and non-throwing lazy
//                     deserialisation. Culture-sensitive formatting in particular is a genuine
//                     cross-machine bug class: it passes on the developer's machine and fails
//                     on an agent with a comma decimal separator, or vice versa.
//
// ----------------------------------------------------------------------------------------------
// WHAT IS DELIBERATELY NOT COVERED HERE
// ----------------------------------------------------------------------------------------------
//
// The mobile layer - driver factories, capability assembly, the cloud-grid fail-fast, mobile
// locator platform resolution and the simulated driver - is already covered by
// tests/Mobile.Tests/UnitTests. Duplicating it here would double the maintenance cost of those
// behaviours and create two places a reader has to check to know whether something is tested.
//
// ----------------------------------------------------------------------------------------------
// PARALLELISM POLICY
// ----------------------------------------------------------------------------------------------
//
// Fixture-level parallelism, because the fixtures are independent by construction: each one
// either touches no shared state at all, or is marked [NonParallelizable] because it does.
//
// The shared state in question is process-global and there is no way to isolate it: environment
// variables (QA_ENVIRONMENT, QA_DATA_SEED, QA_Users__*), CultureInfo.CurrentCulture, and
// TestLog.Sink. A fixture that mutates any of those runs on its own. Getting this wrong would
// make the suite intermittently red, which would be a particularly embarrassing bug in a
// project whose central argument is about flakiness.
//
// Two workers rather than four: this suite is CPU-light and its slowest fixtures are the
// deliberately-timed synchronisation tests, which are dominated by wall-clock waits of a few
// hundred milliseconds. More workers would not shorten the run, and every extra worker is
// another thread that could expose an unmarked global-state dependency as a flake.

[assembly: Parallelizable(ParallelScope.Fixtures)]
[assembly: LevelOfParallelism(2)]
