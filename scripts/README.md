# Scripting toolkit

Seven small tools that sit between the test framework, the database and the pipeline.
Python here is standard library only and shell is bash with `set -euo pipefail`; nothing
in this directory needs an install step, because the moment it does it stops being
usable on the agent where you actually need it.

## Scripts

| Script | Purpose | Example invocation | Exit codes |
| --- | --- | --- | --- |
| `analyse_test_results.py` | Parses `.trx` files, reports totals, pass rate, the slowest tests, and groups failures into clusters by normalised message so one root cause reads as one line rather than forty. Can gate a pipeline on pass rate. | `python scripts/analyse_test_results.py artifacts/test-results/*.trx --slowest 10 --fail-under 95` | `0` report produced and gate met, `1` pass rate below `--fail-under` or no executed tests found, `2` file missing, unreadable or not TRX |
| `verify_backend_data.py` | Opens the demo SQLite database read-only (`file:...?mode=ro`) and runs the Group B data-integrity invariants from `database/validation/qa-validation-queries.sql`: fill reconciliation, orphaned rows, duplicate references, the limit-price rule and quantity boundaries. | `python scripts/verify_backend_data.py --format json` | `0` all invariants hold, `1` at least one violation or a query errored, `2` cannot run (no database, wrong schema) |
| `generate_test_data.py` | Generates reproducible synthetic order or user data as CSV or JSON. Everything is prefixed `QA-` so automation-created data is greppable and removable; emails use the reserved `example.invalid` domain. | `python scripts/generate_test_data.py --entity orders --count 50 --seed 1234 --format csv --output artifacts/orders.csv` | `0` data generated, `2` bad arguments or the output file could not be written |
| `test_qa_scripts.py` | `unittest` suite over the pure logic above: TRX parsing and clustering, the invariant SQL against a throwaway SQLite database, and generator reproducibility. Run in CI on every push. | `python -m unittest discover scripts` | `0` all tests passed, `1` a test failed |
| `run-tests.sh` | Wrapper over `dotnet test` with suite selection, category filter, environment and headed mode. Prints the resolved command so it can be copied and reproduced by hand. | `./scripts/run-tests.sh --suite ui --filter smoke --environment ci --headed` | `0` all selected suites passed, `1` tests failed, `2` bad arguments, `3` prerequisite missing (no SDK, no matching project) |
| `triage-failures.sh` | Extracts failing test names and the first N lines of each failure from `.trx` files, lists captured screenshots, and prints a compact triage summary. Pure `grep`/`sed`/`awk`, so it works on any agent. | `./scripts/triage-failures.sh --lines 8 artifacts/test-results` | `0` directory scanned (whether or not failures were found), `2` bad arguments or missing directory |
| `ci-quality-gate.sh` | Composes the pipeline gate: build warnings, secret scan with a documented allowlist, test pass rate via the Python analyser, and the backend-data verifier. Runs every gate, then prints one summary. | `./scripts/ci-quality-gate.sh --min-pass-rate 100 --max-warnings 0` | `0` every gate passed, `1` at least one gate failed, `2` bad arguments |

Every script supports `--help`, and the help text repeats its own exit codes so the table
above cannot silently go stale.

## Notes worth knowing before you run them

- **The data verifier finds a real defect.** Order `ORD-20240403-0006` in the committed
  seed data is `Filled` with no fill rows, so `verify_backend_data.py` exits `1` out of
  the box. That is deliberate, it is the example used for defect reporting, and it is why
  `ci-quality-gate.sh` has an explicit `--allow-data-violations` switch rather than
  quietly tolerating violations.
- **A gate whose tool is missing fails.** If Python is not installed,
  `ci-quality-gate.sh` reports `FAIL`, not `PASS`. A gate that passes because it could
  not run is the worst possible outcome. `--allow-missing-tools` downgrades that to
  `SKIP` for local use only.
- **Suites are discovered by path.** `run-tests.sh --suite api` matches test projects
  whose path contains `api`, so `tests/TradingDemo.Api.Tests` needs no registration. If
  nothing matches, the script lists what it did find and exits `3`.
- **`triage-failures.sh` treats XML as text on purpose**, and says so in its header: it
  is the zero-dependency first look. `analyse_test_results.py` is the correct parser, and
  it handles the TRX namespace and `<InnerResults>` from data-driven tests properly.
- **Generated data carries its seed.** `generate_test_data.py` prints the seed and run
  segment to stderr so the exact data set behind a failure can be replayed with
  `--seed <n>`, while stdout stays clean for piping.

## Why a QA engineer should be able to script

Testing work is mostly integration work: a test framework, a build system, a database, an
artefact store and a pipeline, none of which were designed to talk to each other. Scripts
are the glue, and being unable to write them means waiting for somebody who can.

Four concrete returns, all of them present in this directory:

- **Triage speed.** A red build with forty failures is not forty problems. Clustering
  them by message turns half an hour of scrolling into one look, and it changes the first
  question from "which test failed?" to "what broke?".
- **Verification beyond the API response.** An HTTP 201 proves what the service said, not
  what it stored. A read-only query set that asserts the data is still self-consistent
  catches the class of defect that every UI and API assertion misses - and running it as
  a scheduled job catches it without anyone writing a new test.
- **Data preparation.** Suites that share fixtures collide, and suites that depend on
  hand-crafted rows rot. Generating identifiable, seeded data on demand removes both
  problems and makes cleanup a single predicate.
- **Pipeline gates.** A quality standard nobody enforces is a preference. Exit codes are
  how a standard becomes a rule, and a gate is only trustworthy if it fails loudly when
  it cannot run.
