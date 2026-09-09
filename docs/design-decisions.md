# Design decisions

A decision log. Each entry states what was decided, the context that forced the decision, what
else was considered, why this option won, and what was given up. The trade-offs are real: several
of these decisions would be made differently on a different product, and where that is the case
it is said so.

---

## 1. Playwright as the primary web driver, Selenium as one comparison fixture

**Decision.** Playwright 1.61 drives all 18 UI scenarios. Selenium 4.47 appears in exactly one
file, `tests/Ui.Tests/Selenium/SeleniumComparisonTests.cs`, which reimplements two of those
scenarios so the differences are concrete.

**Context.** Selenium is named in far more job specifications than Playwright. Claiming depth in
both would be a claim nobody can check. Implementing the same two scenarios twice produces
something arguable instead.

**Alternatives considered.**

- *Selenium only.* Widest applicability, and the tool most teams already run.
- *Playwright only.* Cleaner, but leaves an obvious question unanswered.
- *An abstraction over both.* Rejected outright: a lowest-common-denominator wrapper gets the
  worst of each and doubles the surface that can break.

**Rationale.** The comparison fixture documents four differences, and they are differences in
defaults rather than in capability:

| Concern | Selenium | Playwright |
| --- | --- | --- |
| Waiting | An explicit `WebDriverWait` per interaction; each one is a decision the author must remember. `SignIn_WithValidCredentials_ReachesTheAccountPage` needs three. | Locators auto-wait, so the framework only adds the *application-specific* wait (the busy indicator). |
| Element staleness | `IWebElement` is a handle to a DOM node and goes stale on re-render; robustness means re-finding or retrying on `StaleElementReferenceException`. | A locator is a lazy description re-resolved on use, which is why `WebComponent.Locator` can be a property. |
| Assertions | Retry has to be expressed as a wait *around* the assertion, and forgetting it produces a test that passes on a fast machine and fails in CI. | Web-first assertions retry to a timeout, so "the balance is displayed" is one call. |
| Setup | Selenium Manager (4.6+) resolves the driver binary, which closes the old operational complaint — but it still needs a real Chrome installed. | Ships and manages its own browser binaries. |

**Being fair to Selenium.** It is the W3C WebDriver standard, its grid is far more mature, it
supports more languages, and it reaches browsers Playwright does not ship. The second test in the
comparison fixture makes the point deliberately, with an elapsed-time assertion: an explicit wait
returns as soon as its condition holds, so a correctly written Selenium test is not inherently
slower than its Playwright equivalent. Selenium's
reputation for slowness comes from implicit waits and hardcoded sleeps, which are choices rather
than properties of the tool. And crucially — **a well-built Selenium framework wraps waits into
components exactly as this one does**, at which point most of the difference above disappears.

For a greenfield single-page application I would choose Playwright. For a large estate already on
Selenium Grid, or one needing legacy browsers, I would not propose a rewrite; I would put the
effort into wrapping waits into components, because that is where the flakiness lives either way.

**Trade-off accepted.** Playwright's grid story is weaker, and its browser download is blocked on
some corporate networks (see decision 13). The Selenium fixture is excluded from CI
(`--filter "TestCategory!=Selenium"`) because a minimal agent has no Chrome, so those two tests
are not continuously verified — they call `Assert.Ignore` rather than failing when Chrome is
absent, because a test that cannot run must say so rather than pretend to pass.

---

## 2. Reqnroll (BDD) for the functional suites, plain NUnit for framework tests

**Decision.** The API, UI and mobile suites are Gherkin feature files with step definitions.
`tests/Framework.Tests` and `SeleniumComparisonTests` are plain NUnit test methods.

**Context.** Trading rules are business rules — a 400 for a malformed order versus a 422 for one
the business refuses, a suspended account versus a wrong password. Those distinctions need to be
readable by someone who will never open a `.cs` file.

**Alternatives considered.**

- *Plain NUnit throughout,* with descriptive method names. Less indirection, faster to write, and
  every developer can read it.
- *BDD throughout,* including the framework's unit tests. Consistent, and wrong: a Gherkin
  scenario describing `Wait.ForValueAsync`'s deadline arithmetic has no business reader.

**Rationale.** BDD's value is a business-readable specification and a shared vocabulary — *not* a
different syntax for tests. If a team writes Gherkin that nobody outside the QA function ever
reads, it has bought the indirection and none of the benefit. Two concrete places where the
specification earns its keep here:

```gherkin
@regression @security
Scenario: A suspended trader is told their account is suspended
  When the suspended trader signs in
  Then the sign-in attempt is refused as forbidden
  And the failure message explains that the account is suspended
```

Nothing about HTTP appears in that. The status code is asserted in the step definition, where it
belongs, so the scenario survives a change of transport and can be confirmed by a product owner.
The tags are then load-bearing rather than decorative: `@smoke` and `@security` run on every pull
request, `@regression` nightly, `@known-defect` is quarantined.

**Trade-off accepted.** An indirection layer. Finding the code behind a step is a click in an IDE
and a search on the command line, and a badly phrased step can be reused in a scenario it does
not fit. There is also real cost in keeping step wording consistent across three suites.

**When I would not use it.** A framework's own unit tests. A performance or load suite. A
technical contract suite whose only readers are engineers. A team with no non-technical
stakeholder who reads the scenarios — in that case plain NUnit with good names is honest and
cheaper, and pretending otherwise produces Gherkin written for a reader who does not exist.

---

## 3. SQLite for the validation layer, not PostgreSQL or SQL Server

**Decision.** `database/schema/001_schema.sql` targets SQLite, accessed through
`Microsoft.Data.Sqlite` and Dapper in `src/QaFramework.Core/Database/DatabaseVerifier.cs`.

**Context.** The point of the database layer is to demonstrate that a QA engineer verifies
backend state rather than trusting an HTTP 201. That argument does not depend on the engine.

**Alternatives considered.**

- *PostgreSQL or SQL Server in Docker.* Realistic dialect, realistic connection handling,
  realistic migrations.
- *An in-memory fake.* Zero infrastructure, and worthless: it would not exercise real SQL.

**Rationale.** Zero infrastructure. `dotnet test` works immediately after a clone with no Docker,
no container registry, no service account and no port conflicts, on a locked-down corporate
laptop and on a public CI runner alike. Every reviewer can therefore run the thing rather than
read about it. The queries in `TradingQueries.cs` — `LEFT JOIN ... GROUP BY ... HAVING SUM(...) <>
...` for fill reconciliation, window-free aggregates for risk profiling — are ordinary SQL that
transfers directly.

**Trade-off accepted.** Dialect realism is genuinely lost. SQLite has dynamic typing, no `NUMERIC`
precision enforcement, no schemas, weaker `CHECK` semantics, no stored procedures and no window
functions in older builds. A suite written against SQLite would need its queries revalidated
against a real engine, and problems such as `NULL` collation order and decimal rounding would
surface only there. On a real product the verification layer would target the product's own
engine, and the value of that is high enough that I would accept the container.

---

## 4. A self-hosted application under test

**Decision.** `src/TradingDemo.AppModel/Setup/ApplicationUnderTest.cs` starts
`src/TradingDemo.App` as a process for the duration of a run, on a port the suite chooses, with a
database path the suite dictates.

**Context.** A test suite needs something to drive. The usual options are a public demo site or a
shared deployment.

**Alternatives considered.**

- *A public demo site.* No hosting cost, but the site changes without warning, has no database to
  verify against, cannot be reset, and rate-limits automated traffic. Every failure is ambiguous.
- *A shared deployment.* Realistic, and pays for it continuously: long timeouts to absorb other
  people's load, retries to absorb other people's deploys, test data accumulating for years, and
  a whole class of failure that is nobody's fault.

**Rationale.** Owning the application removes all of that. A known version starts against a known
seeded database, so any failure is a real finding. It also makes two things possible that are
otherwise very hard: reading the application's SQLite file to verify persistence, and calling
`/test-support/reset` to guarantee preconditions.

Startup is deliberately idempotent — if something is already healthy at the configured address,
`StartAsync` attaches to it and logs that it did. That is what makes the developer loop pleasant
(run the app once in a terminal, then run tests repeatedly) and it is the same code path that
works unchanged against a deployed environment.

**Trade-off accepted.** It is not always possible; some systems are far too large to start on an
agent. `ApplicationUnderTest.LocateAssembly` also has to find the built application by walking up
from the test assembly's directory, which is machinery the suite would not otherwise need, plus a
`QA_APP_ASSEMBLY` escape hatch for published layouts. And the demo is small enough that
self-hosting flatters the approach — a real system's start-up cost would need weighing against
the isolation it buys.

---

## 5. Duplicated DTOs: the suite declares its own copy of every contract

**Decision.** `src/TradingDemo.AppModel/Contracts/ApiContracts.cs` re-declares `LoginResponse`,
`AccountResponse`, `InstrumentResponse`, `OrderResponse`, `PagedResponse<T>` and
`ValidationProblem`, which already exist in `src/TradingDemo.App/Domain/Contracts.cs`.

**Context.** Both projects are in one solution. A project reference would remove the duplication
in one line.

**Alternatives considered.**

- *Reference the application's DTOs.* No duplication, always compiles, and undetectable
  regressions.
- *A shared contract package.* The right answer when the provider publishes one and consumers
  depend on the published version. It is not the right answer when the "package" is the
  provider's own internal model with a project reference around it.

**Rationale.** If the suite referenced the application's DTOs, renaming a field would update both
sides simultaneously. Every test would keep passing — while every real client of the API broke.
The suite must assert against the contract it *expects*, not against whatever the application
currently emits. This is the same reasoning a consumer-driven contract test uses when it defines
its own expectations rather than importing the provider's model.

The duplication is reinforced by JSON Schemas in `Contracts/ResponseSchemas.cs`, which catch what
typed assertions cannot: `order.Quantity == 1.5m` still passes if the API starts returning
`"1.5"` as a string, because the deserialiser coerces it — while every strongly typed client in
the estate breaks.

**Trade-off accepted.** A legitimate contract change requires editing two files. That cost *is*
the mechanism: it forces the change to be noticed and consciously accepted. It also means the two
files can drift silently in the other direction — the suite can be updated to match a change
nobody reviewed — so the discipline depends on the reviewer, not the compiler.

A related sub-decision: `ResponseSchemas` uses `"additionalProperties": false`, so a *new* field
fails the test. That is right for a demonstration of contract discipline, and wrong on a
fast-moving API where it produces failures for additive, backwards-compatible changes. On a real
product I would keep it false for responses consumed by a mobile client that cannot be updated
quickly, and true elsewhere.

---

## 6. Read-only database access, with a SELECT/WITH-only guard

**Decision.** `DatabaseVerifier` opens SQLite with `Mode = SqliteOpenMode.ReadOnly`, and a
`Guard` method rejects any statement that does not begin with `SELECT` or `WITH` (after skipping
leading `--` comments). Test data is created through the application's own API.

**Alternatives considered.**

- *Read/write access.* Convenient. A scenario needing an order in a peculiar state could insert
  one directly.
- *Read-only connection with no guard.* The driver would reject a write anyway.

**Rationale.** A suite that can write to the database will eventually be used to set up state the
application itself cannot produce, and from that point the tests verify a world that cannot
exist. Setup goes through the API; the database is for observation only. The guard is belt and
braces — the connection is already read-only — but it fails *earlier* and with a message that
explains the architectural rule rather than a driver-level "attempt to write a readonly
database". The intent is to stop the pattern spreading, not merely to stop the write.

Every query is parameterised. Test code is a legitimate source of SQL injection: generated data
routinely contains apostrophes, and a concatenated query fails confusingly on a name like
O'Brien. Beyond correctness, a QA engineer who writes concatenated SQL in tests is unlikely to
spot it in a review of production code.

**Trade-off accepted.** Some states become expensive or impossible to reach. A partially filled
order, a corrupted row, a mid-migration state — none can be constructed, so the invariant queries
can only find defects the application produced by itself. The planted defect in the seed data
(`database/seed/002_seed.sql`, order 6) exists precisely because the suite cannot create one.
`WITH` is allowed because a documented CTE is a read, but that does widen the guard's surface
slightly compared with `SELECT` alone.

---

## 7. Test hooks shipped in the product: `/health` and `/test-support/reset`

**Decision.** `src/TradingDemo.App/Program.cs` maps two endpoints that exist only for the tests,
behind a configuration flag:

```csharp
if (app.Configuration.GetValue("Demo:EnableTestSupport", true))
{
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapPost("/test-support/reset", (DemoDatabase db) => { db.Reset(); return Results.Ok(...); });
}
```

**Context.** Scenarios need a way to guarantee their own preconditions and the harness needs a
liveness probe (`Wait.UntilAsync(IsHealthyAsync, ...)` in `ApplicationUnderTest.StartAsync`).

**Alternatives considered.**

- *No hook; drive setup through the UI or API only.* Purest, and slow: restoring seeded state
  through the product's own surface is many calls and is not always possible at all.
- *Direct database writes for setup.* Rejected — see decision 6.
- *A separate admin service.* More realistic for a large system, and disproportionate here.

**Rationale.** A deliberate, documented test hook is legitimate engineering. It is what lets a
scenario guarantee its preconditions in milliseconds instead of tolerating order-dependent tests.

**Trade-off accepted, and the real cost.** An endpoint that wipes the database is a catastrophic
liability if it reaches production. The flag here defaults to `true`, which is the wrong default
for anything real — it is `true` because the demo's whole purpose is to be driven by tests. In a
production system I would gate it three ways rather than one: compiled out of release builds with
`#if`, refused unless the environment is explicitly non-production, and — if it had to exist in a
deployed environment — bound to a separate internal listener requiring an authenticated
service principal, with every invocation audited. `/health` is different in kind: a liveness
probe is a normal production endpoint and needs no gate beyond not leaking internals.

A second cost is subtler: because `reset` restores the whole seed, cleanup in
`ScenarioSession.TrackOrderForCleanupAsync` is a blunt instrument that removes every scenario's
data, not just its own. That is only safe because each suite owns its own application instance.
Against a shared environment the correct implementation is a targeted delete — and the
`ResourceTracker` interface is unchanged either way, which is the point of expressing cleanup as
an arbitrary undo action rather than a list of ids.

---

## 8. Evidence on failure, not on every step

**Decision.** `src/QaFramework.Web/Setup/EvidenceCollector.cs` captures a full-page screenshot and
the DOM only when `ScenarioContext.TestError` is set. Video is off by default
(`Evidence.RecordVideo: false` in every `runsettings.json`).

**Alternatives considered.**

- *A screenshot per step.* Produces a filmstrip. Looks impressive.
- *Always record video.* The most complete artefact available.

**Rationale.** The evidence that actually gets read is the state at the moment of failure. A
per-step filmstrip adds measurable time to every run, green or red, and fills a build agent's
disk with images nobody opens. Page source is captured alongside the screenshot because it is
cheap and often more diagnostic: it answers "was the element absent, or present but invisible, or
present with different text?", which an image cannot. Filenames are
`{Sanitised_Scenario_Title}_{yyyyMMdd-HHmmss}.png` rather than `{Guid}.png`, because twenty
GUID-named screenshots in a CI artefact are unusable.

Nothing in the collector may throw: every capture is wrapped and downgraded to a warning. An
evidence collector that fails during teardown replaces the real failure message with its own,
which is the most frustrating thing a framework can do to someone triaging a red build.

**Trade-off accepted.** When a failure depends on *how* the application got into a state — an
animation, a transient toast, a race — a single screenshot is not enough, and the run has to be
repeated with `RecordVideo` switched on. That is a deliberate trade of one slow re-run against
continuous cost on every run.

---

## 9. A simulated mobile driver

**Decision.** `src/QaFramework.Mobile/Simulation/SimulatedMobileDriver.cs` implements
`IMobileDriver` over an in-memory element tree loaded from
`Simulation/Fixtures/trading-app.json`. It is the committed default
(`Mobile.Target: "Simulated"`) and the only target CI uses.

**Context.** A public CI runner has no Android SDK, no Xcode, no emulator and no Appium server.
Most "Appium demonstration" repositories therefore contain code that has never been executed.

**Alternatives considered.**

- *A real Appium suite that cannot run in CI.* More impressive-looking, less honest.
- *No mobile layer at all.* Honest, and omits a requirement.
- *A cloud device grid.* The right answer for a real project; needs an account and credentials
  that do not belong in a portfolio repository. The `CloudGrid` target exists and reads its URL
  and credentials from environment variables only — no provider is named anywhere.

**Rationale.** A passing simulated run proves the framework's wiring works end to end:
configuration binds, capability files parse, `MobileDriverFactoryResolver` picks the right
factory, locators resolve to the correct platform selector, screen objects issue the right calls
in the right order, waits poll and time out correctly, Reqnroll bindings and hooks fire, and
failure evidence is written to the configured directory. Those are real defects when they break,
and they break often. Running the same fixture as iOS proves every locator carries a usable iOS
selector — a gap otherwise discovered on the day someone is asked to add iOS.

**Trade-off accepted, stated as plainly as the code states it.** A green mobile run says nothing
whatsoever about any application. It does not prove a device renders the screen, that the
accessibility identifiers exist in a real build, that a gesture works, or that a network call
succeeds. The fixture answers exactly what it was told to answer. Two details keep that boundary
visible: `TakeScreenshotAsync` returns a blank 1×1 PNG rather than a rendered mock-up, and
`GetPageSourceAsync` prefixes its output with
`<!-- Simulated page source. No device was involved in producing this. -->`. Gestures live on a
separate `ISupportsGestures` interface so that a scenario genuinely depending on scrolling cannot
pass in CI against a silent no-op.

---

## 10. `InvariantGlobalization` disabled, with invariant formatting applied explicitly

**Decision.** `Directory.Build.props` sets `<InvariantGlobalization>false</InvariantGlobalization>`
and every place that formats or parses a number specifies `CultureInfo.InvariantCulture`.

**Context.** `InvariantGlobalization=true` is a tempting default for a self-contained demo: it
removes an ICU dependency and makes behaviour identical on every machine.

**Rationale.** It cannot be used here. Reqnroll resolves the feature-file language through
`CultureInfo.GetCultureInfo("en-us")`, which throws in invariant mode and fails the entire suite
in `OneTimeSetUp`. So culture-sensitivity is handled where it actually bites instead:

- `ApiRequestBuilder.Stringify` formats `decimal` and `double` with `InvariantCulture`, and
  lower-cases booleans because .NET renders them as `"True"`.
- `NewOrderPage.FormatNumber` does the same before typing into a field. On an agent with a
  European locale the default `ToString()` renders 1.5 as `"1,5"`, the application parses it as
  NaN, and the test fails with a validation error that makes no sense to anyone reading it.
- `tests/Mobile.Tests/reqnroll.json` pins `bindingCulture: en-GB`, so a decimal quantity in a
  feature file is parsed the same way on every agent.

These are covered directly: `Framework.Tests/Api/ApiRequestBuilderTests.cs` has a
`[NonParallelizable]` test that swaps `CultureInfo.CurrentCulture` and restores it.

**Trade-off accepted.** Invariant formatting has to be remembered at each new call site, and the
compiler will not remind anyone. The mitigation is that formatting is confined to two helpers, so
there are few sites to get wrong — but it is discipline rather than a guarantee.

---

## 11. Central Package Management

**Decision.** All versions live in `Directory.Packages.props` with
`ManagePackageVersionsCentrally` and `CentralPackageTransitivePinningEnabled` set. Individual
`.csproj` files list `<PackageReference Include="..." />` with no `Version`.

**Alternatives considered.** Per-project versions (the default), or hand-rolled MSBuild
properties in `Directory.Build.props` — a home-made version of a supported feature.

**Rationale.** With ten projects and shared framework libraries, a version cannot be allowed to
drift: two versions of `Microsoft.Playwright` or `RestSharp` in one process produces load-time or
behavioural failures that are hard to attribute. One file makes an upgrade a one-line diff a
reviewer can read, and the grouping by label ("Test runner", "Web automation", "Schema
validation") documents what each dependency is for. Transitive pinning closes the remaining hole,
where a transitive dependency pulls in a different version of something already pinned.

**Trade-off accepted.** A project genuinely needing a different version has to use
`VersionOverride`, an explicit exception rather than a local decision. It is also less familiar,
so a newcomer adding a package sometimes puts the version in the wrong file.

---

## 12. A classic `.sln` rather than `.slnx`

**Decision.** `Senior-QA-Automation-Demo.sln` is the traditional Visual Studio solution format.

**Alternatives considered.** The XML-based `.slnx` format, which is dramatically shorter — the
current `.sln` spends roughly 130 of its lines on GUIDs and per-configuration mappings that say
almost nothing.

**Rationale.** Tool support. The classic format is understood by every version of the .NET SDK,
every IDE, every CI action and every third-party analyser in use today. `.slnx` depends on recent
SDK and IDE versions, and a solution file that does not open is a bad first impression for a
reviewer on an older toolchain. For a repository whose purpose is to be opened and run by a
stranger, compatibility outranks elegance.

**Trade-off accepted.** The file is verbose, merge conflicts in it are annoying, and the six
platform configurations (`Debug|Any CPU`, `Debug|x64`, `Debug|x86` and the Release equivalents)
are noise nothing here needs. This is the decision I would revisit soonest.

---

## 13. Corporate TLS interception and the browser `Channel` setting

**Decision.** `BrowserSettings.Channel` (default empty) can be set to `"chrome"` or `"msedge"` to
drive a browser already installed on the machine instead of Playwright's bundled build.

**Context.** On a network that intercepts TLS, Playwright's browser download fails certificate
validation. During development the install step was run with
`NODE_TLS_REJECT_UNAUTHORIZED=0` — acceptable for a one-off install on a trusted internal
network, and not acceptable as a standing setting, because it disables verification for that
process entirely.

**Rationale.** The `Channel` setting is the durable answer: it needs no download at all. There is
a second, unrelated reason to want it — reproducing a defect against the exact Chrome build a
customer has.

**Trade-off accepted.** Driving an installed browser loses reproducibility: the browser then
updates underneath the suite, so a scenario can start failing without any change to the code or
the application. That is why empty (bundled, pinned) is the committed default. One implementation
detail is worth noting because it cost time: the setting must be passed as `null`, not `""`, since
Playwright treats an empty channel as an *invalid* channel and fails to launch with a message
that does not mention the channel at all.

---

## 14. Unit-testing the test framework

**Decision.** `tests/Framework.Tests` contains 171 unit tests of `QaFramework.Core` and
`QaFramework.Api`, and `tests/Mobile.Tests/UnitTests` covers the mobile layer.
`QaFramework.Api.csproj` grants `InternalsVisibleTo("Framework.Tests")`.

**Rationale.** A test framework is production code for the QA team, with an unusually nasty
failure mode. A red test is investigated within the hour; a green test that verified nothing can
survive for a year. The selection criterion is "what breaks silently if it is wrong": the
configuration precedence chain, `Wait`'s deadline arithmetic *and the shape of its failure
messages*, seed reproducibility, cleanup ordering, the read-only guard, invariant number
formatting, and non-throwing lazy deserialisation.

The value is not theoretical. `SchemaAssertions.ShouldMatchSchema` requires
`OutputFormat.Hierarchical`; with the library's default `Flag` output it reported "the schema
itself is malformed" for a valid schema catching a real regression, and named no field. That was
found by a framework unit test, and could not have been found by any test of the application —
the assertion still went red, just unhelpfully.

**Trade-off accepted.** `InternalsVisibleTo` widens the API surface to one assembly, so
`ApiRequestBuilder.Build` and the `ApiResponse<T>` constructor are testable without being public.
The alternatives were to make them public permanently — undoing the layering they exist to
enforce — or to drive every case through a real HTTP listener, which would test the transport
stack again and add a socket to a unit test suite. The compromise is visible in the `.csproj` with
its reasoning attached.

---

## 15. Reqnroll code-behind generated into `obj/`

**Decision.** `ReqnrollUseIntermediateOutputPathForCodeBehind` and
`ReqnrollDeleteObsoleteCodeBehindFilesOnClean` are set for every test suite, so generated
`.feature.cs` files land in the git-ignored `obj/` rather than beside the feature files.
Committed code-behind goes stale, and somebody then spends an afternoon debugging a test that no
longer matches its feature file. The costs are minor: stepping into generated code is less
convenient, and a stale `obj/` occasionally needs a clean.

**The interesting part is where the setting had to go**, because the obvious location does not
work. It was first placed in `Directory.Build.props` and had no effect at all. MSBuild imports in
this order:

```text
Sdk.props -> Directory.Build.props -> package *.props -> <project body>
          -> Sdk.targets -> Directory.Build.targets -> package *.targets
```

Reqnroll's own `.props` defaults the property to `false`, and it is imported *after*
`Directory.Build.props` — so the central value was silently overwritten. The diagnostic clue was
that `dotnet msbuild -getProperty:ReqnrollUseIntermediateOutputPathForCodeBehind` reported
`false` rather than empty: an empty value would have meant the condition never matched, whereas
an explicit `false` meant something was actively setting it. Setting it in each `.csproj` also
works, because the project body is imported after the package props — but that is four places
instead of one.

It now lives in `Directory.Build.targets`, which is imported after both the package props and the
project body, with the reasoning recorded in a comment there. Verified: all ten generated files
are in `obj/` and none in `Features/`. `.gitignore` also carries a `**/Features/*.feature.cs`
pattern as belt and braces, for anyone building with an IDE that regenerates them in place.
