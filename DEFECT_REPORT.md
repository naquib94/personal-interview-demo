# Defect report — worked example

One defect, written up the way I would actually write it up, using the data-integrity defect
planted in this repository's seed data.

The point of this document is not the defect. It is the shape of the report: what a developer
needs in order to fix something without asking a follow-up question, and what a QA engineer owes
them.

All data is synthetic. This is not a real defect from any system.

---

## Why this document exists

A defect report is a piece of technical writing with one job: transfer everything the reader
needs to reproduce, understand and fix the problem, and nothing else. Most bad reports fail in
one of three ways:

- **No reproduction path.** "Orders are wrong sometimes." Nobody can act on it.
- **A diagnosis dressed up as an observation.** "The fill service is broken." Perhaps, but the
  observation was "an order shows as filled with no fills"; the diagnosis belongs in a separate
  section, clearly labelled as a hypothesis, so the developer is not anchored on it.
- **No expected-versus-actual.** Without both halves, the report is an opinion.

The workflow below is the one I follow, and the sections after it are the report itself.

```text
Reproduce reliably
        ↓
Collect evidence            ← screenshots, request/response, logs, query output
        ↓
Isolate                     ← smallest reproduction; which layer is at fault?
        ↓
Document expected vs actual
        ↓
Assess impact and priority  ← business impact, not just severity
        ↓
Raise, and collaborate      ← the developer usually knows something you do not
        ↓
Retest the fix
        ↓
Regression test around it   ← what else touches this code path?
        ↓
Add automated coverage      ← so it cannot come back silently
```

Two steps in that list are the ones most often skipped, and both are the QA engineer's
responsibility rather than the developer's: **isolate** (a report that says "the UI is wrong"
when the API is at fault costs a developer half a day) and **add automated coverage** (a defect
fixed without a test is a defect that will return, and the second occurrence is always more
expensive because everybody assumes it was fixed).

---

## DEF-0001 — Order marked as Filled with no fill records

| Field | Value |
|---|---|
| **ID** | DEF-0001 |
| **Title** | Order marked as `Filled` has no rows in `order_fills`, so filled quantity is 0 |
| **Status** | Open |
| **Severity** | High — data integrity |
| **Priority** | P2 |
| **Component** | Order management / fill reconciliation |
| **Environment** | Local (`Environment.Local.json`), seeded database `api-suite.db` |
| **Found by** | Automated data-integrity check, `tests/Api.Tests/Features/DataIntegrity.feature` |
| **Found in** | Scheduled data-integrity run, not by a functional test |
| **Affected record** | `ORD-20240403-0006` |
| **Reproducible** | Always (100%) |

### Summary

Order `ORD-20240403-0006` has status `Filled` but no corresponding rows in the `order_fills`
table. The order reports a quantity of 3 while the sum of its fills is 0.

Every API response about this order is internally consistent and looks correct, which is why no
functional test detected it. The inconsistency is only visible when the order table and the fills
table are compared against each other.

### Steps to reproduce

```powershell
# 1. Build and let the suite seed the database.
dotnet build
dotnet test tests/Api.Tests --filter "TestCategory=smoke"

# 2. Run the data-integrity invariant.
dotnet test tests/Api.Tests --filter "TestCategory=known-defect"
```

Or directly against the database, with no test runner involved:

```sql
SELECT
    o.order_reference,
    o.status,
    o.quantity                        AS ordered_quantity,
    COALESCE(SUM(f.fill_quantity), 0) AS filled_quantity,
    COUNT(f.id)                       AS fill_count
FROM orders o
    LEFT JOIN order_fills f ON f.order_id = o.id
WHERE o.status = 'Filled'
GROUP BY o.id, o.order_reference, o.status, o.quantity
HAVING COALESCE(SUM(f.fill_quantity), 0) <> o.quantity;
```

The query is `B1` in `database/validation/qa-validation-queries.sql`.

### Expected result

No rows. An order whose status is `Filled` must have fill records summing exactly to its ordered
quantity. This is the defining invariant of a filled order: the status is a *claim*, and the fills
are the *evidence*.

### Actual result

One row:

| order_reference | status | ordered_quantity | filled_quantity | fill_count |
|---|---|---:|---:|---:|
| ORD-20240403-0006 | Filled | 3 | 0 | 0 |

Test output:

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

### Evidence

| Artefact | Where |
|---|---|
| Failing test output, including the offending row | CI artefact `results-api`, `api.trx` |
| The invariant query, documented | `database/validation/qa-validation-queries.sql`, query B1 |
| The executable version | `src/QaFramework.Core/Database/TradingQueries.cs`, `OrdersWithMismatchedFills` |
| Seeded state | `database/seed/002_seed.sql` — order id 6 has no matching `order_fills` row |

### Impact

Worth separating from severity, because "high severity" alone does not tell a product owner
whether to care.

- **Customer-facing.** A trader sees a filled order with no execution detail. In a real platform
  they would query it, and support would have no answer.
- **Financial reporting.** Any figure derived from `order_fills` — realised P&L, volume,
  commission — silently understates. The order table and the fills table disagree, and different
  reports will use different ones.
- **Reconciliation.** An end-of-day reconciliation against a counterparty would flag a break, and
  those are expensive to investigate.
- **Regulatory.** In a regulated venue, an execution record that cannot be evidenced is a
  reportable problem in its own right, independent of the money involved.

The defect affects one order out of nine in the seeded data (11%). The rate matters as much as
the count: a one-off is a data incident, and 11% is a systemic fault.

### Scope

Deliberately stated, because a report that does not bound the problem invites a fix that is too
narrow.

- Only `Filled` orders can exhibit this. `Pending`, `Rejected` and `Cancelled` orders correctly
  have no fills, and the query excludes them.
- The reverse case — fills existing for a non-filled order — is **not** currently checked. That is
  a gap in the invariant set, and I would add it as part of fixing this rather than afterwards.
- Partial fills are handled correctly: order `ORD-20240402-0003` has two fill rows summing exactly
  to its quantity, and the invariant passes for it. So the aggregation logic is sound and the
  problem is specific to fill records being absent.

### Hypothesis

Labelled as a hypothesis on purpose. The observation above stands on its own; this is where I
guess, and I would rather the developer disagree with a labelled guess than be anchored by an
unlabelled one.

The status transition to `Filled` and the insertion of fill records look like two separate
operations that are not atomic. Either:

1. The status is written first and the fill insert fails without rolling back the status; or
2. A back-office or import path sets status directly, bypassing the code that writes fills.

Both are consistent with the evidence. The second is more likely if the record predates the
current fill service.

Worth noting for contrast: the demo application's own `POST /api/orders` does this correctly —
`src/TradingDemo.App/Endpoints/TradingEndpoints.cs` writes the order, the fill and the balance
adjustment inside a single transaction. So the live path is not the source, which strengthens
hypothesis 2.

### Suggested fix

For the developer to decide, offered rather than prescribed:

1. **Make the transition atomic.** Status change and fill insertion in one transaction, so the
   state cannot exist.
2. **Enforce the invariant in the database** where the dialect allows it, so no code path can
   violate it regardless of which service writes.
3. **Remediate the existing row.** Either reconstruct the fill from the execution log, or move the
   order to a status that reflects what is actually known. Choosing which is a business decision,
   not a technical one, and needs a product owner.

### Verification plan

What I will do when a fix is offered — stated up front so the developer knows what "done" means.

1. **Retest the specific case.** `dotnet test tests/Api.Tests --filter "TestCategory=known-defect"`
   passes, and the invariant query returns zero rows.
2. **Regression around the code path.** The full `@regression` API suite, with attention to the
   partial-fill order (`ORD-20240402-0003`) — a fix that makes B1 pass by deleting fills, or by
   loosening the comparison, would be worse than the defect.
3. **The reverse invariant.** Add and run a check for fills attached to non-filled orders, closing
   the gap identified under *Scope*.
4. **Remove the quarantine.** Delete the `@known-defect` tag so the scenario joins the CI gate and
   the defect cannot silently return.
5. **Balance reconciliation.** Confirm the account balance is consistent with the corrected fills;
   a fill that appears without a corresponding balance movement is the same class of bug wearing a
   different hat.

---

## Why this defect is quarantined rather than deleted or ignored

The scenario is tagged `@known-defect` and excluded from the CI gate:

```yaml
--filter "TestCategory!=known-defect"
```

There are three options for a failing test that documents a real, unfixed problem, and only one
of them is defensible:

| Option | Consequence |
|---|---|
| Delete the test | The coverage is gone. When the defect is fixed nobody knows, and when it returns nobody notices. |
| Leave it failing | The build is permanently red. Within two weeks the team stops reading build results, and the *next* real failure is invisible. This is the most damaging option and the most common. |
| **Quarantine by tag** | The gate stays honest, the coverage stays in the repository, and the defect stays visible in every run's report. |

Quarantine is only legitimate with two things attached, and without them it is just a slower way
of deleting the test:

- **An owner and a date.** A quarantine with no expiry becomes permanent. In a real project this
  tag would carry a ticket reference and be reviewed at every sprint boundary.
- **Reporting.** The quarantined set must appear somewhere a human looks — a nightly run, a
  dashboard, a standing agenda item. `scripts/analyse_test_results.py` reports it, and the
  `@known-defect` tag makes the set queryable.

The same mechanism is what I would use for a genuinely flaky test, with one difference: a flake
gets a much shorter deadline, because a flaky test is a defect in the *suite* and the suite is
mine to fix.
