@ui @authentication
Feature: Signing in through the web interface
  The same authentication rules verified at the API level in Api.Tests must also hold through
  the browser. These scenarios exist to cover what the API tests cannot: that the page renders
  the outcome, that the user is taken somewhere sensible, and that an error is actually shown
  rather than swallowed.

  Deliberately NOT re-tested here: every permutation of invalid credentials. Those rules are
  already covered exhaustively and far more cheaply at the API level, and duplicating them
  through a browser would add minutes to the suite for no additional information. Choosing what
  NOT to automate at this level is the point - see TEST_STRATEGY.md.

  @smoke
  Scenario: An active trader signs in and reaches their account
    Given the sign-in page is open
    When the active trader signs in through the browser
    Then the account page is displayed
    And the account details are shown for the active trader

  @smoke
  Scenario: An error is shown when the password is wrong
    Given the sign-in page is open
    When someone signs in as the active trader with the password "not-the-password"
    Then an error is shown on the sign-in page
    And the error message reads "Invalid username or password."
    And the user remains on the sign-in page

  @regression
  Scenario: A suspended trader is told why they cannot proceed
    Given the sign-in page is open
    When the suspended trader signs in through the browser
    Then an error is shown on the sign-in page
    And the error message mentions "suspended"

  # Client-side validation is a separate concern from the API's validation. The browser submits
  # empty fields, and the API's field errors must be rendered as a list rather than collapsed
  # into one generic message - which is what the user actually needs in order to fix the form.
  @regression
  Scenario: Submitting an empty form reports both missing fields
    Given the sign-in page is open
    When the sign-in form is submitted with no credentials
    Then an error is shown on the sign-in page
    And 2 validation details are listed

  # An unauthenticated user navigating directly to a protected page must not see it. This is
  # only testable through the browser: it is client-side routing, invisible to the API suite.
  @smoke @security
  Scenario Outline: Protected pages redirect an unauthenticated visitor to sign in
    When an unauthenticated visitor navigates directly to "<page>"
    Then the sign-in page is displayed

    Examples:
      | page            |
      | account.html    |
      | order-new.html  |
      | orders.html     |
