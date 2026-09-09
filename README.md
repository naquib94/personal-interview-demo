# Senior QA Automation — Framework Showcase

A working, self-contained test automation framework built around a small trading-platform demo
application. Clone it, run one command, and 277 tests execute against a real application with a
real database.

> **Interview demonstration.** This repository was built as a portfolio piece to show how I
> approach designing and maintaining a QA automation framework. The application under test, the
> data, the schema and every scenario are synthetic and were written for this project. It
> contains no code, data or configuration from any employer.

---

## Contents

- [What this demonstrates](#what-this-demonstrates)
- [Quick start](#quick-start)
- [Architecture](#architecture)
- [Project layout](#project-layout)
- [The application under test](#the-application-under-test)
- [Running tests](#running-tests)
- [UI automation](#ui-automation)
- [API automation](#api-automation)
- [Mobile automation](#mobile-automation)
- [SQL and backend data validation](#sql-and-backend-data-validation)
- [Scripting toolkit](#scripting-toolkit)
- [CI/CD](#cicd)
- [Configuration and secrets](#configuration-and-secrets)
- [Bugs this framework found](#bugs-this-framework-found)
- [Design decisions](#design-decisions)
- [Scalability](#scalability)
- [Known limitations](#known-limitations)
- [Future improvements](#future-improvements)
- [Further reading](#further-reading)

---

## What this demonstrates

| Capability | Where to look |
|---|---|
| Framework design and separation of concerns | `src/QaFramework.*` → `src/TradingDemo.AppModel` → `tests/*` |
| Web/UI automation | `tests/Ui.Tests`, `src/QaFramework.Web/Components` |
| REST API automation | `tests/Api.Tests`, `src/QaFramework.Api` |
| Mobile automation structure | `tests/Mobile.Tests`, `src/QaFramework.Mobile` |
| BDD | `tests/*/Features/*.feature` (Reqnroll) |
| SQL / data validation | `database/validation/qa-validation-queries.sql`, `src/QaFramework.Core/Database` |
| Test planning and risk-based prioritisation | [`TEST_STRATEGY.md`](TEST_STRATEGY.md) |
| CI/CD | [`.github/workflows/ci.yml`](.github/workflows/ci.yml), [`docs/ci-strategy.md`](docs/ci-strategy.md) |
| Python and shell scripting | `scripts/` |
| Testing the framework itself | `tests/Framework.Tests` — 171 unit tests |
| Responsible AI-assisted QA | [`AI_ASSISTED_QA.md`](AI_ASSISTED_QA.md) |

**Current state — all suites green:**

| Suite | Tests | Notes |
|---|---:|---|
| `Framework.Tests` | 171 | Unit tests of the framework's own configuration, waits, data and assertion code |
| `Api.Tests` | 46 | 45 pass; 1 fails **by design** — see [the planted defect](#sql-and-backend-data-validation) |
| `Ui.Tests` | 20 | 18 Playwright scenarios + 2 Selenium comparison tests |
| `Mobile.Tests` | 40 | Appium-oriented, run against a simulated driver — no device required |
| **Total** | **277** | 276 pass, 1 quarantined by tag |

---

## Quick start

**Prerequisites:** .NET 10 SDK. That is all — the database is a file, and the application under
test is in this repository and is started by the test suite itself.

```powershell
git clone <this-repo>
cd Senior-QA-Automation-Demo

dotnet build

# Playwright ships its own browsers; install them once.
pwsh tests/Ui.Tests/bin/Debug/net10.0/playwright.ps1 install chromium

# Everything except the deliberately-failing defect scenario and the Selenium comparison.
dotnet test --filter "TestCategory!=known-defect&TestCategory!=Selenium"
```

There is no environment to configure, no database to provision, no VPN and no credentials to
obtain. That is deliberate: a suite that needs a page of setup before it will run once is a
suite new joiners do not run.

<details>
<summary>If Playwright's browser download is blocked by a corporate proxy</summary>

TLS interception makes the download fail with `SELF_SIGNED_CERT_IN_CHAIN`. Two options:

```powershell
# 1. Trust the intercepting certificate for the download only.
$env:NODE_TLS_REJECT_UNAUTHORIZED = "0"
pwsh tests/Ui.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
Remove-Item Env:\NODE_TLS_REJECT_UNAUTHORIZED

# 2. Or drive a browser you already have installed, no download needed.
$env:QA_RunSettings__Browser__Channel = "chrome"   # or "msedge"
```

The second option exists because of this exact situation — see `BrowserSettings.Channel` in
`src/QaFramework.Core/Configuration/Settings.cs`.
</details>

---

## Architecture

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│  SPECIFICATIONS  —  what the product must do                                 │
│                                                                              │
│  tests/Api.Tests      tests/Ui.Tests      tests/Mobile.Tests                 │
│  Features/*.feature   Features/*.feature  Features/*.feature                 │
│  StepDefinitions/     StepDefinitions/    StepDefinitions/                   │
│                                                                              │
│  tests/Framework.Tests  —  unit tests of the framework itself                │
└───────────────────────────────┬──────────────────────────────────────────────┘
                                │  depends on
┌───────────────────────────────▼──────────────────────────────────────────────┐
│  APPLICATION MODEL  —  how THIS product works                                │
│  src/TradingDemo.AppModel                                                    │
│                                                                              │
│  Pages/       page objects (declarative; no logic)                           │
│  Screens/     mobile screen objects                                          │
│  ApiClients/  domain service layer + endpoint constants                      │
│  Contracts/   response DTOs and JSON schemas                                 │
│  Hooks/       shared lifecycle, inherited by all three suites                 │
│  Setup/       application lifecycle, per-scenario session                    │
└───────────────────────────────┬──────────────────────────────────────────────┘
                                │  depends on
┌───────────────────────────────▼──────────────────────────────────────────────┐
│  FRAMEWORK  —  reusable, knows nothing about trading                         │
│                                                                              │
│  QaFramework.Web       QaFramework.Api      QaFramework.Mobile               │
│  Playwright lifecycle  RestSharp transport  Appium driver factories          │
│  Components/           ApiResponse<T>       Platform locators                │
│  Synchronisation/      Assertions/          Simulated driver                 │
│                        JSON schema                                           │
│                    ┌───────────────────────┐                                 │
│                    │  QaFramework.Core     │                                 │
│                    │  Configuration        │                                 │
│                    │  Synchronisation/Wait │                                 │
│                    │  Logging              │                                 │
│                    │  TestData + cleanup   │                                 │
│                    │  Database verifier    │                                 │
│                    └───────────────────────┘                                 │
└──────────────────────────────────────────────────────────────────────────────┘

                    ┌──────────────────────────────────┐
                    │  APPLICATION UNDER TEST          │
                    │  src/TradingDemo.App             │
                    │  Minimal API + static UI + SQLite│
                    │  Started by the suite            │
                    └──────────────────────────────────┘
```

**The dependency direction is the point.** `QaFramework.*` contains no endpoint paths, no page
objects and no business rules — it could be pointed at an entirely different product unchanged.
`TradingDemo.AppModel` models this application exactly once, and all three functional suites
share it through Reqnroll's `bindingAssemblies` setting. Adding a fourth suite (contract tests,
a nightly deep-regression pass, a performance smoke) means adding feature files, not
re-modelling the application.

Full detail, including hook ordering and the reasoning behind the component abstraction, is in
[`docs/architecture.md`](docs/architecture.md).

---

## Project layout

```text
Senior-QA-Automation-Demo/
├── src/
│   ├── QaFramework.Core/          Config, waits, logging, test data, DB verification
│   ├── QaFramework.Api/           HTTP transport, typed responses, schema assertions
│   ├── QaFramework.Web/           Playwright lifecycle + the component library
│   ├── QaFramework.Mobile/        Appium abstraction + simulated driver
│   ├── TradingDemo.AppModel/      Pages, screens, API clients, contracts, shared hooks
│   └── TradingDemo.App/           The application under test
│
├── tests/
│   ├── Api.Tests/                 46 API scenarios
│   ├── Ui.Tests/                  18 Playwright scenarios + 2 Selenium comparisons
│   ├── Mobile.Tests/              40 mobile tests (simulated driver)
│   └── Framework.Tests/           171 unit tests of the framework
│
├── database/
│   ├── schema/001_schema.sql      Synthetic trading schema
│   ├── seed/002_seed.sql          Deterministic seed data (with one planted defect)
│   └── validation/                The QA validation query set, documented
│
├── scripts/                       Python + shell QA toolkit
├── docs/                          Architecture, decisions, locators, CI, demo script
├── .github/workflows/ci.yml       The pipeline
│
├── README.md
├── TEST_STRATEGY.md
├── AI_ASSISTED_QA.md
├── DEFECT_REPORT.md
└── SECURITY_REVIEW.md
```

---

## The application under test

A deliberately small trading platform: sign in, view your account, view market prices, place an
order, review order history. It exists to give the framework something real to drive, and it is
sized to produce genuine testing problems rather than to be impressive:

- **Authentication** with three distinct outcomes — valid, invalid credentials (401, deliberately
  indistinguishable between a wrong password and an unknown user), and a suspended account (403).
- **Field validation** that reports *every* invalid field at once, not just the first.
- **Business rules** answered with `422` rather than `400`, because "I understood you and the
  answer is no" is a different thing from "you sent me nonsense" and clients must handle them
  differently.
- **Boundaries** worth testing — per-instrument minimum and maximum quantities, a non-tradable
  instrument, and an insufficient-funds rule.
- **Pagination**, an audit trail, and a single consistent error envelope.
- **A SQLite database** whose state can be verified independently of the API.

Two endpoints exist purely for the tests — `/health` and `/test-support/reset` — and that
trade-off is argued explicitly in `src/TradingDemo.App/Program.cs` rather than left implicit.

The UI carries `data-testid` attributes throughout and exposes a single busy indicator
(`data-testid="app-busy"`) that stays active for the whole duration of any in-flight request.
That one element is what removes the need for hardcoded waits — see
[`docs/locator-strategy.md`](docs/locator-strategy.md).

---

## Running tests

```powershell
# Everything (the known-defect scenario will fail by design)
dotnet test

# The CI gate: everything except the quarantined defect and the Selenium comparison
dotnet test --filter "TestCategory!=known-defect&TestCategory!=Selenium"

# One suite
dotnet test tests/Api.Tests
dotnet test tests/Ui.Tests
dotnet test tests/Mobile.Tests
dotnet test tests/Framework.Tests

# By tag
dotnet test --filter "TestCategory=smoke"
dotnet test --filter "TestCategory=security"
dotnet test --filter "TestCategory=contract"

# Watch the browser (useful for a demo; not a way to fix a flaky test)
$env:QA_RunSettings__Browser__Headless = "false"
$env:QA_RunSettings__Browser__SlowMotionMs = "300"
dotnet test tests/Ui.Tests --filter "TestCategory=smoke"
```

There is also a friendlier wrapper: `scripts/run-tests.sh --suite ui --filter smoke --headed`.

### Tags

| Tag | Meaning | Runs |
|---|---|---|
| `@smoke` | Must never break | Every pull request |
| `@regression` | Full behavioural coverage | Nightly (and on PR in this repo, since the suite is fast) |
| `@security` | Authentication and authorisation | Every pull request |
| `@contract` | JSON Schema validation of response shape | Every pull request |
| `@known-defect` | A real, documented, unfixed defect | Reported, excluded from the gate |
| `@Selenium` | The Playwright/Selenium comparison fixture | Excluded in CI (needs a local Chrome) |

---

## UI automation

The central idea is that **a component models a control archetype, not a screen**. A button, a
text input, a data grid — each owns its own waiting, its assertions, its logging and its failure
message. Behaviour is therefore written once per *kind* of control rather than once per screen,
and page objects become declarative manifests with no logic at all:

```csharp
// src/TradingDemo.AppModel/Pages/AccountPage.cs — the whole page
public sealed class AccountPage(WebTestContext context) : ApplicationPage(context, "account.html")
{
    public TextElement Heading       { get; } = new(context, "account-heading", "the Account heading");
    public TextElement AccountNumber { get; } = new(context, "account-number-text", "the account number");
    public TextElement Balance       { get; } = new(context, "account-balance-text", "the account balance");
    // ...
}
```

Consequences worth pointing out:

- **A click includes its own wait.** `Button.ClickAsync` waits for the element, clicks, and then
  waits for the application to become idle. A test *cannot* race the application, because the
  wait is part of the click rather than something each caller must remember.
- **Writes verify themselves.** `TextInput.EnterAsync` fills the field and then reads it back,
  which catches masks, trims and max-lengths at the field that misbehaved instead of three steps
  later.
- **Locators are rebuilt on every access** (`ILocator Locator => Context.App.Locator(...)` is a
  property, not a field), so components never go stale across a re-render.
- **Rows are addressed by business key, never by index** —
  `Orders.CellShouldHaveTextAsync("ORD-20240401-0001", "status", "Filled")`. Index addressing
  fails *silently*: the test still finds a row and asserts against the wrong one.
- **The `ActivePage` / `App` seam** in `WebTestContext` means that if the application is ever
  embedded in an iframe, one property changes and all 35 components and every page follow
  automatically.
- **Failure evidence** — screenshot and page source, captured at the moment of failure, named
  after the scenario. Not per step: a filmstrip of every step is expensive and almost never read.

A single **Selenium** fixture (`tests/Ui.Tests/Selenium/SeleniumComparisonTests.cs`) implements
two of the same scenarios with `WebDriverWait` so the differences in synchronisation, stale
elements and assertion retry are concrete rather than a matter of preference. It is honest about
the conclusion: Playwright has better defaults for a modern SPA; Selenium is a W3C standard with
unmatched grid and language breadth; and most of the flakiness people attribute to Selenium comes
from implicit waits and sleeps, which are choices rather than properties of the tool.

---

## API automation

```text
Feature file  →  Step definition  →  Domain API client  →  ApiClient  →  HTTP
                                     (TradingApiClient)   (RestSharp)
```

Each arrow is a boundary something could be replaced at. No test ever touches `RestResponse` —
RestSharp appears in exactly one file — so changing HTTP library would be a change rather than a
project.

What the suite validates, and why each matters:

| Check | Example |
|---|---|
| Status codes, with the 400/422 distinction asserted deliberately | `the order is rejected as a bad request` vs `the order is refused by the business rules` |
| Every invalid field reported at once | `the validation errors mention the fields "symbol, side, quantity"` |
| **JSON Schema** — catches contract regressions typed assertions cannot | `ResponseSchemas.Order`; a quantity changing from number to string passes a typed assertion and fails a schema |
| Business rules | non-tradable instrument, quantity bounds, insufficient funds |
| Authorisation | a trader cannot read another trader's order — asserted as `404`, not `403`, because a 403 confirms the reference exists |
| Token integrity | a well-formed token with an invalid signature is rejected |
| Side effects | a successful sign-in must leave an audit event — verified in the database |
| **Backend persistence** | the API saying `201` only proves the API said so |

Two API-layer details worth mentioning:

- **Transport failures are distinguished from HTTP errors.** "The application is not running" and
  "the application returned 422" require completely different responses from a human, so the
  framework says which happened rather than reporting a bare status mismatch.
- **Domain clients expose two shapes**: `PlaceOrderAsync` returns the raw response for tests
  *about* order placement, and `PlaceOrderOrFailAsync` asserts success for tests that merely need
  an order to exist. Collapsing them means either negative tests fight the framework or setup
  failures surface as confusing null references later.

---

## Mobile automation

An Appium-oriented structure that runs green in CI **with no device, no emulator and no Appium
server**. That is stated plainly here because it is stated plainly in the code:
`SimulatedMobileDriver` carries a class comment saying it proves the framework's wiring — hooks,
waits, locator resolution, screen objects, step bindings, reporting — and proves nothing
whatsoever about any application.

What makes it credible rather than decorative:

- **`IMobileDriver` is a real seam.** `AppiumMobileDriver` and `SimulatedMobileDriver` implement
  it, and a `RunTarget` setting (`Simulated` | `LocalAppium` | `Emulator` | `CloudGrid`) selects
  between them. The identical feature files, step definitions and screen objects run against real
  hardware by changing one configuration value, not a code branch.
- **Driver factories are unit-testable without a server.** `BuildOptions(MobileRunSettings)` is
  deliberately separate from the method that opens a session, so `tests/Mobile.Tests/UnitTests`
  asserts that the correct `AppiumOptions` are assembled for Android and iOS from committed
  capability files.
- **`MobileLocator` carries a platform pair** with `For(Platform)`, solving the problem an
  Android-only suite never has to face. Preference order is documented: accessibility id first,
  XPath last.
- **Cloud grid credentials are environment-variable names only.** There is no default URL, user
  or key anywhere in the repository, and requesting the `CloudGrid` target without them fails
  immediately naming each missing variable.

I would not claim mobile depth on the strength of this. What it shows is that I know what the
abstraction needs to look like, and that I would rather demonstrate the architecture honestly
than fake a device run.

---

## SQL and backend data validation

The query set in `database/validation/qa-validation-queries.sql` is organised by *purpose*,
because the distinction matters more than the SQL:

| Group | Purpose | Expectation |
|---|---|---|
| **A. Post-operation verification** | "The API returned 201 — did the right thing happen?" | Parameterised, one row expected |
| **B. Data-integrity invariants** | "Is the data self-consistent?" | Must return **zero** rows |
| **C. Exploratory / risk profiling** | "Where should I aim my testing?" | Run by a human |

Group B is the interesting half. Each query is written so that **a returned row *is* a defect**,
which means there are no expected values to maintain — an integrity suite that needed updating
every time the seed data changed would be abandoned within a month.

### The planted defect

The seed data contains one deliberate flaw: order `ORD-20240403-0006` is marked `Filled` but has
no rows in `order_fills`. The fill-reconciliation invariant finds it:

```sql
SELECT o.order_reference, o.quantity AS ordered, COALESCE(SUM(f.fill_quantity), 0) AS filled
FROM orders o
    LEFT JOIN order_fills f ON f.order_id = o.id
WHERE o.status = 'Filled'
GROUP BY o.id, o.order_reference, o.quantity
HAVING COALESCE(SUM(f.fill_quantity), 0) <> o.quantity;
```

```powershell
dotnet test tests/Api.Tests --filter "TestCategory=known-defect"
```

```
Data integrity violation: an order marked Filled must have fills that sum to its ordered
quantity. Found 1 offending row(s).
    "OrderReference": "ORD-20240403-0006",
    "OrderedQuantity": 3, "FilledQuantity": 0, "FillCount": 0
```

A query that finds a real bug is worth considerably more than one that returns nothing. It is
documented in [`DEFECT_REPORT.md`](DEFECT_REPORT.md) and quarantined **by tag** rather than
deleted or commented out — deleting loses the coverage, and leaving it red trains everybody to
ignore a red build.

The framework's `DatabaseVerifier` is **read-only by construction**: the connection is opened
`ReadOnly`, and a guard rejects any statement that is not a `SELECT` or a `WITH`. Test data is
created through the application's own API, so the suite never verifies a state the application
itself could not produce.

---

## Scripting toolkit

`scripts/` — Python standard library only, no `pip install`:

| Script | Purpose |
|---|---|
| `analyse_test_results.py` | Parse `.trx`, report pass rate and slowest tests, **cluster failures by root cause** so 40 failures sharing one cause show as one line; `--fail-under` gates a pipeline |
| `verify_backend_data.py` | Run the integrity invariants read-only against SQLite; non-zero exit on violation |
| `generate_test_data.py` | Reproducible synthetic order/user data (`--seed`), `QA-` prefixed, `example.invalid` addresses |
| `test_qa_scripts.py` | `unittest` coverage of the pure logic above — run in CI |
| `run-tests.sh` | Friendly wrapper over `dotnet test` with `--suite`, `--filter`, `--environment`, `--headed` |
| `triage-failures.sh` | Extract failing names and messages from artefacts, list screenshots, print a triage summary |
| `ci-quality-gate.sh` | Composite gate: build warnings, secret scan, pass rate, backend data. `--only <gate>` for CI |

---

## CI/CD

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) — build once, then four test suites, the
scripting toolkit and a secret scan in parallel, behind one aggregate gate suitable for branch
protection.

**The most important detail in the whole pipeline** is this pairing:

```yaml
- name: Run API tests
  continue-on-error: true          # so results ALWAYS publish, even on failure
  run: dotnet test ... --logger trx

- name: Publish results
  uses: dorny/test-reporter@v1
  if: always()
  with:
    fail-on-error: true            # ...and THIS is what fails the build
```

Both halves are required. Without `continue-on-error`, a failing suite aborts the job and the one
build you most need a report from is the one that never produces one. With `continue-on-error`
alone, a red suite looks green — which is worse than having no pipeline, because it is an active
lie. Two of the reference frameworks I have worked with had the first half without the second.
The aggregate `gate` job re-checks `needs.*.result` explicitly for the same reason one layer up.

Extensions — browser matrix, scheduled regression, multiple environments, device matrix, suite
sharding, Allure publishing — are documented with realistic YAML in
[`docs/ci-strategy.md`](docs/ci-strategy.md), clearly labelled as illustrative rather than wired
up.

---

## Configuration and secrets

Two files per suite, and the split is what stops configuration rotting:

- **`runsettings.json`** — *how* we test. Browser, timeouts, evidence. Identical everywhere.
- **`Environment.{Local,Ci}.json`** — *where* we test. URLs, database path, which user roles exist.

Precedence, documented in code and covered by unit tests in `tests/Framework.Tests/Configuration`:

```text
QA_ENVIRONMENT env var  >  "Environment" in runsettings.json  >  Local

QA_-prefixed env vars   >  Environment.Overrides.json  >  Environment.{Env}.json
(secrets live here)        (git-ignored, per developer)    (committed)
```

Two deliberate behaviours:

- **A missing password is a hard failure**, not an empty default. An empty password produces
  authentication failures that look exactly like a product defect, and the resulting hunt is
  expensive. The error names the role and the environment variable that would fix it.
- **Users are addressed by role**, so Gherkin reads `Given the active trader is signed in` and the
  credential lives in exactly one place per environment.

The seeded demo passwords **are** committed. They unlock nothing but a SQLite file on your own
machine, and committing them is what makes `dotnet test` work immediately after a clone. That is
a narrow, documented exception rather than the pattern — see
[`SECURITY_REVIEW.md`](SECURITY_REVIEW.md).

---

## Bugs this framework found

Building this repository produced a genuinely useful list. These are all real, all found by
running the tests rather than by reading the code, and each is documented at the point it was
fixed:

| # | Bug | How it was found | Why it matters |
|---|---|---|---|
| 1 | `wwwroot` was not copied to the build output, so every page 404'd | Playwright reported "element not found" on a blank page | The failure looked like a locator problem; the evidence capture (page source showed `<body></body>`) is what identified it in one minute |
| 2 | The order form rendered its success message and then called `form.reset()`, whose handler cleared the alert | A UI assertion on the success message | **Invisible to a human** clicking through — the message vanished within a frame |
| 3 | `JsonSchema.Net`'s default `OutputFormat` is `Flag`, so schema failures named no field and wrongly blamed the schema | A **unit test of the framework** | No test of the *application* could have found it: the assertion still went red, just unhelpfully |
| 4 | `ConfigurationLoader`'s empty-users guard was unreachable — the binder maps an empty section to `null`, so it threw `NullReferenceException` instead of its own clear message | A framework unit test | Exactly the opaque failure the guard existed to prevent |
| 5 | `Enum.TryParse` accepts numeric strings, so `QA_ENVIRONMENT=1` silently resolved to `Ci` | A framework unit test | Defeated the entire reason for using an enum |
| 6 | RestSharp keeps `Content-Type` in `ContentHeaders`, not `Headers`, so `ShouldBeJson` could never pass | Running the API suite | An assertion that can never pass is worse than no assertion |
| 7 | A boundary test used `2 BTCUSD` — the maximum — but that costs ~122,000 against a 25,000 balance, so the insufficient-funds rule fired first | The test failed while the boundary logic was correct | **The test could never have verified what it claimed.** Choosing data that isolates the rule under test is the whole skill |
| 8 | A tampered-token test flipped the last base64url character of the signature, which only alters unused padding bits — so the signature still verified | The test passed | A negative test that **passes without testing anything** is the most dangerous kind |

Items 7 and 8 are the two I would most want to talk about in an interview. Both were plausible,
both would survive a review-at-a-glance, and both were worthless. They are the concrete argument
in [`AI_ASSISTED_QA.md`](AI_ASSISTED_QA.md) for why generated tests need to be *run and read*,
not merely reviewed.

---

## Design decisions

Summarised here; argued properly with alternatives and accepted trade-offs in
[`docs/design-decisions.md`](docs/design-decisions.md).

| Decision | Why | Trade-off accepted |
|---|---|---|
| Playwright primary, Selenium as one comparison | Better defaults for a modern SPA; auto-waiting removes the largest source of flakiness | Less grid maturity and language breadth than Selenium |
| Reqnroll / BDD | A business-readable specification and a shared vocabulary — not a different syntax for tests | An indirection layer that is not worth it for a purely technical suite |
| Self-hosted application under test | No shared-environment flakiness, no accumulating data, every failure is a real finding | Only possible when the system is small enough to start on an agent |
| SQLite | Zero infrastructure; runs anywhere including a bare CI agent | Less dialect realism than PostgreSQL or T-SQL |
| Test suite declares its **own** copies of the API DTOs | Sharing the app's DTOs means a breaking contract change updates both sides and no test notices | Two files to edit for a legitimate contract change — which is the mechanism, not a cost |
| Read-only database access, `SELECT`/`WITH` only | A suite that can write will eventually set up states the application cannot produce | Setup must go through the API, which is occasionally slower |
| Evidence on failure only | The state at failure is what gets read; per-step screenshots are expensive and ignored | No filmstrip of a passing run |
| Each suite owns its own port and database | Genuine isolation; suites run concurrently | Two application processes instead of one |
| Simulated mobile driver | Honest, runs in CI, proves the abstraction | Proves nothing about a real device — and says so |

---

## Scalability

- **Parallel execution** is configured per suite: `ParallelScope.Fixtures` with 4 workers for the
  API suite and 2 for the UI suite, because a browser costs far more memory than an HTTP client
  and four browsers on a two-core agent contend until timeouts start firing. What makes it safe is
  structural: a fresh browser context per scenario, rows addressed by business key, and **no
  scenario asserting on a total row count** — that single assertion is what forces most suites to
  run single-threaded.
- **Adding a screen** is a page class of field declarations plus feature files. No new waits, no
  new assertions, no framework change.
- **Adding a suite** means feature files and step definitions; the application model is reused.
- **Adding a platform** — the mobile layer already resolves locators per platform and selects a
  driver factory by enum.
- **Named timeouts** (`src/QaFramework.Core/Configuration/TimeoutSettings.cs`) mean tuning for a
  slow agent is one settings file rather than a hunt for literals.

---

## Known limitations

Stated plainly, because pretending otherwise is worse:

- **The mobile suite has never run on a device.** It runs against a simulated driver. The
  architecture is real; the device execution is not.
- **The application under test is small.** It has no async workflows, no message queue and no
  third-party integration, so the eventual-consistency polling in `Wait.ForValueAsync` and
  `WaitForOrderStatusAsync` is demonstrated against a synchronous system. The pattern is the right
  shape for a real trading platform; that is said in the code comments rather than implied.
- **Cleanup restores the seed** rather than deleting individual records, because the demo API has
  no delete endpoint. That is only safe because each suite owns its own database, and a shared
  environment would need targeted deletes. The `ResourceTracker` interface is unchanged either
  way, which is the point of expressing cleanup as an undo action rather than a list of IDs.
- **No performance, accessibility, visual or contract-broker testing.** Each would be a genuine
  addition; none is faked here.
- **Allure packages are referenced but no report is generated.** Results are published as TRX. I
  left this incomplete deliberately rather than half-wiring it — three of the four frameworks I
  studied had Allure configured and never published a report, which is worse than not having it.
- **The seeded demo passwords are committed.** Argued above and in `SECURITY_REVIEW.md`.
- **Only one browser is exercised.** A matrix is documented but not wired up.

---

## Future improvements

In the order I would actually do them:

1. **Allure reporting, properly** — generated, published to GitHub Pages, with failure categories.
2. **A browser matrix** on the nightly run, not on every pull request.
3. **Consumer-driven contract tests** (Pact) — JSON Schema catches shape regressions, but a broker
   catches the ones that matter to a specific consumer.
4. **Flake detection**: re-run failures once, record the outcome, and report a flake rate as a
   first-class metric rather than a folk memory.
5. **A real device run** on a cloud grid, gated on credentials being present, so the mobile claim
   becomes a demonstrated one.
6. **k6 performance smoke** with thresholds as pass/fail gates, results converted into the same
   report as the functional suites.
7. **Accessibility assertions** on the key screens — cheap with `axe-core`, and a regulated domain
   makes it a requirement rather than a nicety.
8. **Mutation testing** on the framework libraries, to check the 171 unit tests actually constrain
   behaviour rather than merely executing it.

---

## Further reading

| Document | Contents |
|---|---|
| [`TEST_STRATEGY.md`](TEST_STRATEGY.md) | Requirement analysis, test design, a risk-based prioritisation matrix, the pyramid applied to this repo, what **not** to automate, data and environment strategy, metrics worth tracking |
| [`AI_ASSISTED_QA.md`](AI_ASSISTED_QA.md) | Responsible AI-assisted QA, with the two worthless-but-plausible generated tests as the worked example |
| [`DEFECT_REPORT.md`](DEFECT_REPORT.md) | The planted defect written up as a real defect report, end to end |
| [`SECURITY_REVIEW.md`](SECURITY_REVIEW.md) | Confirmation of independent authorship, and the secret-scanning gate |
| [`docs/architecture.md`](docs/architecture.md) | Layering, the component abstraction, hook ordering, DI, parallelism |
| [`docs/design-decisions.md`](docs/design-decisions.md) | Decision log with alternatives and trade-offs |
| [`docs/locator-strategy.md`](docs/locator-strategy.md) | The `data-testid` contract, preference order, anti-patterns |
| [`docs/ci-strategy.md`](docs/ci-strategy.md) | The pipeline explained, plus documented extensions |
| [`docs/demo-flow.md`](docs/demo-flow.md) | A timed screen-share script with likely questions and answers |
| [`scripts/README.md`](scripts/README.md) | Every script: purpose, invocation, exit codes |

---

## Licence

MIT — see [`LICENSE`](LICENSE). Synthetic demonstration code; use it however you like.
