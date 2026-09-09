# AI-assisted QA engineering

How I actually use AI in QA work, what it is genuinely good at, where it fails in ways that are
specific to testing, and what that means for how generated tests must be reviewed.

This repository was built with AI assistance under human direction. That is stated plainly here
rather than left to be inferred, and the evidence that the review actually happened is the
[list of bugs](#the-central-argument) further down — several of which were bugs *in the generated
tests themselves*.

---

## The short version

> AI accelerates QA engineering but does not replace QA judgement.

That sentence is on a lot of slides, and on its own it is not worth much. The specific version is
more useful:

> AI is very good at producing test code that looks correct, and poor at knowing whether a test
> **can actually fail for the reason it claims**. A test that passes for the wrong reason is worse
> than no test, because it creates confidence that nothing is checking.

Everything below is an argument for that second sentence.

---

## Where it genuinely helps

Ordered by how much time it actually saves me, not by how impressive it sounds.

### 1. Scenario generation from requirements

The strongest use, and the least glamorous. Given a requirement, AI will produce twenty scenarios
in a minute. Perhaps twelve are useful, five are duplicates, and three are wrong. That is a good
trade: my job becomes *editing a list* rather than *producing a list*, and editing surfaces
different thinking than generating does.

It is particularly good at the permutations a human skips through boredom. Asked about order
placement, it will reliably suggest the market-order-with-a-limit-price case
(`tests/Api.Tests/Features/OrderPlacement.feature`, "An order type must be consistent with the
limit price") — a real rule, easy to forget, and dull to think of unprompted.

What it will *not* do is tell me which of the twenty matter. See
[what it is bad at](#where-it-fails).

### 2. First-draft automation skeletons

Boilerplate: a page object's field declarations from a chunk of HTML, a step definition class
from a feature file, a set of `TestCaseData` rows from a table. This is mechanical translation,
and mechanical translation is exactly what a language model is for.

The `data-testid`-driven page objects in `src/TradingDemo.AppModel/Pages/` are a good example.
Given `wwwroot/order-new.html` and one existing page as a pattern, generating `NewOrderPage`'s
declarations is a thirty-second job that would otherwise be ten minutes of careful copying — and
careful copying is precisely where a human transposes two test IDs.

### 3. Refactoring and consistency

Applying an agreed pattern across many files. When the component abstraction settled, propagating
it was mechanical. Likewise: "this class uses `Console.WriteLine`, route it through `TestLog`
instead". AI is good at consistency, which humans are measurably bad at across more than about
five files.

### 4. Synthetic test data

Generating plausible reference data — instrument symbols, display names, price levels, account
numbers — that is internally consistent and not copied from anywhere real. The seed data in
`database/seed/002_seed.sql` was produced this way and then shaped by hand so that specific rows
serve specific tests (`GOLD-SPOT` non-tradable, `BTCUSD` with a low maximum quantity,
`analyst.readonly` with no account at all).

One caution that matters more than it sounds: **generated data can look too real**. An email
address at a domain that actually exists means a test system can send mail to a stranger, and
that has caused real incidents. This repository uses `example.invalid` throughout — the reserved
TLD from RFC 2606, guaranteed never to resolve. That is the kind of detail a model will not add
unprompted.

### 5. Failure triage

Pasting a stack trace and a log excerpt and asking "what are the three most likely causes" is a
genuinely useful way to break a stare-at-the-screen deadlock. It is a *hypothesis generator*, not
a diagnosis. The value is that it suggests the possibility you had not considered; the risk is
anchoring on a confident wrong answer.

A concrete case from this build: Playwright reported "element not found" on what should have been
a fully rendered page. The suggestions were reasonable and mostly wrong. What actually identified
it was the framework's own failure evidence — the captured page source was
`<html><head></head><body></body></html>`, which meant the page was not rendering at all rather
than the locator being wrong. `wwwroot` was not being copied to the build output, so every page
404'd. **The evidence found it, not the model.** That is worth remembering when deciding what to
invest in.

### 6. Finding missing edge cases

Asking "what have I not tested here" against an existing feature file. It is good at surfacing
the boring omission — the boundary you tested on one side only, the field you validated for
presence but not for type.

### 7. Documentation

Explaining a design decision in prose, given the decision. Note the direction: the decision is
mine, the explanation is drafted. Asking it to *make* the decision produces something plausible
and unowned, and unowned decisions cannot be defended in a review.

---

## The workflow

```text
Requirement
    ↓
AI-assisted scenario generation          ← breadth: get the permutations on the page
    ↓
QA review                                ← THE step. Which matter? Which can actually fail?
    ↓                                      What is missing? What is at the wrong level?
Automation implementation                ← AI drafts, human owns
    ↓
Test execution                           ← non-negotiable. Every test must be RUN.
    ↓
Failure analysis                         ← AI as hypothesis generator
    ↓
QA validates root cause                  ← is this a product defect, or a test defect?
```

Two things about this diagram.

**The review step is the whole job.** Removing it does not give you a faster QA process; it gives
you a larger test suite of unknown value, which is a liability that grows.

**"Test execution" is not a formality.** The failures below were all found by running tests,
several of which had already been read and approved. Reading a test tells you whether it is
plausible. Running it tells you whether it works. They are not the same check, and the second one
is the one AI cannot do for you.

---

## The central argument

Two examples from this repository. Both were generated, both looked right, both survived a
read-through, and both were worthless. They are the best material here because they are specific
to how AI fails at testing rather than to how it fails generally.

### Example 1 — a boundary test that could never verify the boundary

`BTCUSD` has a maximum order quantity of 2. The obvious boundary test:

```gherkin
When the trader places a market order to buy 2 of "BTCUSD"
Then the order is accepted
```

Correct-looking, well-formed, exactly what the requirement says. It failed. And the reason is the
interesting part:

```text
422 Unprocessable Entity
"Insufficient funds: the order requires 122570.00 but the account balance is 24998.92"
```

Two units of Bitcoin at roughly 61,285 each is about 122,000, against a 25,000 balance. **The
insufficient-funds rule fires before the quantity rule can be reached.** The test could never
have verified the boundary it was written for. Had the balance happened to be larger, it would
have passed — and it would still not have been testing the boundary; it would have been passing
by coincidence.

The fix was to choose data that isolates the rule: `EURUSD` has a maximum of 50 units at about
1.08, so 50 units costs 54 and the funds rule cannot interfere. The reasoning is recorded in the
feature file itself, because the next person to touch it needs to know why the instrument is what
it is:

```gherkin
  # EURUSD is used rather than BTCUSD, and the reason is a genuine test-design trap worth
  # recording. BTCUSD's maximum quantity is 2, but 2 units cost roughly 122,000 against a
  # 25,000 balance - so the insufficient-funds rule fires first and the order is rejected for a
  # reason unrelated to the boundary...
```

**What this shows.** Choosing data that isolates the rule under test requires understanding the
*interaction* between several rules and the state of the fixture. The model had the schema, the
seed data and the endpoint code available and still could not see it — because seeing it requires
holding two rules and an account balance in mind simultaneously and asking "which fires first?".
I very nearly "fixed" this by loosening the assertion, which would have destroyed the test while
making the build green.

### Example 2 — a security test that passed without testing anything

A test that a forged bearer token is rejected. The generated approach: take a valid token and
alter the last character of its signature.

```csharp
// The original, and it was wrong
string tamperedSignature = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
```

Entirely reasonable on inspection. The test passed. It was also verifying nothing.

A 32-byte HMAC is 43 base64url characters. Forty-three characters encode 258 bits — two more than
the 256 the signature uses — so **the final character's low bits are padding**. Changing `A` to
`B` there decodes to the identical byte array. The signature still verified, the token was still
valid, and the order was accepted. The test passed because the request *succeeded*, in a test
asserting that it should fail.

This one is worse than Example 1, because Example 1 failed loudly. This passed. Silently. In a
security test.

```csharp
// The fix: a character in the MIDDLE, which guarantees a different byte array
int middle = parts[1].Length / 2;
char replacement = parts[1][middle] == 'A' ? 'B' : 'A';
string tamperedSignature = parts[1][..middle] + replacement + parts[1][(middle + 1)..];
```

**What this shows.** Catching this needed base64 padding arithmetic — knowledge with no
connection to trading, authentication or test design. No amount of reading the test would have
revealed it; the only thing that revealed it was noticing that a negative test was passing and
asking *why*. Which brings me to the habit that matters most.

### The habit both examples argue for

**A negative test that has never failed is not yet a test.** Before trusting one, break it
deliberately and confirm it goes red for the reason you expect:

- Comment out the validation and check the test fails.
- Feed the security test a *genuinely* valid token and check it fails.
- Point the boundary test at the wrong side of the boundary and check it fails.

This costs about a minute per test. It is the single highest-value review step for generated test
code, and it is the one AI cannot perform on its own behalf, because the model has no way to
distinguish "this passed because the system is correct" from "this passed because my assertion is
unreachable".

---

## Where it fails

Beyond the two examples, the failure modes I plan around:

| Weakness | Why it matters in QA specifically |
|---|---|
| **Cannot recognise a vacuously-passing test** | The dominant risk. Covered above. Also shows up as filter tests that pass against an empty result set — `every returned instrument belongs to asset class X` is trivially true of zero instruments, which is why `AccountAndMarketSteps` asserts non-empty *first* |
| **No sense of business risk** | It will not tell you that order placement deserves ten times the coverage of the market page because it moves money. Prioritisation needs commercial context, and the [risk matrix](TEST_STRATEGY.md) is a human artefact |
| **Cannot decide what not to automate** | Asked for automation, it produces automation. It will never answer "this is a bad automation candidate, test it exploratorily" — which is often the right answer |
| **Confidently wrong about library specifics** | It suggested the default `JsonSchema.Net` evaluation would populate per-location errors. It does not; the default `OutputFormat` is `Flag` and populates nothing, so schema failures named no field. Found by a framework unit test |
| **Optimises for plausible, not for true** | Generated test *names* are the clearest tell: `Login_Works` is plausible and says nothing. A good name states the condition and the expected outcome |
| **No organisational context** | Does not know which team owns what, which environment is flaky on Thursdays, or which "intermittent failure" is actually a real defect nobody has triaged |
| **Weak on the right test level** | Happily generates a browser test for a rule better covered by an API test in a tenth of the time. The pyramid is a judgement call |

---

## Responsible use

Non-negotiables, in the order I would enforce them.

**Never paste proprietary material into a third-party model.** Production source, customer data,
credentials, connection strings, internal hostnames, architecture diagrams, ticket contents.
Assume anything sent leaves your control. This applies to a stack trace as much as to a source
file: a stack trace carries internal namespaces, server names and file paths.

**Work from an abstracted description instead.** "A REST endpoint that accepts an order with a
quantity and a symbol, validates against per-instrument bounds, and rejects with 422 when funds
are insufficient" is enough to generate useful scenarios, and reveals nothing. That is exactly
how this repository was built: the patterns came from experience, and every line here was written
against a synthetic domain invented for the purpose. See [`SECURITY_REVIEW.md`](SECURITY_REVIEW.md).

**Check your organisation's policy before using a tool, not after.** Approved tooling, data
classification and retention terms differ, and "I did not know" is not a defence.

**Be careful with generated data that looks real.** Reserved domains (`example.invalid`,
`example.com`), obviously synthetic identifiers, prefixes that mark automation-created records —
this repository uses `QA-` for exactly that reason. A generated phone number can be someone's
phone number.

**Never attribute to AI something you have not verified.** If it appears in a test, a report or a
pull request, you own it. "The AI wrote it" is not a defence in a post-incident review, and
rightly so.

**Mind provenance and licensing.** Generated code can resemble training material. For test code
this is usually a low risk; for anything shipped it deserves a thought.

---

## How this repository was built

Directly, since it is the most relevant evidence available.

The architecture, the layering, the choice of what to test at which level, the risk
prioritisation, and every decision in [`docs/design-decisions.md`](docs/design-decisions.md) are
mine — several of them derived from reading real automation frameworks and forming a view about
what worked and what did not. AI was used heavily for implementation: page objects, step
definitions, the demo application, boilerplate, and drafting prose from decisions already made.

Every test was executed. Every failure was investigated rather than worked around. That process
found eight real bugs, listed in the [README](README.md#bugs-this-framework-found) — two in the
demo application, four in the framework, and **two in the generated tests themselves**, which are
the two examples above.

I could have presented this as entirely hand-written. It would have been less true and less
interesting. The useful claim is not "I did not use AI"; it is "I used AI and can show you exactly
where it was wrong, and how I found out" — because that is the skill that will still matter when
the tooling has moved on again.

---

## Further reading

- [`TEST_STRATEGY.md`](TEST_STRATEGY.md) — the risk prioritisation and level-selection judgements
  referenced above as the human artefacts
- [`README.md`](README.md#bugs-this-framework-found) — the full bug list with how each was found
- [`DEFECT_REPORT.md`](DEFECT_REPORT.md) — a worked defect report, and why a hypothesis is
  labelled as one
- [`SECURITY_REVIEW.md`](SECURITY_REVIEW.md) — independent authorship and the secret-scanning gate
