# Locator strategy

How elements are addressed, why the order of preference is what it is, and which habits this
suite refuses to acquire. The web half is a contract with `src/TradingDemo.App/wwwroot`; the
mobile half is a contract with a hypothetical native build of the same product. Both are
negotiated, not discovered.

## The contract with the application

The demo application states its side of the contract in a comment at the top of
`src/TradingDemo.App/wwwroot/app.js`, which is the right place for it — a convention documented
only in the test suite is a convention the next developer will break without knowing:

```text
//   1. data-testid attributes.   Every element a test needs is addressed by a stable
//                                data-testid, never by CSS class or DOM position. Naming is
//                                <area>-<thing>-<kind>, e.g. login-username-input.
//
//   2. A single busy indicator.  #app-busy carries the .active class for the whole duration of
//                                any in-flight request.
```

Everything below follows from those two sentences.

### Naming: `<area>-<thing>-<kind>`

Three segments, lower case, hyphen separated. The area maps to a page or a shared region, the
thing is the business noun, the kind is the control archetype. Real examples, each verifiable in
`wwwroot`:

| `data-testid` | File | Area | Thing | Kind |
| --- | --- | --- | --- | --- |
| `login-username-input` | `index.html` | login | username | input |
| `account-balance-text` | `account.html` | account | balance | text |
| `market-asset-class-select` | `market.html` | market | asset class | select |
| `new-order-submit-button` | `order-new.html` | new-order | submit | button |
| `order-history-grid-row` | `orders.html` | order-history | grid | row |
| `nav-order-history-link` | `app.js` (shared chrome) | nav | order-history | link |
| `app-busy` | `app.js` (shared chrome) | app | busy | — |

Two properties are worth having on purpose. The `kind` suffix tells a reader which framework
component the element will be wrapped in before they open the page object: `-input` becomes a
`TextInput`, `-select` a `Dropdown`, `-text` a `TextElement`, `-button` a `Button`. And the
`area` prefix makes the set greppable — `rg 'data-testid="order-history-'` returns everything
that page exposes, which is how you check whether the element you need already exists.

The convention crosses suites deliberately.
`src/QaFramework.Mobile/Screens/Trading/LoginScreen.cs` declares
`MobileLocator.Shared("the username field", "login-username-input")` — the same string, on the
assumption that a native build would expose it as an accessibility identifier. That is an
assumption, and `MobileLocator.Shared`'s documentation comment states it as one.

## Preference order for web locators

| Rank | Strategy | What it buys | What it costs |
| --- | --- | --- | --- |
| 1 | `data-testid` | Decoupled from markup, styling, copy and layout. Explicit: an element carrying one is an element someone agreed to keep stable. Greppable in both directions. | Requires the application to cooperate. Adds attributes with no user-facing purpose, which some teams resist. |
| 2 | Role and accessible name | Tests what a user and a screen reader perceive, so it catches accessibility regressions as a side effect. Needs no application change. | Ambiguous as soon as two controls share a name. Breaks on a copy change, because the accessible name usually *is* the visible text. |
| 3 | Stable structural CSS | Available without asking anyone, and fast. | Couples the test to structure that exists for layout reasons, so a refactor changing nothing a user sees can break it. |
| 4 | Visible text | Reads well, needs nothing from the application. | Breaks on rewording, fails outright under localisation, and invites substring matching (below). |
| 5 | XPath | Sometimes the only way to express "the row containing X" or an ancestor traversal. | Slowest, least readable, hardest to attribute: an XPath encodes the view hierarchy, so an innocuous re-layout breaks a locator that was never about layout. |

This suite lives almost entirely at rank 1. Every selector in
`src/TradingDemo.AppModel/Pages` is a `data-testid`, and the framework builds the selector rather
than the caller — `TextInput`, `Dropdown`, `Button` and `TextElement` each take a bare test id
and compose `$"[data-testid='{testId}']"` themselves. That makes the convention mechanical:
drifting to rank 3 requires editing a framework class, not a moment's convenience in a page
object. Rank 2 is unused here, which is a gap rather than a considered rejection — the demo's
controls are all labelled, and a suite with an accessibility mandate would be right to prefer it.

## Rows are addressed by business key, never by index

`src/QaFramework.Web/Components/Grid/DataGrid.cs` takes a `rowKeyAttribute` in its constructor
and resolves through it:

```csharp
public ILocator Row(string key) =>
    Context.App.Locator($"[data-testid='{rowTestId}'][{rowKeyAttribute}='{key}']");

public ILocator Cell(string key, string field) => Row(key).Locator($"[data-field='{field}']");
```

The application supplies the keys. `orders.html` stamps every row with `data-reference` and
every cell with `data-field`:

```html
<tr data-testid="order-history-grid-row" data-reference="${o.orderReference}">
  <td data-field="orderReference">${o.orderReference}</td>
  ...
  <td data-field="status"><span class="${pillClass(o.status)}">${o.status}</span></td>
```

`market.html` does the same with `data-symbol`. The two grids are configured in
`OrderHistoryPage.Orders` (`rowKeyAttribute: "data-reference"`) and `MarketPage.Instruments`
(`rowKeyAttribute: "data-symbol"`), and a real call from
`tests/Ui.Tests/StepDefinitions/MarketAndHistorySteps.cs` reads:

```csharp
[Then("the order {string} is shown as {string}")]
public Task ThenTheOrderIsShownAs(string reference, string status) =>
    orderHistoryPage.Orders.CellShouldHaveTextAsync(reference, "status", status);
```

**The failure mode of index addressing is that it is silent.** `Rows.Nth(0)` does not throw when
the grid is sorted differently, a default filter is introduced, or another scenario places an
order a few milliseconds earlier. It finds a row — just not the row the test was written about.
The assertion then passes or fails for reasons unrelated to the behaviour under test, and both
outcomes are damaging: a pass hides a regression, a failure sends someone to investigate a defect
that does not exist. There is no error message to read, because from the framework's point of
view nothing went wrong.

This is also what makes the UI suite parallel-safe at two workers
(`tests/Ui.Tests/Support/ParallelisationSetup.cs`). From
`tests/Ui.Tests/StepDefinitions/OrderPlacementSteps.cs`:

```csharp
// Addressed by business key. An index-based assertion would break as soon as another
// scenario places an order concurrently, which is exactly what happens in this suite.
await orderHistoryPage.Orders.ShouldContainRowAsync(RequireReference());
```

Cells follow the same rule via `data-field`, for a smaller but more frequent reason: inserting a
column must not renumber every assertion in the suite. `orders.html` renders seven columns; an
eighth inserted at position two would invalidate every positional cell assertion in the codebase,
again without any error.

The cost is real. Business-key addressing needs the application to publish a key, and it cannot
express assertions that genuinely are about position. "The newest order appears first" needs
`DataGrid.VisibleKeysAsync()` and an ordering assertion on the returned list — which is why that
method exists, and why it returns keys rather than indices.

## The busy indicator as a synchronisation contract

`src/QaFramework.Web/Synchronisation/PageSynchronisation.cs` waits on one element and nothing
else:

```csharp
private const string BusyIndicator = "[data-testid='app-busy']";
```

`app.js` maintains it with an in-flight counter, so it stays active across concurrent requests
rather than clearing when the first one returns:

```javascript
function setBusy(busy) {
  inFlight += busy ? 1 : -1;
  const el = document.querySelector('[data-testid="app-busy"]');
  if (el) el.classList.toggle('active', inFlight > 0);
}
```

Playwright's auto-waiting solves "is this element present and clickable". It cannot know the
application has just fired a request and is about to replace the grid being read. That gap is
where most UI flakiness lives, and the two usual responses are both bad: a `Thread.Sleep` makes
every run slower and only slightly less flaky, and a retry loop around the assertion converts a
race into a slow race.

The indicator moves the problem to the layer that has the information — only the application
knows whether it is busy. Asking it to say so costs a developer the five lines above (most
single-page applications already track in-flight requests for a spinner, so the work is exposing
state that already exists) and it removes a class of failure rather than reducing its frequency.
That is a better return than any retry policy.

Negotiating it is a QA engineering activity in the same sense that agreeing an API contract is:
knowing what to ask for, being able to explain the return, and accepting a constraint in
exchange. The framework must not fail when the indicator is absent, or the pages that have not
adopted it yet never will. Hence `WaitUntilIdleAsync` treats a missing indicator as idle, and
`WaitForResponseWhileAsync` is the documented fallback for applications that refuse —
synchronise on the network instead, which works but couples the test to a URL pattern.

One implementation note recorded in the code: the class is toggled rather than the element
removed, so waiting for `detached` can legitimately never succeed. The reliable signal is the
class assertion, and the `detached` wait is wrapped in a `catch` that falls through to it.

## Mobile locators

`src/QaFramework.Mobile/Elements/MobileLocator.cs` is a record carrying a *pair* of selectors and
a strategy:

```csharp
public sealed record MobileLocator(
    string Description,
    string AndroidSelector,
    string IOSSelector,
    LocatorStrategy Strategy = LocatorStrategy.AccessibilityId)
```

`For(Platform)` resolves the pair at the last possible moment, inside the driver, and throws a
message naming the locator when the requested platform has no selector. The alternative — a bare
selector string resolved at the call site — is what makes adding iOS later feel like a rewrite:
every locator becomes an `if (platform == ...)`, or a duplicate screen object per platform that
then drifts. `MobileLocator.Shared(description, selector)` covers the common case where both
platforms use the same accessibility identifier.

`Description` is required rather than optional because it is what appears in a timeout message.
"Timed out waiting for the order history grid" is diagnosable from a CI log; "Timed out waiting
for `//*[@resource-id='...']`" sends the reader to the code first.

The preference order is the member order of `src/QaFramework.Mobile/Elements/LocatorStrategy.cs`,
stated in its documentation comment so the two cannot diverge:

| Rank | Strategy | Notes |
| --- | --- | --- |
| 1 | `AccessibilityId` | Maps to `content-desc` on Android and the accessibility identifier on iOS, so one string serves both platforms. Set deliberately by developers, stable across layout changes, and asking for it improves the product's accessibility as a side effect — the rare case where the testability argument and the user-facing argument point the same way. |
| 2 | `Id` | Stable, but Android resource ids and iOS identifiers rarely match, so it costs a platform pair. |
| 3 | `AndroidUiAutomator` / `IosClassChain` | Native queries evaluated on the device rather than round-tripping every candidate over HTTP. The right tool for scrolling a long list into view. Platform-specific by construction. |
| 4 | `XPath` | Slowest on both platforms, because the driver serialises the whole page source to evaluate it, and it couples the test to the view hierarchy. Kept in the enum because sometimes there is no alternative, and pretending otherwise just means someone writes an XPath in a string literal somewhere worse. |

## What to ask developers for, and what to offer

Four things, in the order I would ask for them:

1. **A `data-testid` on every element a test needs to address**, following an agreed naming
   convention. One attribute, no behaviour.
2. **A business key on every row of a collection, and a field name on every cell** —
   `data-reference`, `data-symbol`, `data-field`. Largest return of the four, and usually the
   cheapest, because the client already holds the object it is rendering.
3. **One busy indicator per page**, maintained with an in-flight counter rather than set and
   cleared per request.
4. **A dedicated element for any value a test must read**, rather than a value embedded in prose.
   `order-new.html` publishes the new order reference into `new-order-reference-text`, and the
   step definition reads it there with the reason attached: "Read from the dedicated element
   rather than parsed out of the success prose ... which keeps this step working when the wording
   changes."

What is on offer in return matters, because "please add attributes for my tests" is a weak
request on its own: failures that name the application rather than the test (every component
carries a human description, so a red build reports `Failed to click the Place order button` with
the current URL attached, which a developer can act on without opening test code); no requests to
slow the application down, since the alternative to a busy indicator is longer timeouts and
retries; fewer "is it the test or the app?" conversations, because most of that ambiguity comes
from synchronisation and row addressing; and accessibility improvements as a side effect wherever
accessibility identifiers are involved, which survive the test suite being deleted.

The reciprocal commitment matters too. If a team provides these, the suite must not then reach
around them: a framework that asks for test ids and still contains XPath has spent the goodwill
and kept the flakiness.

## Anti-patterns

**Index-based rows.** `Rows.Nth(0)` in place of `Row("ORD-20240403-0001")`. The bug: the
assertion silently targets a different record after a sort change, a new default filter, or a
concurrent insert. Nothing throws; the test simply stops testing what it says it tests. This is
why `DataGrid` offers no index-based row or cell helper — the only positional access is
`VisibleKeysAsync()`, which returns keys.

**Brittle text assertions that parse prose.** `orders.html` renders "Showing 3 of 12 orders" from
three separate DOM nodes. The tempting assertion reads the sentence and extracts a number. The
bug: a copy change to "Displaying 3 of 12" breaks a test that has nothing to do with copy, and a
translation breaks all such tests at once. What the suite does instead — count DOM rows and
compare against the number displayed:

```csharp
int actualRows = await orderHistoryPage.Orders.RowCountAsync();
string reported = (await orderHistoryPage.DisplayedCount.GetTextAsync()).Trim();

reported.Should().Be(actualRows.ToString(),
    "the count shown to the user must match the rows actually rendered");
```

Two independent views of the same fact rather than either against a hardcoded number, so it stays
valid as the seed data changes and still catches the defect it is aimed at: a count computed from
the wrong collection.

**CSS-framework class names.** `orders.html` styles status cells with `pill pill-filled` and
sides with `side-buy`. Locating on `.pill-filled` works today. The bug: a theme upgrade, a
utility-class migration or a change of component library renames it, and the failure reads as
"element not found" on a page that is functionally perfect. Class names exist to be changed by
whoever owns the styling; a locator built on them turns a styling change into a test change.

**Substring matching by default.** `ToContainTextAsync("Filled")` also passes on "Partially
Filled" and on "Unfilled". In a trading context those are three different business outcomes, and
the middle one is the one most worth catching. Hence `DataGrid.CellShouldHaveTextAsync` asserts
exact text by default, with the reasoning in the method's own remarks: "Where a substring
genuinely is the requirement, that should be explicit at the call site rather than being the
default everywhere." `AlertMessage` keeps both, with the substring version named
`ShouldContainTextAsync` so that choosing it is visible in the diff; the UI suite uses it once,
for `"placed successfully"`.

**Coupling "an error appeared" to "the error said X".** The application separates the two by
design. `Ui.alert` in `app.js` renders a constant `data-testid="alert-message"` and carries the
kind in a separate attribute:

```html
<div class="alert alert-${kind}" data-testid="alert-message" data-alert-kind="${kind}">
  <span data-testid="alert-text">${message}</span>
```

So `AlertMessage.ShouldShowAsync(AlertKind.Error)` asserts that an error was shown without
asserting any wording, and `ShouldHaveTextAsync` is reserved for cases where the wording is
itself the requirement — a regulated disclosure, or a message a support team scripts against.
Asserting only the text means a copy edit breaks a functional test; asserting only the kind means
a success alert reading "Order rejected" would pass. The split lets a suite make the right choice
per assertion instead of once for everything.
