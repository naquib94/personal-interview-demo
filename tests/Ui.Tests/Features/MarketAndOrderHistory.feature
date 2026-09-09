@ui
Feature: Viewing market data and order history
  Read-only screens. Cheap to cover, used constantly, and a broken grid is immediately visible
  to every customer - which is why they earn smoke coverage despite being low complexity.

  Background:
    Given the active trader is signed in through the API

  @smoke
  Scenario: The market page lists instruments with their prices
    Given the market page is open
    Then the market grid contains the instrument "EURUSD"
    And the instrument "EURUSD" is shown in the asset class "FX"
    And the instrument "GOLD-SPOT" is shown as not tradable

  # Filtering is asserted on both sides: the matching rows appear AND the non-matching rows are
  # gone. Asserting only the first would pass against a filter that does nothing at all, which
  # is the most common way a filter test stops testing anything.
  @regression
  Scenario: The market page can be filtered by asset class
    Given the market page is open
    When the asset class filter is set to "Crypto"
    Then the market grid contains the instrument "BTCUSD"
    And the market grid does not contain the instrument "EURUSD"

  @regression
  Scenario: A filter that matches nothing shows the empty-state message
    Given the order history page is open
    When the order reference search is set to "NO-SUCH-REFERENCE"
    # Asserting the empty-state MESSAGE rather than "no rows exist". A positive assertion is
    # instant and stronger: it proves the application recognised the empty result and told the
    # user, rather than silently rendering nothing because a request failed.
    Then the no-orders message is shown

  @smoke
  Scenario: Order history shows the trader's seeded orders
    Given the order history page is open
    Then the order history grid contains the order "ORD-20240401-0001"
    And the order "ORD-20240401-0001" is shown as "Filled"
    And the order "ORD-20240401-0001" is shown with the symbol "EURUSD"

  @regression
  Scenario: Order history can be filtered by status
    Given the order history page is open
    When the status filter is set to "Pending"
    Then the order history grid contains the order "ORD-20240401-0002"
    And the order history grid does not contain the order "ORD-20240401-0001"

  # The displayed count and the total count are separate values and a common source of defects:
  # a client-side filter that forgets to update the total, or a total that counts only the
  # current page.
  @regression
  Scenario: The order history reports how many orders are shown
    Given the order history page is open
    Then the displayed order count matches the number of rows in the grid
