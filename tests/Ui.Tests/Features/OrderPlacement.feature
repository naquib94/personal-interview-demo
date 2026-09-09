@ui @orders
Feature: Placing an order through the web interface
  The end-to-end journey a customer actually takes: sign in, choose an instrument, place an
  order, and see it in their history.

  Note the Background. Signing in happens through the API rather than the browser, and that is
  a deliberate choice: driving the login form takes a few seconds of browser interaction per
  scenario and re-tests a rule already covered by Login.feature. It also means a defect in the
  sign-in page cannot fail an order-placement scenario, so a failure here points at the right
  screen. The one scenario that must exercise the form is in Login.feature, once.

  Background:
    Given the active trader is signed in through the API
    And the new order page is open

  @smoke
  Scenario: A valid market order is placed and appears in order history
    When the trader places a market order to buy 1 of "EURUSD" through the browser
    Then a success message confirms the order was placed
    And the order appears in the order history page
    # Closing the loop against the database, not against the screen. The UI showing a row only
    # proves the UI rendered a row.
    And the order is persisted against the trader's own account

  @regression
  Scenario: A limit order requires a limit price, and the field appears only for limit orders
    Then the limit price field is not shown
    When the trader selects the order type "Limit"
    Then the limit price field is shown
    When the trader completes a limit order to sell 2 of "GBPUSD" at 1.30000
    Then a success message confirms the order was placed

  # The UI guard and the API rule are separate defences and both need testing. The dropdown
  # disables a non-tradable instrument so the user cannot choose it; Api.Tests separately proves
  # the API still refuses the order if that guard is bypassed. Testing only one layer leaves the
  # other unverified.
  @regression
  Scenario: A non-tradable instrument cannot be selected
    Then the instrument "GOLD-SPOT" cannot be selected

  # Note the two separate assertions, and the distinction they preserve. The platform's error
  # envelope carries a general message ("The order was rejected.") and a per-field reason
  # ("Insufficient funds: the order requires..."). The user needs the second one to understand
  # what to change, so the UI must render the details and not just the summary.
  #
  # Collapsing this into one assertion on "the error message" is what the first version of this
  # scenario did, and it failed - correctly. The summary alone is not the requirement.
  @regression
  Scenario: A rejected order shows the specific reason, not just a generic failure
    When the trader places a market order to buy 20 of "XAUUSD" through the browser
    Then an error is shown on the new order page
    And the error message mentions "rejected"
    And a rejection detail mentions "Insufficient funds"

  @regression
  Scenario: Submitting the form with no quantity reports the problem
    When the trader submits the order form with no quantity
    Then an error is shown on the new order page
    And the error message mentions "invalid"
