# CI strategy

`.github/workflows/ci.yml` is 408 lines, of which perhaps twenty carry all the risk. This
document walks the pipeline that exists, then documents — clearly labelled as such — the
extensions a real product would need but which are deliberately not wired up here.

## The pipeline that exists

Eight jobs. One builds, five verify, one scans, one aggregates.

| Job | Runs after | Timeout | What it proves |
| --- | --- | --- | --- |
| `build` | — | 10 min | The solution compiles warning-free under `Release`. |
| `framework-tests` | `build` | 10 min | The framework's own logic: configuration precedence, wait arithmetic, seed reproducibility, the read-only guard. |
| `api-tests` | `build` | 15 min | 45 API and data-integrity scenarios (the `@known-defect` one is filtered out — see below). |
| `ui-tests` | `build` | 25 min | 18 Playwright scenarios. Installs Chromium with `--with-deps`. |
| `mobile-tests` | `build` | 10 min | 40 mobile tests against the simulated driver. No device, no emulator, no Appium server. |
| `scripts` | — | 5 min | `python -m unittest discover -s scripts`, `bash -n` on every shell script, and `shellcheck --severity=warning`. |
| `secret-scan` | — | 5 min | `scripts/ci-quality-gate.sh --only secrets` over the full history (`fetch-depth: 0`). |
| `gate` | all six above | — | One required check instead of eight. |

`scripts` and `secret-scan` do not depend on `build` because neither needs a compiled artefact,
and making them wait would add three minutes to the feedback loop on a change that cannot break
them. `secret-scan` deliberately checks out full history: a secret that was committed and then
removed is still a leaked secret, and scanning only the tip would miss it entirely.

### Why the suites are separate jobs

A single `dotnet test` over the solution would be shorter YAML. Four reasons not to:

- **Per-suite timing.** The UI job has a 25-minute budget, the mobile job 10. One job means one
  timeout sized for the slowest thing in it, so a hung browser and a hung unit test are
  indistinguishable and both cost 25 minutes.
- **Failure attribution.** A red `api-tests` check tells a reviewer what broke before they open
  anything. A red `test` check does not.
- **Re-running one suite.** GitHub re-runs failed jobs individually: 18 scenarios rather than the
  274 tests CI executes in total (277 less the `@known-defect` scenario and the two Selenium
  comparisons).
- **Genuine isolation.** Each suite starts its own instance of `src/TradingDemo.App` — the API
  suite on port 5199 with `api-suite.db`, the UI suite on port 5198 with `ui-suite.db`. Both call
  `/test-support/reset`, so a shared database would mean each periodically wiping the other's
  data, and a shared process meant the first assembly to finish killed the application underneath
  the others. Separate jobs make that isolation structural rather than a convention.

The cost is honest: the build artefact is downloaded four times and each job pays the
`actions/setup-dotnet` cost. The jobs run concurrently, so wall-clock time is set by the slowest
suite rather than the sum.

## `continue-on-error: true` plus `fail-on-error: true`

This is the most important detail in the pipeline, and it appears in all four test jobs. From
`api-tests`:

```yaml
- name: Run API tests
  id: test
  continue-on-error: true
  run: >-
    dotnet test tests/Api.Tests/Api.Tests.csproj
    --configuration Release --no-build
    --filter "TestCategory!=known-defect..."
    --logger "trx;LogFileName=api.trx"
    --results-directory artifacts/test-results

- name: Publish results
  uses: dorny/test-reporter@v1
  if: always()
  with:
    name: API tests
    path: artifacts/test-results/api.trx
    reporter: dotnet-trx
    # THIS is what makes a failing suite fail the build. See the header comment.
    fail-on-error: true
```

Both halves are required, and each is useless — or worse — without the other.

**Without `continue-on-error`,** a non-zero exit from `dotnet test` aborts the job immediately.
The publish step never runs, the TRX is never parsed, no annotations appear on the pull request,
and the artefact upload is skipped. The one build you most need a report from is the only build
that does not produce one: a reviewer gets a red X and a raw log to scroll, with the failure
names buried in console output rather than surfaced as annotations against the failing scenarios.
That is the state most pipelines are in, and it is why "just read the logs" is such a common
instruction.

**With `continue-on-error` alone,** the step is marked as a warning and the job continues to a
successful conclusion. The suite is red; the check is green. This is strictly worse than having
no pipeline at all, because a pipeline is a claim about the state of the code and this one is an
active lie. A team with no CI knows it must test manually; a team with a green pipeline that does
not fail on test failures believes something false, and keeps believing it until a defect reaches
production.

The pairing separates two concerns that are usually conflated: producing the results whatever the
outcome (the test step, via `continue-on-error`) and deciding pass or fail from those results (the
publish step, via `fail-on-error`). `if: always()` on the publish and upload steps is the third
piece — without it those steps inherit the default `success()` condition and are skipped whenever
an earlier step in the job failed.

**The same trap, one layer up.** The `gate` job runs `if: always()`, which it must, so that it
reports something when a dependency fails rather than being skipped. But a job that always runs
and does nothing reports success. So it re-checks every dependency explicitly:

```yaml
for result in \
  "${{ needs.framework-tests.result }}" \
  ...
do
  case "$result" in
    success|skipped) ;;
    *) echo "::error::A required job did not succeed."; exit 1 ;;
  esac
done
```

`skipped` is tolerated deliberately, because a targeted `workflow_dispatch` run of one suite
legitimately skips the others. `failure` and `cancelled` are not. Without that loop, `gate` — the
single required status check protecting `main` — would report success on a completely red build.
It is exactly the same failure mode as `continue-on-error` alone, and it is worth checking for
both whenever reviewing someone else's pipeline.

## Tags, and which of them gate a pull request

The tags in the table below are the ones that genuinely appear in `tests/*/Features/*.feature`.
`Selenium` is an NUnit `[Category]` on the comparison fixture rather than a Gherkin tag, which is
why it has no `@`.

| Tag | Where it appears | Meaning | In CI |
| --- | --- | --- | --- |
| `@smoke` | All three suites | Must never break. The shortest path through each critical journey. | Runs; gates the PR |
| `@regression` | All three suites | Full behavioural coverage, including boundaries and outlines. | Runs; gates the PR (the suite is fast enough here) |
| `@security` | `Authentication.feature`, `OrderPlacement.feature`, `OrderHistory.feature`, `AccountAndMarketData.feature`, `Login.feature` | Authentication and authorisation behaviour. | Runs; gates the PR |
| `@contract` | Four of the five API feature files | JSON Schema validation of response shape. | Runs; gates the PR |
| `@known-defect` | `DataIntegrity.feature`, one scenario | A real, documented, unfixed defect. | Excluded by filter; reported in `DEFECT_REPORT.md` |
| `@api`, `@ui`, `@mobile` | Feature level | Which surface the specification is about. | Not filtered on in CI; used for local selection |
| `@orders`, `@authentication`, `@data-integrity` | Feature level | Functional area. | Not filtered on in CI; used for local selection and triage |
| `Selenium` | `tests/Ui.Tests/Selenium/SeleniumComparisonTests.cs` | The Playwright/Selenium comparison fixture. | Excluded — the agent has no Chrome |

Two filters do the whole job:

```
--filter "TestCategory!=known-defect"   # api-tests
--filter "TestCategory!=Selenium"       # ui-tests
```

Everything else runs. That is defensible *for this repository* because the whole suite takes
minutes; on a product where regression takes an hour the split would be by trigger rather than by
exclusion, which is sketched in the extensions section below. `workflow_dispatch` also composes
an optional `tag` input into the same expression —
`"TestCategory!=known-defect${{ inputs.tag && format('&TestCategory={0}', inputs.tag) || '' }}"`
— so a human investigating a failure can run one tag across one suite without editing YAML.

### Why `@known-defect` is excluded by tag

The seeded database contains one deliberate flaw: order `ORD-20240403-0006` is `Filled` with
zero rows in `order_fills`. The fill-reconciliation invariant in
`tests/Api.Tests/Features/DataIntegrity.feature` finds it and fails. There were three options.

| Option | Consequence |
| --- | --- |
| **Delete or comment out the scenario** | The gate goes green and stays green. The coverage is gone, so when the defect is fixed nothing proves it, and when a second order develops the same problem nothing notices. The knowledge lives in someone's memory. |
| **Leave it failing** | Honest, and corrosive. A permanently red build trains everybody to ignore red, at which point the *next* failure — a real regression — is also ignored. This is how teams end up with "the usual two failures" and then three, then five. |
| **Exclude by tag, document, report separately** | The gate is green and truthful about what it covers. The scenario still exists, still runs on demand, and still fails when run, so it is verifiable. The defect has a written record in `DEFECT_REPORT.md` with an owner and a verification plan. |

Only the third is defensible. The mechanism is one tag and one `--filter`, and the reasoning is
recorded in the feature file's header comment as well as in the workflow, so nobody has to guess
whether the exclusion was deliberate. The important property is that it is *narrow and named*:
adding the tag to a scenario is a visible diff a reviewer can challenge, whereas a blanket retry
policy or a lowered pass-rate threshold would hide the same failure with nobody having to justify
it.

## Artefacts

| Artefact | Condition | Retention | Why |
| --- | --- | --- | --- |
| `build-output` | Always | 1 day | Consumed by the four test jobs in the same run; useless afterwards. |
| `results-framework`, `results-api`, `results-ui`, `results-mobile` (TRX) | `if: always()` | Default | The record of what ran. Needed most on the runs that failed, which is why the condition is `always()` and not `failure()`. Also the input to `scripts/analyse_test_results.py` and `scripts/triage-failures.sh`. |
| `ui-failure-evidence` (screenshots + page source) | `if: steps.test.outcome == 'failure'` | 14 days | The state of the browser at the moment of failure. |

The asymmetry is deliberate. TRX is always uploaded because it is a few kilobytes of XML and the
only durable record of which tests ran; a green run's TRX is still evidence, and comparing two of
them is how you notice that a suite quietly stopped executing forty tests.

Evidence is uploaded only on failure because it is the opposite trade: full-page screenshots plus
DOM dumps on every green run, on every push, fill a busy repository's artefact storage with files
nobody opens. `src/QaFramework.Web/Setup/EvidenceCollector.cs` only captures when
`ScenarioContext.TestError` is set, so on a green run the directory is empty and the upload would
be pointless anyway. Note that the condition uses `steps.test.outcome` rather than `failure()`
precisely because `continue-on-error` means the *job* has not failed — the step outcome is the
only thing that still knows the truth. Video is a third case, off by default
(`"RecordVideo": false` in every `runsettings.json`), and switched on for one deliberate re-run
when a failure depends on *how* the application reached a state.

## Caching

```yaml
- name: Cache NuGet packages
  uses: actions/cache@v4
  with:
    path: ~/.nuget/packages
    key: nuget-${{ runner.os }}-${{ hashFiles('Directory.Packages.props') }}
    restore-keys: nuget-${{ runner.os }}-
```

`Directory.Packages.props` is the right key because Central Package Management makes it the
single place any dependency version can change (decision 11 in `docs/design-decisions.md`). The
two failure modes either side of it are both common. A cache keyed on nothing — a constant string
— never invalidates: it hits every time and serves a stale package set forever, until somebody
spends a day on a build failure caused by a version nobody references any more. A cache keyed on
everything — `hashFiles('**/*')`, or the commit SHA — never hits, because the key changes on every
push, and the save step still costs time, so the pipeline is slower than with no cache at all.

`restore-keys: nuget-${{ runner.os }}-` is the partial-hit fallback: when a version does change,
the most recent cache for that OS is restored and `dotnet restore` downloads only the delta.

## `--warnaserror` on the CI build, not in the `.csproj`

```yaml
- name: Build
  run: dotnet build --configuration Release --no-restore --warnaserror
```

The comment in the workflow states the reason: a developer must not be blocked mid-refactor by an
unused variable, and nothing that is warning-free only on one machine may reach `main`.

`TreatWarningsAsErrors` in `Directory.Build.props` would apply to every local build, so a
developer halfway through extracting a method could not compile to run a single test because the
intermediate state has an unused parameter. The reliable response to that is `<NoWarn>` entries,
added under time pressure and never removed, and the rule quietly stops meaning anything.

Putting it on the CI invocation keeps the rule at the boundary the code has to cross to become
shared. The cost is that a warning is discovered on push rather than while typing;
`.editorconfig` in the repository root narrows the gap by surfacing the same diagnostics in the
IDE. The editor advises, the pipeline decides.

---

## Extensions — documented, not implemented

**Everything in this section is illustrative.** None of it is in `ci.yml`. The YAML fragments are
written to be realistic rather than copy-pasteable, and each is here because it is the next thing
a real product would need. Documenting them and saying plainly that they are not wired up is more
useful than half-wiring them, which is the state most repositories are in.

### Browser matrix

```yaml
ui-tests:
  strategy:
    fail-fast: false          # one browser failing must not cancel the others
    matrix:
      browser: [chromium, firefox, webkit]
  steps:
    - run: pwsh tests/Ui.Tests/bin/Release/net10.0/playwright.ps1 install ${{ matrix.browser }} --with-deps
    - run: dotnet test tests/Ui.Tests/Ui.Tests.csproj --no-build
      env:
        QA_RunSettings__Browser__Name: ${{ matrix.browser }}
```

The suite already reads the browser from configuration (`BrowserSettings.Name`, overridable as
`QA_RunSettings__Browser__Name`), so this needs no code change. `fail-fast: false` matters: the
default cancels the remaining legs on the first failure, which is precisely when you most want to
know whether the problem is one browser or all three. This belongs on the nightly run rather than
on every pull request, and the artefact names would need `-${{ matrix.browser }}` appended so
three legs do not overwrite one another's results.

### Scheduled nightly regression

```yaml
on:
  schedule:
    - cron: '0 2 * * *'        # 02:00 UTC daily
  workflow_dispatch:
```

The nightly run is where the expensive things go: the full `@regression` set across the browser
matrix, the `@known-defect` scenario run *without* the exclusion so its status is checked rather
than assumed, and the Group B integrity invariants from
`database/validation/qa-validation-queries.sql` via `scripts/verify_backend_data.py`. It also
needs somewhere to report to, or it fails silently at 02:00 for a fortnight — and a notification
on transition rather than on every run is the version people do not mute.

### Multiple environments

```yaml
on:
  workflow_dispatch:
    inputs:
      environment:
        type: choice
        options: [Ci, Staging, Preprod]

jobs:
  api-tests:
    environment: ${{ inputs.environment }}
    env:
      QA_ENVIRONMENT: ${{ inputs.environment }}
      QA_Users__ActiveTrader__Password: ${{ secrets.ACTIVE_TRADER_PASSWORD }}
```

This is the extension the configuration layer was built for. `QA_ENVIRONMENT` already sits at the
top of the precedence chain — `QA_ENVIRONMENT` > `"Environment"` in `runsettings.json` > `Local`
— so pointing the suite elsewhere is one variable and an `Environment.Staging.json` file. Secrets
arrive as `QA_`-prefixed variables, which override `Environment.Overrides.json` (git-ignored) and
the committed `Environment.{Env}.json`. GitHub's `environment:` key adds per-environment secrets
and optional required reviewers, so a run against pre-production cannot be started casually.

### Mobile device matrix on a cloud device grid

```yaml
mobile-tests:
  strategy:
    fail-fast: false
    matrix:
      include:
        - platform: Android
          device: 'Pixel 7'
          os: '14'
        - platform: IOS
          device: 'iPhone 15'
          os: '17'
  env:
    QA_Mobile__Target: CloudGrid
    QA_Mobile__Platform: ${{ matrix.platform }}
    QA_Mobile__Device__DeviceName: ${{ matrix.device }}
    QA_Mobile__Device__PlatformVersion: ${{ matrix.os }}
    QA_MOBILE_GRID_URL: ${{ secrets.MOBILE_GRID_URL }}
    QA_MOBILE_GRID_USERNAME: ${{ secrets.MOBILE_GRID_USERNAME }}
    QA_MOBILE_GRID_ACCESS_KEY: ${{ secrets.MOBILE_GRID_ACCESS_KEY }}
```

Deliberately provider-agnostic: a generic cloud device grid reached over a URL with a user name
and an access key. Those three variable *names* are the committed defaults in
`tests/Mobile.Tests/runsettings.json` under `Mobile.CloudGrid`; no URL, user or key value appears
anywhere in the repository, and `MobileSettingsLoader` resolves them before any session is
attempted so a misconfigured grid run fails in the first second naming each missing variable.
What this fragment does *not* solve is the limitation stated in `docs/design-decisions.md`: the
mobile suite has never run against a device, and a real device leg would surface gesture, timing
and rendering problems the simulated driver cannot.

### Smoke versus regression, split by trigger

```yaml
- name: Choose the tag filter
  id: scope
  run: |
    if [ "${{ github.event_name }}" = "pull_request" ]; then
      echo 'filter=TestCategory=smoke|TestCategory=security' >> "$GITHUB_OUTPUT"
    else
      echo 'filter=TestCategory!=known-defect' >> "$GITHUB_OUTPUT"
    fi

- run: dotnet test --no-build --filter "${{ steps.scope.outputs.filter }}"
```

The trade is feedback speed against coverage per commit, and it only becomes worth making when
the full suite is slow enough that people start pushing without running it. The thing to watch is
that `@smoke` stays a genuine subset and does not quietly become "the tests that still pass".

### Sharding a slow UI suite

```yaml
strategy:
  fail-fast: false
  matrix:
    shard: [1, 2, 3, 4]
steps:
  - run: >-
      dotnet test tests/Ui.Tests/Ui.Tests.csproj --no-build
      --filter "${{ steps.split.outputs.filter }}"
      --logger "trx;LogFileName=ui-shard-${{ matrix.shard }}.trx"
```

Worth being precise about the mechanism, because "just shard it" hides the work. NUnit has no
built-in shard filter, so `steps.split.outputs.filter` has to come from somewhere: a `[Category]`
per shard (explicit, but a manual balancing act that rots), a filter derived from a hash of the
fixture name (automatic, unbalanced when one feature file is much slower), or a splitter that
reads timings from previous TRX files (best balance, most machinery). Sharding also multiplies
the result-merging problem — four TRX files to combine before one check can report, which
`scripts/analyse_test_results.py` already accepts as multiple inputs.

Sharding and in-process parallelism solve different halves. The UI suite already runs
`ParallelScope.Fixtures` with two workers; sharding adds machines. On a two-core agent more
workers contend for CPU until page loads slow and timeouts fire, which looks exactly like product
flakiness — so the next increment for a slow suite is agents, not workers.

### Flaky-test detection and quarantine reporting

```yaml
- name: Re-run failures once, and record the outcome separately
  if: steps.test.outcome == 'failure'
  run: |
    # Derive a --filter from the failing test names in the first TRX, re-run only those,
    # and log to a second TRX so the two runs stay distinguishable.
    dotnet test --no-build --filter "$FAILED_FILTER" --logger "trx;LogFileName=rerun.trx"
```

The crucial design point is that a test which fails and then passes must be recorded as **flaky**,
not as passed. An automatic retry that reports green is how a flake rate of fifteen per cent
becomes invisible, and a suite nobody trusts is only slightly better than no suite. Keeping the
re-run in a separate TRX is what preserves that distinction, and
`scripts/analyse_test_results.py` already accepts multiple TRX files and clusters failures by
normalised message. A quarantine list belongs in a committed file with an owner and a date per
entry, reported in every run, so that quarantining costs something and cannot be used as a silent
fix. `TEST_STRATEGY.md` section 11 sets out the flaky-test policy this would enforce.

### Allure publishing to GitHub Pages

```yaml
- uses: actions/upload-pages-artifact@v3
  with:
    path: artifacts/allure-report
- uses: actions/deploy-pages@v4
```

The Allure packages are referenced in `Directory.Packages.props` and no report is generated —
stated as a known limitation in `README.md` rather than half-wired, because a framework with
Allure configured and no published report is worse than one with no Allure at all. Doing it
properly means generating the report, keeping `allure-history` across runs so trend graphs mean
something, and defining failure categories so "application defect", "environment problem" and
"test defect" are separated in the report rather than in a conversation.

---

## What would change against a shared deployment

The pipeline above assumes each suite starts its own copy of the application. Pointing it at a
shared, deployed environment changes five things, and none of them is a code change to a test.

| Concern | Self-hosted (today) | Shared deployment |
| --- | --- | --- |
| Application lifecycle | `StartApplicationUnderTest: true`; `ApplicationUnderTest` starts and kills the process | `false`. The suite attaches to a running instance. `ApplicationUnderTest.StartAsync` is already idempotent — if something healthy answers at the configured address it attaches and logs that it did — so this is one setting. |
| Secrets | Seeded demo passwords committed in `Environment.Local.json`, with the exception argued in `README.md` | Absent from the repository. Injected as `QA_Users__*__Password` from the platform's secret store, scoped per environment. The loader already fails hard rather than defaulting when a password is missing. |
| Data cleanup | `/test-support/reset` restores the whole seed | Targeted deletes only. A reset endpoint against a shared environment is a catastrophic liability, and it would destroy other people's data. `ResourceTracker` expresses cleanup as an arbitrary undo action rather than a list of ids, so the interface is unchanged — only the action registered in `ScenarioSession` changes. |
| Timeouts | `PageLoadMs: 15000`, `ApiRequestMs: 30000` in `runsettings.json` | Longer, because you are absorbing other people's load and other people's deploys. One settings file, no code. |
| Retries | None. Every failure is a real finding, because the suite owns the application | A bounded retry policy for transport-level noise only — connection reset, 502 from a load balancer mid-deploy — never for assertion failures. The distinction is already available: the framework separates a transport failure from an HTTP error, so "the application is not reachable" and "the application returned 422" are different signals. |

Two consequences are worth stating rather than discovering. Data assertions must stop assuming
exclusive ownership — no scenario may assert a total row count, and every scenario must address
its own records by business key, which this suite already does for parallelism reasons. And
failure triage gains a category it does not have today, "someone else's deploy", which is
precisely why a flake report and a clear transport-versus-assertion distinction stop being
niceties.
