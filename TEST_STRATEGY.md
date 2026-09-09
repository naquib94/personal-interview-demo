# Test strategy

This document explains how the tests in this repository were chosen, why some things are
covered deeply, why some things are covered once, and why a few things are not automated at
all. Every example is taken from the code in this repository so that any claim here can be
checked against a file.

The application under test is a deliberately small synthetic retail trading platform
(`src/TradingDemo.App`): sign in, read an account, read instrument prices, place an order,
read order history. It is backed by SQLite with a committed schema
(`database/schema/001_schema.sql`) and a deterministic seed (`database/seed/002_seed.sql`).
The domain is invented for this project.

## 1. What is in the suite

| Suite | Project | Tests run by the gate | Notes |
| --- | --- | --- | --- |
| Framework unit tests | `tests/Framework.Tests` | 171 | Tests of the test framework itself, not of the application |
| API scenarios | `tests/Api.Tests` | 45 | 46 discoverable; one is excluded by the `@known-defect` tag |
| Mobile tests | `tests/Mobile.Tests` | 40 | 4 Reqnroll scenarios plus 36 unit tests of the mobile layer |
| UI scenarios | `tests/Ui.Tests` | 18 | 20 discoverable; 2 Selenium comparison tests are excluded by category |
| **Total** | | **274** | All passing |

The counts above are the discoverable test counts from `dotnet test --list-tests`, minus the
two exclusions that `.github/workflows/ci.yml` applies by filter. Stack: .NET 10, NUnit 4,
Reqnroll for BDD, Playwright as the primary web driver with one Selenium fixture kept for
comparison, an Appium-oriented mobile layer driven in CI by a simulated driver, RestSharp,
Dapper over SQLite, JsonSchema.Net, Bogus and AwesomeAssertions.

Two of those numbers need immediate qualification, because an unqualified test count is a
number designed to impress rather than inform. The 40 mobile tests run against a simulated
driver - no device, no emulator, no Appium server - so a green mobile run proves the
framework's locator translation, capability assembly and screen model are wired correctly, not
that any real application signs anybody in. And the 171 framework tests are not application
coverage at all; they exist because a framework defect produces a suite that reports the wrong
answer confidently, and `tests/Framework.Tests/SuiteOverview.cs` sets out the selection
criterion, which is "what breaks silently if it is wrong" rather than "what is easy to reach".

---

## 2. Requirement analysis

Before writing a single scenario, the questions worth answering are: what must the system do,
which of those things move money or expose data, where does it talk to something else, and
what would a defect actually cost. Order placement is the right worked example here because
it is the only operation in the demo that changes a balance.

### 2.1 Reading the requirement out of the implementation

`src/TradingDemo.App/Endpoints/TradingEndpoints.cs` implements `POST /api/orders`. Read as a
specification rather than as code, it contains eleven distinct rules, and they fall into two
groups that the API answers with different status codes:

Field-level validation, answered with 400 and collected rather than short-circuited
(lines 100-116):

- `symbol` is required.
- `side` must be `Buy` or `Sell`.
- `orderType` must be `Market` or `Limit`.
- `quantity` must be present and greater than zero.
- A `Limit` order requires a positive `limitPrice`.
- A `Market` order must not supply a `limitPrice`.
- An unknown symbol is a 400, not a 422 - the caller sent a value that does not exist.

Business rules, answered with 422 (lines 135-153):

- A non-tradable instrument is refused.
- Quantity must sit within the instrument's `min_quantity` and `max_quantity`.
- A buy whose notional exceeds the account balance is refused for insufficient funds.
- A user with no account gets a 404 before any of the above is evaluated.

The 400/422 distinction is a requirement, not an implementation detail. It is the difference
between "you sent me nonsense" and "I understood you and the answer is no". A client has to
handle those differently: the first is a bug in the caller, the second is a message for the
user. That is why `tests/Api.Tests/Features/OrderPlacement.feature` separates its scenarios
into a "Field validation - answered with 400" block and a "Business rules - answered with
422" block, and why the step definitions assert the specific code rather than "not 2xx".

### 2.2 Business-critical workflow, integration points and dependencies

The workflow a customer actually performs is: sign in, read the account, read prices, place an
order, see it in history. Only one step in that chain mutates state. That single fact drives
most of the prioritisation in section 4 - order placement earns the deepest coverage because
it moves money and a defect is immediately visible to the customer who lost it.

The integration points that workflow crosses:

- HTTP boundary between the browser or mobile client and the API. Contract-tested by JSON
  Schema (`src/TradingDemo.AppModel/Contracts/ResponseSchemas.cs`, exercised by the
  `@contract` scenarios).
- API to database. Every write is checked at the database, not only in the response.
- Authentication. A bearer token issued by `POST /api/auth/login` and validated by
  `TokenService`. Every trading endpoint except the instrument endpoints requires it.
- Static assets. The web pages are plain HTML and JavaScript served from `wwwroot`, which
  turned out to be a genuine integration risk - see 2.5.

Worth raising as a finding rather than encoding as a test: `GET /api/instruments` and
`GET /api/instruments/{symbol}` take no token at all (`TradingEndpoints.cs` lines 44-80). For
public reference pricing that is defensible, but it is the kind of asymmetry that belongs in
requirement review.

### 2.3 Side effects

A requirement is not satisfied just because the response is right. `POST /api/auth/login`
writes an `audit_events` row on success, on failure and on a blocked suspended account
(`AuthEndpoints.cs` lines 38, 44, 50). A test that asserts only the 200 would never notice
audit logging silently breaking, so the smoke scenario `An active trader signs in
successfully` ends with `And the sign-in is recorded in the audit trail`, verified with query
A2 in `database/validation/qa-validation-queries.sql`, which counts audit events for that
user, event type and time window.

### 2.4 Risks, and one that materialised

The highest risk in a small suite like this is not that the application is wrong, but that a
test is wrong in a way nobody notices. Three risks identified up front:

- The API can say a thing happened without it happening. Handled by verifying writes against
  the database. Query A1 joins orders, instruments, accounts and users so that a broken
  foreign key or a mis-resolved symbol is caught rather than assumed; query A3 is the negative
  counterpart, asserting that after a 422 zero order rows exist.
- A rejected order might still be persisted. `OrderPlacement.feature` asserts `And no order
  is persisted` on the non-tradable and insufficient-funds scenarios.
- A failure might be blamed on the wrong layer. This one materialised. The Web SDK globs
  `wwwroot` as content but only stages it for `publish`, not for `build`; the harness launches
  the application as `dotnet bin/.../TradingDemo.App.dll`, so the content root became the
  output directory, `wwwroot` was absent, and every page returned 404 with an empty body.
  Playwright reported "element not found" on a blank page, which reads like a locator problem.
  The fix is one line, documented where it was made in
  `src/TradingDemo.App/TradingDemo.App.csproj` lines 23-34. The defect took minutes to fix and
  much longer to understand, which is exactly the cost section 10 argues should be measured.

---

## 3. Test design

Each technique below is illustrated with a scenario that is actually in the repository.

### 3.1 Positive paths

One per meaningful outcome, no more. `POST /api/orders` has two distinct successful outcomes
because a market order fills immediately and a limit order rests as pending
(`TradingEndpoints.cs` lines 157-158), so there are two positive scenarios and not one:

```gherkin
  @smoke
  Scenario: A valid market order is accepted and filled immediately
    When the trader places a market order to buy 1 of "EURUSD"
    Then the order is accepted
    And the order status is "Filled"
    And the order is persisted against the trader's own account
    And the order appears in the trader's order history
```

The third step is the important one. The API saying "Filled" only proves the API said so.

### 3.2 Negative paths

Negative testing is where equivalence classes earn their keep. `Authentication.feature` uses a
Scenario Outline for missing credentials, and the choice of examples is deliberate:

```gherkin
    Examples:
      | username    | password     | field    | case                        |
      |             | Demo!Pass123 | username | username sent as empty      |
      | trader.demo |              | password | password sent as empty      |
      | null        | null         | username | neither field sent at all   |
```

The literal `null` is translated to a real null in the step definition. Gherkin has no null,
and treating an empty cell as "absent" would stop the suite distinguishing "sent as an empty
string" from "not sent at all". Those are different HTTP requests and an API is entitled to
answer them differently, so the distinction is worth the small amount of translation code.

Enumeration safety is treated as a rule of its own, not as an accident of the implementation.
`AuthEndpoints.cs` returns one indistinguishable 401 for "no such user" and "wrong password",
and both scenarios assert `And the failure message does not reveal whether the username
exists`. A suspended account is a separate rule again - the credentials are correct but the
account may not trade - and it gets its own 403 scenario, because conflating the two would
hide a genuine defect: a suspended trader being allowed to place orders.

### 3.3 Boundary and edge cases

The boundary rule in the application is
`quantity < instrument.MinQuantity || quantity > instrument.MaxQuantity`
(`TradingEndpoints.cs` line 141). BTCUSD is seeded with `min_quantity` 0.01 and
`max_quantity` 2.00, so the rejected side of the boundary is:

```gherkin
  @regression
  Scenario Outline: Quantity must respect the instrument's tradable range
    When the trader places a market order to buy <quantity> of "<symbol>"
    Then the order is refused by the business rules
    And the rejection reason mentions the permitted quantity range

    Examples:
      | symbol | quantity | boundary                          |
      | BTCUSD | 2.01     | just above the maximum of 2       |
      | BTCUSD | 0.009    | just below the minimum of 0.01    |
```

That pair on its own is not enough, and this is the point most often missed. A rule written
`quantity > max` and a rule written `quantity >= max` both reject 2.01. Testing only the
failing side of a boundary cannot tell them apart, so an off-by-one that rejects every order
at exactly the maximum quantity would pass this outline cleanly while breaking every customer
who trades the maximum size. The accepted side is therefore a separate scenario:

```gherkin
  @regression
  Scenario Outline: The exact boundary quantities are accepted
    When the trader places a market order to buy <quantity> of "EURUSD"
    Then the order is accepted

    Examples:
      | quantity | boundary                              |
      | 0.01     | the exact minimum permitted quantity  |
      | 50       | the exact maximum permitted quantity  |
```

Note the change of instrument, which is the more interesting half of the story. The first
version of this scenario used BTCUSD for symmetry with the rejection outline. BTCUSD's
maximum quantity is 2 and its ask is 61,285, so two units cost roughly 122,570 against
`trader.demo`'s balance of 25,000. The insufficient-funds rule at line 150 fires before the
boundary check has anything to say, so the order was refused for a reason unrelated to the
boundary and the test could never verify the thing it claimed to verify. EURUSD's maximum of
50 units costs about 54 at an ask of 1.08435, well inside the balance, so the funds rule
cannot interfere and the scenario isolates exactly one rule. Choosing data that isolates the
rule under test is the difference between a test that proves something and one that merely
goes red. The reasoning is recorded in the feature file itself so the next person does not
"tidy" it back.

Other boundaries covered: pagination (`Order history is paginated` asserts the page size, the
row count and that the reported total exceeds the page), and the page-size clamp in
`TradingEndpoints.cs` line 215 - which is currently untested, and is named as a gap in
section 12.

### 3.4 Error handling

Error handling is tested from the consumer's point of view, which means the shape of the error
matters as much as the fact of it:

```gherkin
  @regression
  Scenario: Every invalid field is reported at once
    When the trader submits an order with no symbol, side "Hold" and quantity 0
    Then the order is rejected as a bad request
    And the validation errors mention the fields "symbol, side, quantity"
```

A test that asserted only "an error was returned" would still pass if the API regressed to
reporting one field at a time, which is a real usability regression for anyone building a
client. The UI has the matching requirement from the other side. In
`tests/Ui.Tests/Features/OrderPlacement.feature`:

```gherkin
  @regression
  Scenario: A rejected order shows the specific reason, not just a generic failure
    When the trader places a market order to buy 20 of "XAUUSD" through the browser
    Then an error is shown on the new order page
    And the error message mentions "rejected"
    And a rejection detail mentions "Insufficient funds"
```

The platform's error envelope carries a general message ("The order was rejected.") and a
per-field reason ("Insufficient funds: the order requires ..."). The user needs the second to
know what to change, so the page must render the details and not just the summary. The first
version of this scenario collapsed both into one assertion and failed, correctly.

### 3.5 Security

Security scenarios are tagged `@security` and are intended to run on every pull request rather
than nightly, because an authorisation regression is not something to discover the following
morning.

- Unauthenticated access is refused, asserted separately for account reads and for order
  placement.
- A tampered token is refused (`A tampered token cannot place an order`). Its implementation
  in `tests/Api.Tests/StepDefinitions/OrderPlacementSteps.cs` lines 43-79 alters a character
  in the middle of the signature rather than the last one, for the reason described in
  `AI_ASSISTED_QA.md`.
- One trader cannot read another's order. This is the authorisation test that matters most,
  because an order reference is guessable in sequence:

```gherkin
  @smoke @security
  Scenario: A trader cannot read another trader's order
    Given the second trader has placed a market order to buy 1 of "EURUSD"
    When the active trader requests that order by reference
    Then the request is refused as not found
```

It asserts a 404 and not a 403, deliberately. A 403 confirms the reference exists, which is
itself an information leak; the application makes the same choice, with the reasoning in a
comment at `TradingEndpoints.cs` lines 264-268. The scenario exists partly to stop somebody
"improving" that into a 403 later.

Client-side route protection is covered where it lives, in the browser suite, because it is
invisible to the API: `Protected pages redirect an unauthenticated visitor to sign in` in
`tests/Ui.Tests/Features/Login.feature` navigates directly to `account.html`, `order-new.html`
and `orders.html` and expects the sign-in page each time.

### 3.6 Data validation

Two layers, answering different questions.

Contract validation. The `@contract` scenarios validate responses against JSON Schema. Field
assertions verify the values a test happens to care about; they do not notice that `quantity`
changed from a number to a string, that a required field disappeared, or that a null crept
into a field a mobile client dereferences. Those are the regressions that break consumers
rather than tests. Schema validation is explicitly not a substitute for value assertions - an
order with the wrong quantity passes schema validation cleanly - and
`src/QaFramework.Api/Assertions/SchemaAssertions.cs` says so in its own documentation.

Stored-data invariants. `tests/Api.Tests/Features/DataIntegrity.feature` asserts nothing about
the API. It queries the database and checks that the stored data is self-consistent, using the
Group B queries in `database/validation/qa-validation-queries.sql`: no orphaned references, no
duplicate order references, the limit-price rule holds for every stored order, every quantity
sits inside its instrument's range, and every filled order's fills sum to its quantity. Three
properties make these worth having. API tests can only see what the API chooses to expose, so
a status transition that updates `orders` but not `order_fills` is invisible to every
endpoint. Each query covers all historical data rather than only the rows this run created, so
it catches damage done by a migration, a back-office tool or an import path that no functional
test exercises. And a returned row is by construction a defect, so there are no expected
values to maintain.

This one finds something. Order `ORD-20240403-0006` in the committed seed is `Filled` with
three units ordered and zero rows in `order_fills` (`database/seed/002_seed.sql` line 73).
Query B1's `HAVING COALESCE(SUM(f.fill_quantity), 0) <> o.quantity` returns it by name. The
defect is deliberately planted; the point is that the query set catches the class of problem it
was written for, and that `scripts/verify_backend_data.py` exits 1 out of the box rather than
pretending otherwise.

---

## 4. Risk-based prioritisation

Coverage depth is a budget, and it should be spent where a defect costs the most, not spread
evenly. Five dimensions, scored 1 (low) to 5 (high). Technical complexity is scored as a risk
input, not as an excuse: complex code fails in more ways.

| Feature | Business impact | Likelihood of failure | Frequency of use | Customer impact | Technical complexity | Score | Coverage depth |
| --- | :-: | :-: | :-: | :-: | :-: | :-: | --- |
| Order placement | 5 | 4 | 4 | 5 | 5 | 23 | Deepest: positive, negative, both sides of every boundary, business rules, security, contract, database verification |
| Authentication | 5 | 3 | 5 | 5 | 3 | 21 | Deep at API level, exhaustive on failure modes; one browser journey |
| Order history | 3 | 3 | 5 | 4 | 3 | 18 | Filtering, pagination, cross-account authorisation, contract |
| Account retrieval | 4 | 2 | 5 | 4 | 1 | 16 | Happy path, no-account 404, unauthenticated refusal, contract |
| Market data | 3 | 2 | 5 | 4 | 1 | 15 | Listing, asset-class filter, unknown symbol, contract |
| Stored-data integrity | 5 | 3 | n/a | 3 | 4 | 15 | Five SQL invariants over all data, plus a risk-profiling query |

The reasoning behind the individual scores matters more than the arithmetic. Order placement
scores 5 for business and customer impact because it is the only operation that changes a
balance, and a customer whose order was rejected or filled wrongly notices within seconds; it
scores 5 for complexity because it is the only endpoint with two validation layers, a
transaction, a conditional fill, a balance update and an audit write. Authentication scores 5
for frequency because every other operation depends on it and 5 for impact because its failure
modes are asymmetric - refusing a valid user is an outage, admitting an invalid one is a
breach - but only 3 for likelihood, because the rules are simple and stable once written.
Market data scores 5 for frequency and 1 for complexity, the classic profile of a feature that
is cheap to cover and disproportionately damaging when broken, and it earns smoke coverage on
frequency alone. Stored-data integrity has no meaningful frequency of use, so its total is not
comparable with the rows above it; it is included because it is where the only real defect in
the seeded system lives, which is a useful corrective to any scoring table - the model ranked
it joint last and it is the row that found something.

### 4.1 How the scores map to the actual tags

Scenario counts in `tests/Api.Tests/Features` (counting Scenario and Scenario Outline
declarations, not expanded example rows):

| Feature file | Scenarios | @smoke | @regression | @security | @contract | @known-defect |
| --- | :-: | :-: | :-: | :-: | :-: | :-: |
| `OrderPlacement.feature` | 11 | 2 | 8 | 2 | 1 | 0 |
| `Authentication.feature` | 6 | 2 | 2 | 3 | 1 | 0 |
| `OrderHistory.feature` | 6 | 2 | 3 | 1 | 1 | 0 |
| `AccountAndMarketData.feature` | 8 | 3 | 3 | 1 | 2 | 0 |
| `DataIntegrity.feature` | 6 | 0 | 5 | 0 | 0 | 1 |

Some tags overlap: `@smoke @security` scenarios are counted in both columns.

The mapping is good in places and imperfect in others, and it is more useful to say which.
Order placement has the most scenarios of any feature, as the risk table demands: eleven
scenarios expanding to fourteen executed tests, against six for account and market data
combined. Authentication has the highest proportion of `@security` scenarios, matching its
asymmetric failure modes.

Where it breaks down is the smoke count. `AccountAndMarketData.feature` carries three `@smoke`
scenarios and `OrderPlacement.feature` only two, which inverts the risk table. That is
defensible - a smoke suite answers "is this build worth testing further?", and reading an
account and listing instruments are the cheapest signals that the application, its database
and its authentication are all alive - but it means the smoke tag is chosen for diagnostic
value per second rather than for risk. Those are different criteria and conflating them would
be dishonest. Similarly, `DataIntegrity.feature` has no `@smoke` scenarios even though its
invariants are the highest-value checks here: they are slower, they cover all historical data
rather than this build's changes, and one fails by design. A smoke suite that fails on
pre-existing data is a smoke suite people learn to ignore.

The tags are consumed by `.github/workflows/ci.yml`, where Reqnroll tags arrive as NUnit
categories: the API job runs `--filter "TestCategory!=known-defect"`, the UI job runs
`--filter "TestCategory!=Selenium"`, and `workflow_dispatch` accepts a tag input so a human
investigating a failure can run `smoke`, `regression` or `security` on its own.

---

## 5. The test pyramid, applied honestly

The shape of this repository is 171 framework unit tests, 45 API scenarios, 40 mobile tests
against a simulated driver, and 18 UI scenarios. That is deliberately bottom-heavy, and the
reasoning is per-level.

Framework unit tests (171) are the level most suites are missing entirely. They test the test
framework: the configuration precedence chain, the fail-fast guards, `Wait`'s deadline
arithmetic and the content of its failure messages, generator reproducibility, cleanup
ordering, the database layer's read-only guard, and the API response wrapper's null handling
and invariant number formatting. The justification is asymmetry of failure - a red application
test gets investigated within the hour, while a green test that verified nothing can survive
for a year. Four of the eight defects listed in `AI_ASSISTED_QA.md` were found at this level,
and none of them would have been found by any test of the application.

API scenarios (45) are where business rules belong. They are fast, have no browser to
synchronise with, can assert on status codes and error envelopes precisely, and can cross-check
the database. All eleven rules from section 2.1 are verified here.

UI scenarios (18) cover what only a browser can see: that the page renders the outcome, that
the user is taken somewhere sensible, that an error is displayed rather than swallowed, that
client-side routing protects a page, that a conditional field appears only for the order type
that needs it, and that error details are rendered and not just the summary.

Mobile tests (40) are four Reqnroll scenarios plus 36 unit tests of the mobile layer's own
logic - capability assembly, locator translation per platform, the cloud-grid credential
fail-fast, evidence recording. Their honest value is that the framework is correct and the
feature files would run unchanged against a device.

### 5.1 Why login-failure permutations live at API level only

`Authentication.feature` covers wrong password, unknown username, suspended account, empty
username, empty password and neither field sent - six failure modes, each asserting the status
code and the message. `tests/Ui.Tests/Features/Login.feature` covers wrong password once.

The reasoning is written into the feature file so it survives a future reviewer:

> Deliberately NOT re-tested here: every permutation of invalid credentials. Those rules are
> already covered exhaustively and far more cheaply at the API level, and duplicating them
> through a browser would add minutes to the suite for no additional information.

What the browser test adds over the API test is not the rule but the rendering: that the error
appears on the page, that its text is right, and that the user stays on the sign-in page rather
than being navigated somewhere confusing. One scenario establishes that the page can render an
authentication error. The seventh permutation of the same rendering path teaches nobody
anything and costs several seconds a run, for ever.

The same logic drives the Background in `tests/Ui.Tests/Features/OrderPlacement.feature`:
sign-in happens through the API, not through the form. That saves browser interaction per
scenario and, more importantly, means a defect in the sign-in page cannot fail an
order-placement scenario. A failure then points at the right screen. The one scenario that must
drive the login form is in `Login.feature`, once.

### 5.2 When the same rule at two levels is legitimate

Duplication is not always waste. The non-tradable instrument is tested twice on purpose,
because there are two separate defences and each can fail independently:

- `tests/Ui.Tests/Features/OrderPlacement.feature`: `Then the instrument "GOLD-SPOT" cannot be
  selected` - the dropdown disables it so the user cannot choose it.
- `tests/Api.Tests/Features/OrderPlacement.feature`: `An order for a non-tradable instrument
  is refused` - the API still returns 422 with a reason if that guard is bypassed, and no order
  is persisted.

Testing only the UI guard leaves the API open to anyone who posts directly. Testing only the
API rule leaves users able to select an instrument that will always be refused, which is a
usability defect rather than a security one. The test at each level asserts something the
other cannot see, so this is two tests of two things that happen to share a rule - not one test
written twice.

---

## 6. Automation selection, and what not to automate

### 6.1 Good candidates

- Stable rules with clear pass criteria. The eleven order-placement rules will not change
  shape often, and each has an unambiguous expected outcome.
- Repetitive, data-driven checks. The asset-class filter runs over four classes and the
  status filter over two, from one Scenario Outline each.
- Regression-heavy areas. Authentication is touched by every change to the token service.
- Business-critical paths. Order placement, always.
- Things a human cannot reliably observe. This is the strongest category and the most
  underrated. The demo's order form originally called `form.reset()` after rendering the
  success message; `reset()` fires a `reset` event, and the handler that clears stale errors
  cleared the alert region, so the success message was erased within a frame. A human clicking
  through the page sees a flicker and assumes they blinked. An automated assertion on the
  success alert found it immediately, and the failure initially looked like a test problem
  rather than an application one. See `src/TradingDemo.App/wwwroot/order-new.html` lines
  114-126.
- Invariants over stored data. Five SQL queries cover every row that has ever existed, which
  no amount of manual clicking can do.

### 6.2 What I would leave manual or exploratory in this domain

- Visual and design judgement. Nothing here asserts that the market grid is legible, that the
  price column is aligned, or that the rejection alert is noticeable enough to stop someone
  re-submitting an order. A screenshot-diff tool can tell you a pixel changed; it cannot tell
  you the change was bad, and on a page rendering live prices it will report a change on every
  run.
- One-off data migrations. If `instruments` gained a `tick_size` column and existing rows were
  backfilled, I would verify that once with SQL - the Group B invariants and query C3 are
  exactly the right tools - not build a permanent test for an operation that happens once.
- Rapidly changing UI. If the new-order page were being redesigned over three sprints, I would
  hold the browser scenarios at the one smoke journey and push coverage into the API suite,
  which does not care what the form looks like. Automating a screen about to be replaced buys
  a test you will delete.
- Tests whose setup costs more than the risk they retire. Verifying what happens when the
  price moves between the quote a user sees and the fill they get would mean adding a
  price-feed mechanism to the demo purely so a test could exercise it. On a real product with
  a real feed that test is essential; here it would test scaffolding I had just built.
- Exploratory testing around anything new. When order cancellation is added, the first hour
  should be a person trying to cancel an already-filled order, cancelling twice concurrently,
  cancelling someone else's order, and cancelling with the page open in two tabs. That hour
  finds the cases worth automating; writing the automation first only automates the cases you
  already thought of.
- Anything asserting on absolute counts of pre-existing data. `Order history shows the
  trader's seeded orders` asserts on `ORD-20240401-0001` by reference, never on "the trader has
  six orders". A count assertion is the commonest reason a suite becomes order-dependent and
  cannot run in parallel.

### 6.3 Rule of thumb

Automate a check when the cost of writing and maintaining it is less than the cost of the
defect it catches multiplied by how often you would otherwise have to look for it manually.
Two corollaries worth stating because they are where the rule usually gets broken: a check
that runs once is a task, not a test; and a check whose expected outcome requires human
judgement is not a check, it is a person with a tool.

---

## 7. Test data strategy

Three kinds of data, with different rules for each.

### 7.1 Deterministic committed seed

`database/seed/002_seed.sql` is fixed: the same rows with the same ids on every run. It exists
so that read-only assertions have something stable to name, and it is shaped so the interesting
paths are real rather than contrived:

- `analyst.readonly` is an active user with no account, which gives a genuine 404 path for
  `GET /api/accounts/me` rather than one manufactured by deleting a row.
- `trader.suspended` is `Suspended`, which gives the 403 branch a home.
- `GOLD-SPOT` is non-tradable, so the business rule at `TradingEndpoints.cs` line 135 has
  something real to refuse.
- BTCUSD has `max_quantity` 2.00, which is what makes the boundary outline meaningful.
- Order 3 is filled across two tranches, so the `SUM()` reconciliation in query B1 is
  exercised on healthy data as well as broken data.
- Order 6 is the planted defect.

The credentials in that file are the same throwaway string for every account - a narrow
exception justified by the demo's entire purpose being to be signed into by an automated test,
and called out in the file itself. On a real product these values would not be committed: they
would arrive from a secret store as `QA_Users__ActiveTrader__Password`, and
`ConfigurationLoader.ValidateUsers` already fails fast rather than defaulting a missing
password to an empty string, because an empty password produces authentication failures that
look like product defects.

### 7.2 Runtime-generated unique data

Anything a scenario creates is generated at run time by
`src/QaFramework.Core/TestData/DataGenerator.cs`, with two properties:

- Unique. Identifiers take the form `QA-4F2A9C-0001`: a constant `QA-` prefix, a run segment
  shared by everything one run creates, and a counter incremented with `Interlocked`. The
  prefix is a convention with a purpose - automation-created data is greppable, and removable
  by a single predicate. `scripts/generate_test_data.py` uses the same prefix so data created
  outside the .NET suite is equally identifiable.
- Reproducible. The seed is fixed per run and logged. Random data with an unlogged seed
  produces the worst class of failure there is: one that cannot be reproduced, and therefore
  cannot be triaged or proven fixed. Setting `QA_DATA_SEED` to the value from a failed run
  replays exactly the same data. `SharedHooks.RegisterScenarioDependencies` derives each
  scenario's generator from the run seed XORed with the scenario title, so data is unique per
  scenario under parallel execution while the whole run stays reproducible from one number.

Generated email addresses use `@example.invalid`. The `.invalid` TLD is reserved by RFC 2606
and is guaranteed never to resolve, so a test system cannot email a stranger.

### 7.3 Cleanup, and its honest limitation here

`src/QaFramework.Core/TestData/ResourceTracker.cs` records what a scenario created and removes
it in `[AfterScenario]`. It is a stack of undo actions rather than a list of ids, which matters
twice: reverse order respects foreign keys without the tracker knowing anything about the
schema, and because the caller supplies the removal logic the same tracker cleans up API
resources, database rows and files identically. Cleanup failures are collected and reported as
one aggregated warning, never thrown - a scenario that passed must not be reported as failed
because teardown could not delete something, and equally a teardown problem must not be silent.

Cleanup exists because its absence builds slowly and then all at once: a shared environment
fills with orphaned records, grids get slower, "should return 3 results" assertions start
returning 4,000, and the suite quietly becomes order-dependent.

The limitation here is worth stating plainly. The demo API has no delete endpoint, so the undo
action registered by `ScenarioSession.TrackOrderForCleanupAsync` restores the seeded state
rather than deleting one order. That is a blunt instrument - it removes every scenario's data,
not just this scenario's - and it is only safe because each suite owns its own application
instance and its own database file (section 8). Against a shared environment the correct
implementation is a targeted delete, and the tracker's interface would not change, which is
the point of expressing cleanup as an arbitrary undo action rather than as a list of ids.

---

## 8. Environment strategy

Each suite starts its own copy of the application and points at it.
`src/TradingDemo.AppModel/Setup/ApplicationUnderTest.cs` locates the built assembly, launches
it with an explicit `--urls` and `--Demo:DatabasePath`, and waits for the health endpoint using
`Wait.UntilAsync` with the `ApplicationStartup` timeout. If startup times out, the
application's own stdout and stderr are captured into the failure message, because "port
already in use" or "seed script not found" is very often the whole explanation.

Each suite has its own port and its own database file:

| Suite | Base URL | Database |
| --- | --- | --- |
| API | `http://localhost:5199` | `api-suite.db` |
| UI | `http://localhost:5198` | `ui-suite.db` |
| Mobile | `http://127.0.0.1:5185` (unused in the simulated run) | none |

That separation is not cosmetic. Both the API and UI suites call `/test-support/reset`, so
sharing one database would mean each suite periodically wiping the other's data; and sharing
one process meant the first test assembly to finish killed the application the others were
still using, after which every remaining scenario failed with a connection error that looked
nothing like the real cause. With separate ports and files the suites run concurrently as
separate CI jobs, which also gives per-suite timing and per-suite failure attribution.

Owning the application buys three things. There is no shared-environment flakiness: nobody
else deploys mid-run and nobody else's load causes a timeout, so the values in
`TimeoutSettings` can be sized for the work rather than padded to absorb strangers -
`ElementMs` is 5,000 and not 30,000, on the reasoning that if a button is not present within a
few seconds of the page settling, waiting thirty more will not help, it will only make the
failure take thirty seconds to report. There is no accumulating data, because a known version
runs against a known seed. And every failure is a real finding, which is the property that
matters most: a suite in which some failures are environmental teaches its team to re-run
rather than to investigate.

It is not always possible - a system too large to start on an agent, one depending on
third-party sandboxes, one whose data is too big to seed. `ApplicationUnderTest.StartAsync` is
idempotent, attaching to anything already healthy at the configured address rather than
starting a second copy, and `StartApplicationUnderTest: false` makes the whole suite run
unchanged against a deployed environment. That is the switch to use against a shared test
environment, where in exchange you accept longer timeouts, targeted cleanup instead of a seed
restore, and a standing agreement about who may deploy during a run.

Configuration is split by intent: `runsettings.json` says how we test (timeouts, browser,
evidence) and is identical in every environment, while `Environment.{Name}.json` says where we
test. Nothing in the first file can point the suite at the wrong system. Precedence runs
environment variables over the git-ignored `Environment.Overrides.json` over the committed
environment file, documented in `ConfigurationLoader` and covered by unit tests.

---

## 9. Definition of done and the CI gate

### 9.1 Entry criteria

- Acceptance criteria state observable outcomes, and for each error case they state which
  status code and which message. "Rejects invalid orders" is not testable; "a quantity outside
  the instrument's range is refused with 422 and a reason naming the permitted range" is.
- The story says whether a new rule is field validation or a business rule, because that
  determines 400 versus 422 and therefore how a client must handle it.
- Any new state needed by a test exists in the seed, or the story includes adding it.
- The build is green before the change goes in, so a new failure is attributable.

### 9.2 Definition of done for a story

- Automated coverage at the level the rule belongs to, per section 5: business rules at API
  level, rendering and routing at UI level, framework logic in `Framework.Tests`.
- Both sides of any new boundary, not only the rejecting side.
- Negative and error paths covered, including the shape of the error, not only its presence.
- Any write verified against the database, and any rejection verified as not persisted.
- New response fields reflected in the JSON schema, so the `@contract` scenarios cover them.
- New scenarios tagged, so CI can select them: `@smoke` for the per-pull-request gate,
  `@regression` for breadth, `@security` for authentication and authorisation, `@contract` for
  schema.
- Exploratory testing done around the new behaviour, with anything found either fixed or
  raised.
- The full suite green locally, and no test quarantined without an owner and a date.

### 9.3 Exit criteria and what CI actually enforces

`.github/workflows/ci.yml` runs seven jobs plus an aggregate gate. What it enforces:

- The solution builds in Release with `--warnaserror`. The framework is production code for
  the QA team and is held to the same bar as the application.
- All 274 selected tests pass across the four suites. `@known-defect` and the Selenium
  comparison fixture are excluded by filter.
- The Python helper scripts pass their own unit tests, the shell scripts parse, and
  `shellcheck` reports no warnings.
- A secret scan runs over full history, because a secret that was committed and then removed is
  still a leaked secret.
- The aggregate `gate` job checks `needs.*.result` explicitly. It runs with `if: always()`, so
  without that explicit check it would report success on a red build.

Two details in that file are worth pointing out because they are the ones most often got wrong.
Test steps use `continue-on-error: true` and the result-publishing step owns pass and fail.
Without `continue-on-error` a failing suite aborts the job and the results are never published,
so the one build you most need a report from is the one that does not produce one. But
`continue-on-error` alone makes a red suite look green, which is worse than having no pipeline:
it is an active lie. Both halves are required.

---

## 10. Metrics worth tracking, and metrics that are not

| Metric | Worth tracking? | Reasoning |
| --- | --- | --- |
| Escaped-defect rate | Yes, primary | Defects found in production per release is the only direct measure of whether testing worked. Everything else is a proxy. |
| Mean time to diagnose a failure | Yes | The `wwwroot` defect took minutes to fix and far longer to understand. Diagnosis time is where a suite's cost actually accumulates, and it is improvable: name the last observed value in a timeout, capture evidence at the moment of failure, cluster failures by message. |
| Flake rate per test | Yes | Retries per pass, tracked per test rather than per suite. A suite-level figure hides the two tests responsible for all of it. |
| Suite duration, per suite | Yes | A suite people skip has no value. Tracked per suite because "the tests take 20 minutes" is not actionable while "the UI job takes 18 of them" is. |
| Requirement-to-test traceability | Yes, qualitatively | Every rule in section 2.1 maps to a named scenario. Useful as a gap-finder, not as a percentage. |
| Defect detection stage | Yes | A defect caught by a framework unit test cost minutes; the same defect caught in a browser test costs an afternoon of misattribution. |
| Test count | No | Trivially gamed and actively misleading. Splitting one Scenario Outline into six scenarios raises the count and lowers the information. Reported here only with the qualifications in section 1. |
| Code coverage percentage as a target | No | Fine as a gap-finder, corrosive as a goal. Coverage measures which lines executed, not which behaviours were asserted. Every one of the eight defects listed in `AI_ASSISTED_QA.md` sat in code that a coverage tool would already have marked as covered, and the base64 padding-bit bug is the clearest case: the test executed the token-tampering path, the line was covered, and the assertion could not fail. |
| Pass rate as a target | No | Pass rate is a diagnostic, not an objective. The fastest way to raise it is to delete or weaken the tests that fail, and `--fail-under` in `scripts/analyse_test_results.py` exists as a gate against regression, not as a KPI to optimise. |
| Automation percentage | No | Section 6.2 lists things that should stay manual. A team measured on automation percentage automates them, and then maintains them. |

The opinionated version: the only two numbers I would put in front of a stakeholder are
escaped defects per release and how long it takes to know why the build is red. The rest are
instruments for the team, not targets for the organisation. A number that becomes a target
stops measuring the thing it was chosen for.

---

## 11. Flaky test policy

A flaky test is a test that passes and fails without the system changing. It is worse than a
failing test, because it teaches people that red does not mean broken - and once that lesson is
learned it applies to the genuine failures too.

The policy:

1. Investigate before quarantining. Most flakes are a fixable defect in the test: a sleep
   instead of a poll, an assertion on a total count, a shared fixture, or a wait whose deadline
   depends on how fast the probe happens to run. `Wait` exists to remove that class of cause,
   and its failure messages name the last observed value so an investigation can start from
   evidence rather than from a re-run.
2. Quarantine by tag if it cannot be fixed the same day - excluded from the gate by filter,
   with an owner and a review date. Not deleted, not commented out.
3. Two weeks. At the review date it is either fixed or the coverage gap is accepted explicitly
   and recorded, which is a decision somebody has made rather than a test everyone forgot.
4. Track the count. Quarantined tests are a debt with a visible balance, and a balance that
   grows for two consecutive sprints is a signal about the system, not about the tests.

This repository demonstrates the mechanism with a known defect rather than a flake, but the
handling is identical. The fill-reconciliation scenario in `DataIntegrity.feature` fails
against the committed seed, by design, because it finds `ORD-20240403-0006`. It is tagged
`@known-defect` and excluded from the gate by `--filter "TestCategory!=known-defect"` in
`.github/workflows/ci.yml`. It is still present, still readable and still runnable - the tag
input on `workflow_dispatch` runs it on demand, and `scripts/verify_backend_data.py` exits 1
when the same invariant is checked from outside the suite.

Deleting it would lose the coverage silently: the next person would have no way to know the
invariant was ever checked. Leaving it red would destroy trust in the gate, because a build
that is always slightly red is a build nobody reads, and the next genuine failure arrives into
an audience that has stopped looking. Excluding by tag keeps the gate honest and the problem
visible; the cost is that somebody has to look at the excluded list, which is why the tag
carries an owner and a date rather than being a permanent home.

---

## 12. Where this repository falls short

Stated plainly, because a strategy document that lists only strengths is a marketing document.

- No scheduled nightly run exists. The header of `DataIntegrity.feature` and the API job in
  `ci.yml` both describe `@regression` as running on a nightly build and the known defect as
  being reported by it, but there is no `schedule:` trigger in `.github/workflows/ci.yml`. The
  tag strategy is designed for that split and the pipeline does not yet implement it, so today
  `@regression` runs on every pull request along with everything else.
- Some referenced documents do not exist. Comments in the seed script, the environment files
  and the CI workflow point at `DEFECT_REPORT.md`, `SECURITY_REVIEW.md`,
  `docs/design-decisions.md` and `docs/ci-strategy.md`. Those files are not in the repository.
- The mobile suite proves wiring, not behaviour, because in CI it runs against a simulated
  driver. It is honest about that in the code and the feature files, but it is not mobile
  coverage and should not be counted as such.
- Cleanup is a seed restore rather than a targeted delete, only safe because each suite owns
  its own database (section 7.3).
- The page-size clamp is untested. `TradingEndpoints.cs` line 215 clamps `pageSize` to between
  1 and 100, and nothing asserts what happens at `pageSize=0`, `-1` or `1000` - by the
  reasoning of section 3.3, exactly the kind of boundary that should be covered on both sides.
- No performance, accessibility or cross-browser coverage. `ShouldRespondWithin` exists in
  `src/QaFramework.Api/Assertions/ApiResponseAssertions.cs` but no scenario uses it, there is
  no accessibility check of any kind, and the UI suite runs Chromium only. On a real product a
  browser matrix would be a CI matrix dimension and a per-endpoint response-time budget would
  be part of the contract scenarios.
- The suspended-trader rule is not tested at the order endpoint. Sign-in is refused with 403,
  so a suspended trader cannot obtain a token and the order path is unreachable in practice.
  On a real platform I would want an explicit test that a token issued before suspension stops
  working; the demo's token service does not model revocation at all.
- Everything runs on one machine. No containers, no deployed environment, no data volume worth
  speaking of. Section 8 describes the strategy I would want on a real product; what is
  demonstrated here is the version of it that fits on a laptop.
