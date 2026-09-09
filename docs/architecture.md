# Architecture

Ten projects, three layers, one direction of dependency. The layering exists to answer a single
question repeatedly: if this changes, how many files do I edit? Everything below is verifiable
from the paths quoted.

## Layering and dependency direction

```text
                      ┌──────────────────────────────────────────────────────────┐
   TEST SUITES        │  tests/Api.Tests      tests/Ui.Tests    tests/Mobile.Tests │
   feature files,     │  45 + 1 scenarios     18 scenarios      40 tests           │
   step definitions,  │                                                            │
   suite-only hooks   │  tests/Framework.Tests  (171 unit tests of the framework)   │
                      └───────┬──────────────────┬───────────────────┬────────────┘
                              │                  │                   │
                              ▼                  ▼                   │
                      ┌──────────────────────────────────────────┐    │
   APPLICATION MODEL  │  src/TradingDemo.AppModel                │    │
   the app, modelled  │  Pages/  ApiClients/  Contracts/         │    │
   exactly once       │  Setup/  Hooks/SharedHooks.cs            │    │
                      └───────┬───────────┬──────────┬───────────┘    │
                              │           │          │                │
                              ▼           ▼          ▼                ▼
                      ┌───────────────┐ ┌────────────┐ ┌──────────────────────────┐
   FRAMEWORK          │ QaFramework   │ │ QaFramework│ │ QaFramework.Mobile       │
   generic, reusable  │ .Api          │ │ .Web       │ │ Drivers/ Elements/       │
                      │ RestSharp,    │ │ Playwright,│ │ Simulation/ Screens/     │
                      │ schemas       │ │ components │ │                          │
                      └───────┬───────┘ └─────┬──────┘ └────────────┬─────────────┘
                              │               │                     │
                              └───────────────┼─────────────────────┘
                                              ▼
                                   ┌─────────────────────────┐
                                   │  QaFramework.Core       │
                                   │  Configuration/ Logging/│
                                   │  Synchronisation/       │
                                   │  TestData/ Database/    │
                                   └─────────────────────────┘

   APPLICATION UNDER TEST (referenced by nothing above; started as a process)
                                   ┌─────────────────────────┐
                                   │  src/TradingDemo.App    │
                                   │  minimal API + wwwroot  │
                                   └─────────────────────────┘
```

Two properties of that picture are worth stating explicitly.

`QaFramework.Core` references no test runner and no automation library — see the comment in
`src/QaFramework.Core/QaFramework.Core.csproj`. It can therefore be unit-tested on its own, which
is what `tests/Framework.Tests` does.

`src/TradingDemo.App` is not referenced by any test project. The suite starts it as an external
process (`src/TradingDemo.AppModel/Setup/ApplicationUnderTest.cs`) and talks to it only over HTTP
and the browser, exactly as a real client would. Nothing in the suite can reach into the
application's internals, which is what stops the tests from asserting against a world the
application cannot itself produce.

## The ten projects

| Project | Responsibility | Must not know about |
| --- | --- | --- |
| `src/QaFramework.Core` | Configuration precedence, timeout catalogue, polling waits, logging facade, reproducible data generation, cleanup tracking, read-only database access. | Playwright, Appium, HTTP, NUnit, Reqnroll. |
| `src/QaFramework.Api` | HTTP transport (`Requests/ApiClient.cs`), fluent request building, a non-throwing typed response, status/schema assertions. | Endpoint paths, DTOs, business rules. RestSharp appears nowhere else in the repository. |
| `src/QaFramework.Web` | Browser lifecycle (`Setup/WebTestContext.cs`), the component library, application-level synchronisation, failure evidence. | Any named screen or control of the trading app — a component knows "a button", never "the Place order button". |
| `src/QaFramework.Mobile` | `IMobileDriver` seam, Android/iOS Appium factories, capability assembly, `MobileLocator`, the simulated driver. | Appium outside `Drivers/` — `Elements/` and `Screens/` carry no WebDriver types. |
| `src/TradingDemo.AppModel` | The trading application modelled once: page objects, typed API clients, response contracts and schemas, run/scenario setup, shared Reqnroll hooks. | Which suite is using it. It contains no assertions about UI mechanics and no feature files. |
| `src/TradingDemo.App` | The application under test: minimal API, SQLite persistence, five static pages under `wwwroot/`. | The test suite. It provides `data-testid` attributes and two test hooks, and nothing else for the tests' benefit. |
| `tests/Framework.Tests` | 171 unit tests of the framework's own logic — configuration precedence, wait arithmetic and failure-message shape, seed reproducibility, the read-only guard, request/response behaviour. | The application under test; it starts nothing and opens no socket. |
| `tests/Api.Tests` | 46 API and data-integrity scenarios across five feature files, plus step definitions. | HTTP mechanics — those live in `QaFramework.Api`. |
| `tests/Ui.Tests` | 18 browser scenarios across three feature files, plus one Selenium comparison fixture. | Selectors. Every locator is declared in `TradingDemo.AppModel/Pages`. |
| `tests/Mobile.Tests` | 4 Reqnroll scenarios plus 36 unit tests of the mobile layer, run against the simulated driver. | Whether a device exists. The target is one setting, not a code branch. |

Counts verified by running each suite: 171 + 45 + 40 + 18 = 274 passing, with one further API
scenario (`@known-defect`) failing by design. See `docs/ci-strategy.md`.

### Where the repository deviates from its own rule

Two files put trading-domain knowledge inside a `QaFramework.*` library, which the layering says
they should not:

- `src/QaFramework.Core/Database/TradingQueries.cs` holds the suite's SQL, including
  `orders`/`instruments`/`accounts` column names.
- `src/QaFramework.Mobile/Screens/Trading/` holds four screen objects for the demo application.

The correct home for both is `TradingDemo.AppModel`. They sit where they do because
`tests/Mobile.Tests` references `QaFramework.Mobile` directly rather than going through the
application model, and moving the screens would mean moving that reference too. It is a real
inconsistency rather than a subtlety, and the cost is that neither library could be dropped into
a second product entirely unchanged.

## Three structural ideas

### (a) A component models a control archetype, not a screen

`src/QaFramework.Web/Components/WebComponent.cs` is the base class. A component owns the
behaviour of one *kind* of control: how to wait for it, how to operate it, how to assert on it,
and how to name itself when it fails. `Button.ClickAsync` does three things, and the third is the
reason the class exists:

```csharp
public Task ClickAsync() => PerformAsync("Click", async () =>
{
    await Locator.ClickAsync(new LocatorClickOptions { Timeout = Timeouts.ElementMs });
    await Context.WaitUntilIdleAsync();
});
```

The click and the wait-for-idle are inseparable. A test that clicks and immediately asserts is
racing the application; a test that clicks through this component cannot, because there is no way
to perform the click without the wait. That is the difference between a convention and a
guarantee.

The consequence is that pages carry no logic. `src/TradingDemo.AppModel/Pages/AccountPage.cs` is
30 lines, of which six declare components and four are the only method:

```csharp
public sealed class AccountPage(WebTestContext context) : ApplicationPage(context, "account.html")
{
    public TextElement Heading { get; } = new(context, "account-heading", "the Account heading");
    public TextElement Username { get; } = new(context, "account-username-text", "the signed-in username");
    public TextElement AccountNumber { get; } = new(context, "account-number-text", "the account number");
    public TextElement Currency { get; } = new(context, "account-currency-text", "the account currency");
    public TextElement Balance { get; } = new(context, "account-balance-text", "the account balance");
    public TextElement AccountType { get; } = new(context, "account-type-text", "the account type");

    public override async Task ShouldBeDisplayedAsync()
    {
        await Heading.ShouldHaveTextAsync("Account");
        await AccountNumber.ShouldBePopulatedAsync();
    }
}
```

Why behaviour-per-widget-type beats behaviour-per-screen: across the six page classes the
application declares 16 read-only text elements, 8 buttons and links, 5 dropdowns, 5 text
inputs, 2 grids and 2 alert regions. Behaviour written per screen means the waiting, the
read-back verification and the failure messages are written six times and diverge. Behaviour
written per archetype means `DataGrid` is one file
(`src/QaFramework.Web/Components/Grid/DataGrid.cs`, 139 lines) and both grids inherit every
improvement to it. The maintenance cost then scales with the number of *control
types*, which is roughly constant, rather than with the number of screens, which is not.

The self-verification in `TextInput.EnterAsync` — fill, then assert the field holds what was
typed — is the same argument applied to a different failure. A mask, a trim, a max-length or a
debounced re-render silently changes the value; without the read-back the test fails three steps
later on an assertion unrelated to the cause.

### (b) The `ActivePage` / `App` seam

`src/QaFramework.Web/Setup/WebTestContext.cs` exposes two properties that are the same object
today:

```csharp
/// <summary>The browser tab. Use for navigation, load state and evidence capture.</summary>
public IPage ActivePage => page ?? throw new InvalidOperationException(...);

/// <summary>The surface the application is rendered on. Every locator resolves from here.</summary>
public IPage App => ActivePage;
```

`ActivePage` is the tab: navigation, load state, screenshots, video. `App` is the surface the
application is rendered on, and it is what every locator resolves from —
`WebComponent.Locator => Context.App.Locator(Selector)`, `DataGrid.Rows`, `DataGrid.Row(key)`,
`AlertMessage.Text`, and `PageSynchronisation.WaitUntilIdleAsync`. Only
`NavigateToAsync`, the evidence collector and `WaitForResponseWhileAsync` use `ActivePage`, and
each of those genuinely means the tab.

If the application is later embedded — a portal shell, an SSO wrapper, a host application —
`App` becomes `ActivePage.FrameLocator("#shell")` and returns an `IFrameLocator`. One property
changes; every component and page follows, because none of them ever touched the page directly.
Without the seam, the same change means editing every locator in the suite.

The property costs three lines and buys an option. That is the whole argument, and it is the kind
of decision that is cheap before it is needed and impossible afterwards.

### (c) Framework / application model / specs

The mechanism is Reqnroll's `bindingAssemblies`. `tests/Api.Tests/reqnroll.json` and
`tests/Ui.Tests/reqnroll.json` both contain:

```json
"bindingAssemblies": [
  { "assembly": "TradingDemo.AppModel" }
]
```

That single line is what lets both suites inherit `SharedHooks` — run initialisation, container
registration, data cleanup — rather than copying it. Copied hooks drift: one suite gets a fix and
the others do not, and the resulting behavioural difference between suites is very hard to reason
about.

The API and UI suites also share the model itself. `NewOrderPage.PlaceOrderAsync` takes the same
`PlaceOrderRequest` record that `TradingApiClient.PlaceOrderAsync` takes, which is what makes
"the API and the UI enforce the same rules" a testable statement rather than an assumption. A UI
scenario needing an existing order calls `TradingApiClient.PlaceOrderOrFailAsync` — one HTTP call
instead of thirty seconds of browser interaction, and a defect in the order form then cannot fail
an order-history test.

Honest limitation: `tests/Mobile.Tests` does **not** participate. Its `reqnroll.json` lists no
binding assemblies, its `.csproj` references `QaFramework.Core` and `QaFramework.Mobile` only, and
it has its own lifecycle in `tests/Mobile.Tests/Support/MobileHooks.cs`. So the model is shared by
two suites, not three. Bringing mobile in would mean moving `QaFramework.Mobile/Screens/Trading`
into the application model, which is the same fix as the deviation noted above.

## Lifecycle and hook ordering

For the API and UI suites, ordering is explicit in the attributes and documented in both hook
classes:

| Phase | Where | What happens |
| --- | --- | --- |
| `BeforeTestRun(0)` | `SharedHooks.InitialiseTestRun` | Load configuration, log the data seed, start the application under test, create the one `ApiClient`. |
| `BeforeScenario(0)` | `SharedHooks.RegisterScenarioDependencies` | Register configuration, timeouts, evidence settings, the run-scoped `ApiClient`, a per-scenario `DataGenerator` and `ResourceTracker`, and a lazy `DatabaseVerifier` factory. |
| `BeforeScenario(10)` | `Ui.Tests/Support/BrowserHooks.StartBrowser` | Resolve the configuration registered above, construct and register `WebTestContext` and `EvidenceCollector`, launch the browser. |
| — | steps | The scenario runs. |
| `AfterScenario(100)` | `BrowserHooks.CaptureEvidenceAndStopBrowser` | If `ScenarioContext.TestError` is set, capture a full-page screenshot and the DOM; then close the browser in a `finally`. |
| `AfterScenario(200)` | `SharedHooks.CleanUpScenario` | Run the `ResourceTracker` undo stack, log the outcome. |
| `AfterTestRun(100)` | `SharedHooks.ShutDownTestRun` | Dispose the API client, kill the application process tree. |

**Why evidence must be captured before cleanup.** Cleanup exists to remove what the scenario
created, and in this repository the demo has no delete endpoint, so cleanup restores the seeded
database wholesale (`ScenarioSession.TrackOrderForCleanupAsync`). If that ran first, the
screenshot would show a page whose data no longer explains the failure, and a database
verification run afterwards would query state that had already been reset. The evidence is of the
moment of failure; the cleanup destroys that moment. Ordering 100 before 200 is the whole
mechanism, and it is why `SharedHooks` documents the number rather than relying on declaration
order.

There is a second ordering inside `CaptureEvidenceAndStopBrowser`: capture, then close, with the
close in a `finally`. A closed page cannot be screenshotted, and an exception during capture must
not leak a Chromium process per failing scenario.

`tests/Mobile.Tests` uses unordered `[BeforeScenario]`/`[AfterScenario]` because it inherits no
shared hooks, so there is nothing to order against. It applies the same capture-then-teardown rule
internally.

## Dependency injection

Reqnroll's BoDi container is scenario-scoped. Step definitions declare what they need as
constructor parameters:

```csharp
public sealed class OrderPlacementSteps(
    ScenarioSession session,
    NewOrderPage newOrderPage,
    OrderHistoryPage orderHistoryPage,
    DatabaseVerifier database)
```

`SharedHooks.RegisterScenarioDependencies` registers only the types the container cannot build
unaided. Page objects, API clients and `ScenarioSession` resolve automatically from those, so
adding a page requires no registration — which is what keeps container configuration from
becoming a maintenance burden of its own.

Two registrations are worth noting. `DatabaseVerifier` is registered as a *factory*, so a
scenario that never touches the database does not fail in an environment with no database path
configured. `DataGenerator` is seeded from the run seed XOR the scenario title's hash, so data is
unique per scenario (no collisions under parallel execution) while the whole run remains
reproducible from a single logged seed.

**Why no static mutable state.** The comment on `WebTestContext` states the concrete failure: a
`static` or `[ThreadStatic]` browser handle breaks under an async runner, because
`[ThreadStatic]` does not follow an `await` onto a continuation thread. The failure is
intermittent and reads as product flakiness.
`tests/Mobile.Tests/Support/MobileHooks.cs` records the sibling trap: an `AsyncLocal<T>` written
inside an `async` hook does not survive the hook returning, because the runtime copies the
execution context at the async boundary. Both are arguments for constructor injection being the
primary mechanism rather than a convenience.

**The one justified exception.** `src/TradingDemo.AppModel/Setup/TestRunContext.cs` is static.
Three conditions make write-once-then-read-only acceptable, and all three have to hold:

1. It is written exactly once, in `[BeforeTestRun]`, before any scenario starts.
2. Everything it holds is either immutable (`TestConfiguration` is a record) or genuinely
   run-scoped and thread-safe (one `ApiClient`, one application process).
3. No scenario ever mutates it.

`ApiClient` in particular *must* be run-scoped: it owns an HTTP connection pool, and one per
scenario exhausts ephemeral ports under load — a failure that works fine for ten tests and
appears at two hundred. Write-once-then-read-only is a different thing from shared mutable
state, and the moment a scenario needed to change any of it, it would have to move into the
per-scenario container.

## Parallelism

| Suite | Scope | Workers | Set in |
| --- | --- | --- | --- |
| `Api.Tests` | `ParallelScope.Fixtures` | 4 | `Support/ParallelisationSetup.cs` |
| `Ui.Tests` | `ParallelScope.Fixtures` | 2 | `Support/ParallelisationSetup.cs` |
| `Framework.Tests` | `ParallelScope.Fixtures` | 2 | `SuiteOverview.cs` |
| `Mobile.Tests` | none — sequential | 1 | no attribute declared |

Reqnroll generates one NUnit fixture per feature file, so `ParallelScope.Fixtures` runs feature
files concurrently and scenarios within a file sequentially. That is deliberate rather than
conservative: scenarios inside a feature share a `Background` and are written in a considered
order, and running them concurrently would make the `Background`'s meaning ambiguous — which is
where order-dependence creeps in later.

Four workers for the API suite, two for the UI suite. A browser costs far more than an HTTP
client: on a two-core agent, four Chromium instances contend for CPU, page loads slow, and
timeouts fire, producing failures that look like product flakiness but are entirely
self-inflicted. Two is a ceiling chosen for the smallest agent this is expected to run on, and it
is the first number to revisit if the suite grows.

Three properties make the concurrency safe, and all three are design choices rather than luck:

- **A fresh browser context per scenario.** `WebTestContext.StartBrowserAsync` creates a new
  `IBrowserContext`, so no cookies, session storage or local storage carry over. That is what
  allows scenarios to run in any order, and it is much cheaper than launching a whole browser.
- **Business-key row addressing.** `OrderHistoryPage.Orders` is keyed on `data-reference`, so a
  scenario asserts on *its own* order and is unaffected by orders another scenario creates
  concurrently.
- **No total-row-count assertions.** This is the assertion that forces a suite single-threaded.
  The one scenario that touches counts — "the order history reports how many orders are shown" —
  compares the displayed count against the number of rows actually rendered, which is invariant
  under other scenarios' activity.

Separately, each suite owns its own port and its own SQLite file: `api-suite.db` on
`http://localhost:5199`, `ui-suite.db` on `http://localhost:5198` (see the `Environment.*.json`
files). Both suites call `/test-support/reset`, so a shared database would mean each periodically
wiping the other's data — and a shared *process* meant the first test assembly to finish killed
the application underneath the others, which is the real problem this arrangement fixed. Every
remaining scenario then failed with a connection error that looked nothing like the cause.

In `Framework.Tests` the shared state is process-global and cannot be isolated: environment
variables, `CultureInfo.CurrentCulture` and `TestLog.Sink`. Fixtures touching any of those carry
`[NonParallelizable]` individually — see `Configuration/EnvironmentResolutionTests.cs` and
`Api/ApiRequestBuilderTests.cs`.
