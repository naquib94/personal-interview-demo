# ==========================================================================================
# Backend data integrity
# ==========================================================================================
# These scenarios assert nothing about the API. They query the database directly and verify
# that the stored data is internally consistent.
#
# Why this belongs in a functional suite:
#   * API-level tests can only see what the API chooses to expose. A status transition that
#     updates the order table but not the fills table is invisible to every endpoint.
#   * Each query below covers ALL historical data, not just the rows this run created. That
#     catches damage done by a bad migration, a back-office tool, or an import path that no
#     functional test exercises.
#   * A returned row is, by construction, a defect - so these scenarios need no expected
#     values to maintain.
#
# The fill-reconciliation scenario is tagged @known-defect because it CURRENTLY FAILS against
# the seeded data, by design. Order ORD-20240403-0006 is marked Filled with no fill rows. It is
# a deliberately planted defect, it is documented in DEFECT_REPORT.md, and it demonstrates the
# point of writing these queries at all: this one finds something.
#
# It is excluded from the CI gate by tag rather than deleted or commented out - the honest way
# to keep a suite green while keeping a known problem visible. Deleting it loses the coverage;
# leaving it failing trains the team to ignore red.
# ==========================================================================================

@api @data-integrity
Feature: Backend data integrity
  Invariants that must hold across all stored trading data, regardless of how it got there.

  @regression
  Scenario: No order references another entity that does not exist
    When the orphaned-records invariant is checked
    Then no rows violate the invariant

  @regression
  Scenario: No two orders share a customer-facing reference
    When the duplicate-reference invariant is checked
    Then no rows violate the invariant

  @regression
  Scenario: Every stored order's type is consistent with its limit price
    When the limit-price consistency invariant is checked
    Then no rows violate the invariant

  @regression
  Scenario: Every stored order's quantity is within its instrument's tradable range
    When the quantity-range invariant is checked
    Then no rows violate the invariant

  @known-defect
  Scenario: Every filled order's fills sum to the ordered quantity
    When the fill-reconciliation invariant is checked
    Then no rows violate the invariant

  # A positive demonstration of the same technique: an aggregate query used for risk profiling
  # rather than for finding defects. This is the "which areas are used most" input to
  # risk-based prioritisation described in TEST_STRATEGY.md.
  @regression
  Scenario: Account activity can be profiled for risk-based prioritisation
    When accounts with at least 5 orders are profiled
    Then at least 1 account is reported
    And every profiled account reports the instruments it has traded
