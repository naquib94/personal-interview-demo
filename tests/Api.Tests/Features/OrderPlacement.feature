@api @orders
Feature: Placing an order
  Placing an order is the platform's most business-critical operation: it moves money, and a
  defect here is expensive and immediately visible to customers. It therefore carries the
  heaviest coverage in the suite - see TEST_STRATEGY.md for the prioritisation reasoning.

  Background:
    Given the active trader is signed in

  # ----------------------------------------------------------------------------------------
  # Happy paths
  # ----------------------------------------------------------------------------------------

  @smoke
  Scenario: A valid market order is accepted and filled immediately
    When the trader places a market order to buy 1 of "EURUSD"
    Then the order is accepted
    And the order status is "Filled"
    # Verified against the database rather than the API response. The API saying "Filled" only
    # proves the API said so; this proves a row exists, attributed to the right account and
    # instrument. See database/validation/qa-validation-queries.sql, query A1.
    And the order is persisted against the trader's own account
    And the order appears in the trader's order history

  @regression
  Scenario: A valid limit order rests as pending
    When the trader places a limit order to sell 2 of "GBPUSD" at 1.30000
    Then the order is accepted
    And the order status is "Pending"
    And the order is persisted against the trader's own account

  # ----------------------------------------------------------------------------------------
  # Field validation - a malformed request. Answered with 400.
  # ----------------------------------------------------------------------------------------

  @regression
  Scenario: Every invalid field is reported at once
    When the trader submits an order with no symbol, side "Hold" and quantity 0
    Then the order is rejected as a bad request
    # Asserting all three at once is the actual requirement. A test that only checked "an error
    # was returned" would pass even if the API regressed to reporting one field at a time, which
    # is a real usability regression for anyone building a client against it.
    And the validation errors mention the fields "symbol, side, quantity"

  @regression
  Scenario Outline: An order type must be consistent with the limit price
    When the trader submits a "<orderType>" order for "EURUSD" with quantity 1 and limit price "<limitPrice>"
    Then the order is rejected as a bad request
    And the validation error mentions the field "limitPrice"

    Examples:
      | orderType | limitPrice | note                                |
      | Limit     | null       | a limit order needs a price         |
      | Market    | 1.05       | a market order must not specify one |

  # ----------------------------------------------------------------------------------------
  # Business rules - a well-formed request the business refuses. Answered with 422.
  # ----------------------------------------------------------------------------------------
  # The 400/422 distinction is asserted deliberately. It is the difference between "you sent me
  # nonsense" and "I understood you and the answer is no", and clients need to handle them
  # differently: one is a bug in the caller, the other is a message for the user.

  @regression
  Scenario: An order for a non-tradable instrument is refused
    When the trader places a market order to buy 1 of "GOLD-SPOT"
    Then the order is refused by the business rules
    And the rejection reason mentions that the instrument is not tradable
    And no order is persisted

  @regression
  Scenario Outline: Quantity must respect the instrument's tradable range
    When the trader places a market order to buy <quantity> of "<symbol>"
    Then the order is refused by the business rules
    And the rejection reason mentions the permitted quantity range

    Examples:
      | symbol | quantity | boundary                          |
      | BTCUSD | 2.01     | just above the maximum of 2       |
      | BTCUSD | 0.009    | just below the minimum of 0.01    |

  # The boundary values themselves must be ACCEPTED. Testing only the failing side of a boundary
  # is the most common way an off-by-one gets through: a rule of "quantity > max" and one of
  # "quantity >= max" both reject 2.01, and only this scenario tells them apart.
  #
  # EURUSD is used rather than BTCUSD, and the reason is a genuine test-design trap worth
  # recording. BTCUSD's maximum quantity is 2, but 2 units cost roughly 122,000 against a
  # 25,000 balance - so the insufficient-funds rule fires first and the order is rejected for a
  # reason unrelated to the boundary. The test would have failed while the boundary logic was
  # perfectly correct.
  #
  # EURUSD's maximum of 50 units costs about 54, so the funds rule cannot interfere and the
  # scenario isolates exactly one rule. Choosing data that isolates the rule under test is the
  # difference between a test that proves something and one that merely goes red.
  @regression
  Scenario Outline: The exact boundary quantities are accepted
    When the trader places a market order to buy <quantity> of "EURUSD"
    Then the order is accepted

    Examples:
      | quantity | boundary                              |
      | 0.01     | the exact minimum permitted quantity  |
      | 50       | the exact maximum permitted quantity  |

  @regression
  Scenario: An order larger than the account balance is refused
    When the trader places a market order to buy 20 of "XAUUSD"
    Then the order is refused by the business rules
    And the rejection reason mentions insufficient funds
    And no order is persisted

  # ----------------------------------------------------------------------------------------
  # Security
  # ----------------------------------------------------------------------------------------

  @smoke @security
  Scenario: An unauthenticated request cannot place an order
    When an unauthenticated caller attempts to place a market order to buy 1 of "EURUSD"
    Then the request is refused as unauthorised

  @regression @security
  Scenario: A tampered token cannot place an order
    When a caller with a tampered token attempts to place a market order to buy 1 of "EURUSD"
    Then the request is refused as unauthorised

  @contract
  Scenario: The order response matches its published contract
    When the trader places a market order to buy 1 of "EURUSD"
    Then the order response matches the order schema
