@api @orders
Feature: Order history
  A trader must be able to see their own orders, and only their own.

  Background:
    Given the active trader is signed in

  @smoke
  Scenario: Order history is returned for the signed-in trader
    When the trader requests their order history
    Then the order history is returned
    And every order in the history belongs to the trader

  @regression
  Scenario Outline: Order history can be filtered by status
    When the trader requests their order history filtered by status "<status>"
    Then every order in the history has the status "<status>"

    Examples:
      | status  |
      | Filled  |
      | Pending |

  # Pagination is worth its own scenario because it is easy to implement wrongly in a way that
  # no single-page test notices: a total count that reflects only the current page, or an offset
  # that skips a record between pages.
  @regression
  Scenario: Order history is paginated
    When the trader requests page 1 of their order history with a page size of 2
    Then at most 2 orders are returned
    And the reported total count exceeds the number of orders on the page
    And the reported page size is 2

  # This is the authorisation test that matters most. An order reference is guessable in
  # sequence, so the platform must not serve one trader's order to another. It is asserted as a
  # 404 rather than a 403 deliberately: a 403 confirms the reference exists, which is itself an
  # information leak.
  @smoke @security
  Scenario: A trader cannot read another trader's order
    Given the second trader has placed a market order to buy 1 of "EURUSD"
    When the active trader requests that order by reference
    Then the request is refused as not found

  @regression
  Scenario: An unknown order reference is reported as not found
    When the trader requests the order "ORD-19990101-NOSUCH"
    Then the request is refused as not found

  @contract
  Scenario: The order history response matches its published contract
    When the trader requests their order history
    Then the order history response matches the order page schema
