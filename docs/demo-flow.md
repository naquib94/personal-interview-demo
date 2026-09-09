# Demo flow

A timed screen-share script: roughly thirteen minutes of content for a slot that will
realistically be interrupted. Every file path and command below is taken from the repository and
is written for PowerShell, run from the repository root. The order is deliberate — architecture
first so everything after it has somewhere to sit, and the SQL section placed so the strongest
moment lands in the middle rather than at the end, where an interviewer running short of time
would miss it.

| # | Section | Time | Running total |
| --- | --- | --- | --- |
| 1 | Architecture | 2:00 | 2:00 |
| 2 | UI automation | 2:00 | 4:00 |
| 3 | API automation | 2:00 | 6:00 |
| 4 | SQL and backend verification | 1:00 | 7:00 |
| 5 | CI/CD | 2:00 | 9:00 |
| 6 | Test strategy | 2:00 | 11:00 |
| 7 | AI-assisted QA | 1:30 | 12:30 |

## Pre-flight checklist

Do all of this before the call, not during it.

```powershell
cd Senior-QA-Automation-Demo

# 1. Build. A build failure on screen is unrecoverable; a build failure ten minutes earlier is nothing.
dotnet build

# 2. Playwright browsers. Once per machine.
pwsh tests/Ui.Tests/bin/Debug/net10.0/playwright.ps1 install chromium

# 3. Confirm green. This is the CI gate's filter, so it should be entirely green.
dotnet test --filter "TestCategory!=known-defect&TestCategory!=Selenium"

# 4. Confirm the one deliberate failure still fails, so section 4 cannot surprise you.
dotnet test tests/Api.Tests --filter "TestCategory=known-defect"

# 5. No leftover application processes holding a port.
Get-Process -Name TradingDemo.App -ErrorAction SilentlyContinue | Stop-Process -Force
```

Then the presentation hygiene, which matters more than it sounds:

- Terminal and editor font sizes up to something readable on a compressed video stream — 16pt or
  more. Minimap and side panels closed.
- Close every other window. No email, no chat, no notifications.
- Repository open in one editor window with the section 1 files already in tabs, and `README.md`
  open at the counts table, because "how many tests?" is asked early and often.
- For the UI section, have the application already running in its own terminal so the browser has
  something to talk to immediately. `ApplicationUnderTest.StartAsync` is idempotent — if
  something healthy answers at the configured address it attaches and logs that it did — so this
  saves the start-up wait without changing any behaviour. Mind the port: the launch profile in
  `src/TradingDemo.App/Properties/launchSettings.json` listens on 5199, which is the **API**
  suite's address, so for the UI suite pass the port explicitly:

  ```powershell
  dotnet run --project src/TradingDemo.App --urls http://localhost:5198
  ```

---

## 1. Architecture — 2 minutes

**Open, in this order:** `README.md` (the architecture diagram), then
`src/QaFramework.Core/QaFramework.Core.csproj`, then
`src/TradingDemo.AppModel/Pages/AccountPage.cs`, then `tests/Ui.Tests/reqnroll.json`.

**Points to make:**

- Ten projects, three layers, one direction of dependency. The framework knows nothing about
  trading; the application model knows the trading application once; the suites hold only
  specifications.
- `QaFramework.Core` references no test runner and no automation library, which is why
  `tests/Framework.Tests` can unit-test it — 171 tests of the framework's own logic.
- `src/TradingDemo.App` is referenced by no test project. The suite starts it as a process and
  talks to it over HTTP and the browser only, exactly as a real client would.
- `AccountPage.cs` is 30 lines with no logic — six component declarations and one method. All the
  behaviour lives in the components.
- `"bindingAssemblies": [{ "assembly": "TradingDemo.AppModel" }]` in `reqnroll.json` is the one
  line that lets the API and UI suites share hooks and page objects rather than copying them.

**Likely questions:**

*Isn't ten projects over-engineered for five HTML pages?* For five pages, yes. The layering
exists to answer one question repeatedly: if this changes, how many files do I edit? Fewer
projects would be defensible — merging `QaFramework.Api` into `QaFramework.Core`, say. What I
would not merge is the framework and the application model, because that is the boundary that
lets the framework be reused.

*Where does this design break down?* Two places, both written up in `docs/architecture.md`.
`QaFramework.Core/Database/TradingQueries.cs` holds trading-domain SQL inside a supposedly
generic library, and `QaFramework.Mobile/Screens/Trading/` holds four screen objects for this
application. Both belong in `TradingDemo.AppModel`.

*Why not the ASP.NET test host instead of a real process?* The tests would then run in the same
process as the application and could reach into its internals. Driving it over HTTP is what stops
the suite asserting against a world the application cannot produce.

---

## 2. UI automation — 2 minutes

**Open:** `src/QaFramework.Web/Components/Actions/Button.cs`, then
`src/QaFramework.Web/Components/Grid/DataGrid.cs`, then
`tests/Ui.Tests/Features/OrderPlacement.feature`.

**Run:**

```powershell
$env:QA_RunSettings__Browser__Headless = "false"
$env:QA_RunSettings__Browser__SlowMotionMs = "300"
dotnet test tests/Ui.Tests --filter "TestCategory=smoke"
```

Start this *before* talking, so the browser is doing something visible while you explain the
code. Clear both variables afterwards.

**Points to make:**

- `Button.ClickAsync` clicks and then waits for the application to be idle, in the same method.
  A test cannot race the application, because there is no way to perform the click without the
  wait. That is a guarantee rather than a convention.
- The wait is on one element: `data-testid="app-busy"`, which the application keeps active for
  the whole duration of any in-flight request. One line of developer cooperation, and it removes
  more flakiness than any retry logic.
- Rows are addressed by business key. `Orders.CellShouldHaveTextAsync(reference, "status",
  status)` — never by index, because index addressing fails silently: the test still finds a row
  and asserts against the wrong one.
- `CellShouldHaveTextAsync` asserts exact text, because "contains Filled" also passes on
  "Partially Filled" and "Unfilled".
- Evidence on failure only: screenshot plus page source, at the moment of failure, named after
  the scenario.

**Likely questions:**

*Why Playwright rather than Selenium?* Better defaults for a modern single-page application:
locators are lazy descriptions so they never go stale, and web-first assertions retry to a
timeout. `tests/Ui.Tests/Selenium/SeleniumComparisonTests.cs` implements two of the same
scenarios with `WebDriverWait` so the difference is concrete rather than a preference — and the
honest conclusion is in it: a well-built Selenium framework wraps waits into components exactly
as this one does, at which point most of the difference disappears. For a large estate already on
Grid I would not propose a rewrite.

*How do you handle flaky tests?* By removing the cause rather than retrying. Almost all of it is
synchronisation and row addressing. Where a retry is genuinely needed, a test that fails and then
passes must be reported as flaky, not as passed, or the flake rate becomes invisible.

*What about a browser matrix?* Documented in `docs/ci-strategy.md` with the YAML and explicitly
not implemented. The suite already reads the browser from configuration, so it is a matrix
dimension rather than a code change — but it belongs on a nightly run, not on every pull request.

---

## 3. API automation — 2 minutes

**Open:** `tests/Api.Tests/Features/OrderPlacement.feature`, then
`src/TradingDemo.AppModel/Contracts/ApiContracts.cs`, then
`tests/Api.Tests/StepDefinitions/OrderPlacementSteps.cs` (the tampered-token step).

**Run:**

```powershell
dotnet test tests/Api.Tests --filter "TestCategory=smoke"
```

**Points to make:**

- 46 scenarios across five feature files. The chain is feature file → step definition → domain
  API client → `ApiClient` → RestSharp, and RestSharp appears in exactly one file, so no test
  ever touches a `RestResponse`.
- The 400 versus 422 distinction is asserted deliberately. "You sent me nonsense" and "I
  understood you and the answer is no" are different things and clients handle them differently.
- Every invalid field is reported at once, and that is asserted:
  `the validation errors mention the fields "symbol, side, quantity"`. A test that only checked
  "an error was returned" would pass if the API regressed to one field at a time.
- The suite declares its **own** copy of every response DTO, duplicating
  `src/TradingDemo.App/Domain/Contracts.cs`. A project reference would remove the duplication and
  make every breaking contract change invisible: renaming a field would update both sides at once
  while every real client broke.
- JSON Schema on top of that, because `order.Quantity == 1.5m` still passes if the API starts
  returning `"1.5"` as a string — the deserialiser coerces it, and every strongly typed client
  breaks.
- Authorisation is asserted as `404`, not `403`, when a trader reads another trader's order: a
  403 confirms the reference exists.

**Likely questions:**

*Why duplicate the DTOs? That is what a shared package is for.* A shared package is right when
the provider publishes one and consumers depend on the published version. It is wrong when the
"package" is the provider's own internal model with a project reference around it. The cost — two
files to edit for a legitimate contract change — is the mechanism, not a side effect. The
residual risk is that the reviewer, not the compiler, is what stops silent drift.

*Why JSON Schema as well as typed assertions?* Typed assertions catch wrong values; the schema
catches wrong shapes. `"additionalProperties": false` means a new field fails the test, which is
right for a demonstration of contract discipline and wrong on a fast-moving API.

*How do you keep 46 API scenarios fast?* `ParallelScope.Fixtures` with four workers, one
application instance owned by the suite on port 5199 with its own `api-suite.db`, and no scenario
asserting a total row count.

---

## 4. SQL and backend verification — 1 minute

**This is the strongest moment in the demo. Stage it deliberately.**

**Open:** `database/validation/qa-validation-queries.sql` at query B1.

**Say first, before running anything:** the query set is organised by purpose, not by table.
Group A verifies what an operation actually persisted, because an HTTP 201 only proves what the
API said. Group B are invariants where a returned row *is* a defect, so there are no expected
values to maintain. Group C is exploratory risk profiling. And one of the Group B queries finds a
real bug in the committed seed data.

**Run:**

```powershell
dotnet test tests/Api.Tests --filter "TestCategory=known-defect"
```

**Expected output** — the test fails, and the failure names the offending row:

```text
Data integrity violation: an order marked Filled must have fills that sum to its ordered
quantity. Found 1 offending row(s).

Offending rows:
[
  {
    "OrderReference": "ORD-20240403-0006",
    "Status": "Filled",
    "OrderedQuantity": 3,
    "FilledQuantity": 0,
    "FillCount": 0
  }
]
```

**Points to make:**

- The query is `LEFT JOIN ... GROUP BY ... HAVING COALESCE(SUM(f.fill_quantity), 0) <>
  o.quantity`. Order `ORD-20240403-0006` is `Filled` with zero rows in `order_fills`. The status
  is a claim; the fills are the evidence.
- A query that finds a real bug is worth considerably more than one that returns nothing.
- The failure names the row rather than saying "1 row violated the invariant": the first is
  actionable, the second sends someone to a debugger.
- It is written up in `DEFECT_REPORT.md` as a real defect report — impact, scope, hypothesis,
  suggested fix, verification plan — and quarantined **by tag**, not deleted and not left red.
- The verifier is read-only by construction: the connection opens `ReadOnly` and a guard rejects
  any statement that is not a `SELECT` or a `WITH`. Test data is created through the API, so the
  suite never verifies a state the application could not produce.

**Likely questions:**

*Why not just fix it?* Because the point is to show the invariant working. In a real team it would
be raised, triaged and fixed, and the scenario would then pass and rejoin the gate. What I would
not do is delete the test or leave the build red — one loses the coverage, the other trains
everybody to ignore red.

*Why SQLite?* Zero infrastructure, so `dotnet test` works immediately after a clone with no
Docker and no service account. Dialect realism is genuinely lost — no `NUMERIC` precision
enforcement, weaker `CHECK` semantics — and on a real product the verification layer would target
the product's own engine.

---

## 5. CI/CD — 2 minutes

**Open:** `.github/workflows/ci.yml`, at the header comment, then the `api-tests` job, then the
`gate` job.

**Points to make:**

- Eight jobs: `build`, four test suites, `scripts`, `secret-scan`, and one aggregate `gate` that
  is the single required status check for branch protection. Suites are separate jobs so each has
  its own timeout and its own failure attribution, can be re-run alone, and genuinely owns its
  own application instance, port and database file.
- **The pairing.** `continue-on-error: true` on the test step, `fail-on-error: true` on the
  publish step. Without the first, a failing suite aborts the job and the results never publish,
  so the one build you most need a report from produces none. With the first alone, a red suite
  looks green — which is worse than having no pipeline, because it is an active lie.
- The `gate` job runs `if: always()` and therefore re-checks `needs.*.result` explicitly. Without
  that loop it would report success on a completely red build: the same failure mode, one layer
  up.
- `--warnaserror` is on the CI build rather than in the `.csproj`, so a developer is not blocked
  mid-refactor while nothing warning-free-only-locally reaches `main`.
- The NuGet cache is keyed on `Directory.Packages.props`. A cache keyed on nothing never
  invalidates; one keyed on everything never hits.

**Likely questions:**

*Where is the flaky-test handling, the Allure report, the browser matrix?* Documented in
`docs/ci-strategy.md` with realistic YAML and labelled as not implemented. That is deliberate:
three of the four reference frameworks I studied had Allure configured and never published a
report, which is worse than not having it at all.

*How would this change against a shared environment?* Five things, none of them a change to a
test: `StartApplicationUnderTest` off, secrets from the platform's secret store as `QA_`-prefixed
variables, targeted deletes instead of a seed restore, longer timeouts, and a bounded retry
policy for transport-level noise only — never for assertion failures.

*Would you gate on coverage?* Not on line coverage of the application. I would gate on the suite
passing, zero build warnings, the secret scan and the data invariants. Line coverage as a gate
produces tests written to touch lines.

---

## 6. Test strategy — 2 minutes

**Open:** `TEST_STRATEGY.md` at section 4 (risk-based prioritisation), then section 6.2 (what to
leave manual), then `tests/Api.Tests/Features/OrderPlacement.feature` at the boundary comment
around line 88.

**Points to make:**

- Prioritisation is by risk, and the tags are the output of it, not decoration: `@smoke` and
  `@security` on every pull request, `@regression` for full behavioural coverage,
  `@contract` for response shape, `@known-defect` quarantined.
- Order placement carries the heaviest coverage because it moves money and a defect is
  immediately visible to customers. That is stated in the feature file's own description.
- The pyramid applied honestly: login-failure permutations live at API level only, because
  driving five of them through a browser tests the same rule five times through the slowest
  possible surface. The UI gets one representative failure path.
- Boundary data has to isolate the rule under test. The comment at line 88 records a real
  mistake: a boundary test used `2 BTCUSD`, the exact maximum, which costs around 122,000 against
  a 25,000 balance, so the insufficient-funds rule fired first and the test could never have
  verified the boundary. EURUSD's maximum of 50 costs about 54, so it isolates one rule.

**Likely questions:**

*How do you decide what not to automate?* Cost of automation against cost of the defect
escaping, plus how stable the requirement is. Anything where a human judgement is the assertion —
does this price chart look right, is this disclosure legible — I would keep exploratory, and I
would automate the data it depends on instead.

*What metrics would you track?* Ones that change a decision: pass rate by suite, flake rate as a
first-class number, time to feedback, escaped defects by area. Not test count, and not line
coverage. `TEST_STRATEGY.md` section 10 lists the ones I would refuse to report and why.

*How would you onboard someone onto this suite?* Clone, `dotnet build`, one `dotnet test`. That
is the whole setup, deliberately: a suite needing a page of setup before it runs once is a suite
new joiners do not run.

---

## 7. AI-assisted QA — 1 to 2 minutes

**Open:** `AI_ASSISTED_QA.md` at "The central argument" — examples 1 and 2 — cross-referenced as
items 7 and 8 of the "Bugs this framework found" table in `README.md`. Then
`tests/Api.Tests/StepDefinitions/OrderPlacementSteps.cs` at the tampered-token step.

**Points to make:**

- Building this repository produced a real defect list, all found by running tests rather than
  reading code. Two of the eight are the interesting ones, and both were plausible enough to
  survive a review at a glance.
- **Item 7:** a boundary test using the exact maximum quantity of an expensive instrument. The
  insufficient-funds rule fired first, so the test could never have verified the boundary it was
  written for. It failed, which is the lucky case.
- **Item 8:** the tampered-token test originally flipped the *last* base64url character of the
  signature, which only alters unused padding bits — so the signature still verified and the test
  passed while testing nothing. A negative test that passes without testing anything is the most
  dangerous kind. The fix is in the code and so is the reasoning: alter a character in the
  *middle* of the signature, which guarantees a different byte array.
- The conclusion: generated or assisted test code has to be **run and read**, not merely
  reviewed. Reviewing catches code that looks wrong; only running and reading catches code that
  looks right and asserts nothing. Both of those would have passed a review.
- Where assistance genuinely helps: boilerplate, test data shapes, drafting Gherkin from an
  acceptance criterion, explaining an unfamiliar failure. Where it does not: deciding what is
  worth testing, choosing data that isolates a rule, judging whether an assertion can fail.

**Likely questions:**

*How do you know an assisted test is any good?* Make it fail on purpose. If I cannot break the
application, or the data, in a way that turns the test red, the test is not testing what it
claims. That check would have caught item 8 in thirty seconds.

*Would you let it write your framework?* For a component that follows an existing pattern, yes,
and then I read every line. Three of the eight defects in that table were found by unit tests of
the framework itself, and that safety net is what makes it reasonable.

---

## If something goes wrong

| Symptom | What to do |
| --- | --- |
| **No network** | Nothing here needs it after the first build. The application under test is in the repository, the database is a file, and the Playwright browsers are already installed. Say so — it is a point in the framework's favour, not an excuse. |
| **Playwright browsers missing** | `pwsh tests/Ui.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`. On a TLS-intercepting corporate network this fails with `SELF_SIGNED_CERT_IN_CHAIN`; either set `$env:NODE_TLS_REJECT_UNAUTHORIZED = "0"` for the install only and remove it immediately afterwards, or set `$env:QA_RunSettings__Browser__Channel = "chrome"` and drive an installed browser with no download at all. The second is the durable answer and the reason the setting exists. |
| **Port already in use** | `Get-Process -Name TradingDemo.App \| Stop-Process -Force`, then re-run. This happens when a previous run was cancelled before `AfterTestRun` could kill the process tree. |
| **A test fails live** | Read the failure message out loud. That is the whole point of the framework's error messages: it will name the component ("Failed to click the Place order button"), give the current URL, and for a grid it will list the rows that were actually present. Then say what you would check next and why. An interviewer learns more from watching you triage than from a green run. |
| **Something hangs** | Ctrl+C, then the port command above, then move on to the next section and come back if there is time. Do not debug live for more than about ninety seconds. |
| **The known-defect test unexpectedly passes** | The database was re-seeded from a modified `002_seed.sql`, or the wrong `DatabasePath` is in play. Fall back to showing query B1 in `database/validation/qa-validation-queries.sql` and the row in `DEFECT_REPORT.md`. |

## Exact commands, in one place

```powershell
# Everything. The known-defect scenario fails by design.
dotnet test

# The CI gate's filter: everything except the quarantined defect and the Selenium comparison.
dotnet test --filter "TestCategory!=known-defect&TestCategory!=Selenium"

# Smoke only, across all suites.
dotnet test --filter "TestCategory=smoke"

# One suite at a time.
dotnet test tests/Framework.Tests
dotnet test tests/Api.Tests
dotnet test tests/Ui.Tests
dotnet test tests/Mobile.Tests

# The demo moment: the invariant that finds a real defect.
dotnet test tests/Api.Tests --filter "TestCategory=known-defect"

# Headed browser with slow motion, so a human can follow along.
$env:QA_RunSettings__Browser__Headless = "false"
$env:QA_RunSettings__Browser__SlowMotionMs = "300"
dotnet test tests/Ui.Tests --filter "TestCategory=smoke"
Remove-Item Env:\QA_RunSettings__Browser__Headless
Remove-Item Env:\QA_RunSettings__Browser__SlowMotionMs

# Run the application on its own. The launch profile uses 5199 (the API suite's port);
# pass --urls http://localhost:5198 to pre-start it for the UI suite instead.
dotnet run --project src/TradingDemo.App
```

`SlowMotionMs` is a presentation aid, not a synchronisation tool, and the comment in
`tests/Ui.Tests/runsettings.json` says so — using it to "fix" a flaky test would hide the real
problem. Worth mentioning if you use it on screen.

## The closing message

Three sentences, and they should be the last thing said:

> This is a framework I would be comfortable handing to a team on their first day: one command
> after a clone, no environment to obtain, and failures that say what broke rather than which
> selector timed out. The parts I am least happy with are written down — the mobile suite has
> never run on a device, the SQL is SQLite rather than a real engine, and two libraries hold
> domain knowledge they should not — because a framework's limitations are the thing a new
> maintainer most needs to know. What I would want to talk about further is where the same
> reasoning would need to change for a real trading platform: eventual consistency in order
> state, a shared environment instead of a self-hosted one, and the fact that in this domain a
> test that passes for the wrong reason is not a nuisance but a risk.
