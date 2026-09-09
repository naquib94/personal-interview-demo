# ==========================================================================================
# Authentication
# ==========================================================================================
# On how these scenarios are written. Each one describes a rule of the business, not a sequence
# of HTTP calls. "the sign-in attempt is refused" rather than "the response status is 401",
# because the rule is what a product owner can confirm and what survives a change of transport.
# The status code is asserted in the step definition, where it belongs.
#
# Tags carry meaning used by CI (see .github/workflows/ci.yml):
#   @smoke      - must pass on every pull request; the suite gate.
#   @regression - runs on the scheduled nightly build.
#   @security   - authentication and authorisation; also runs on every pull request.
#   @contract   - JSON Schema validation of the response shape.
# ==========================================================================================

@api @authentication
Feature: Signing in to the trading platform
  Access to account and trading functions requires a valid session, and the platform must
  distinguish between "these credentials are wrong" and "this account may not trade".

  @smoke
  Scenario: An active trader signs in successfully
    When the active trader signs in
    Then the sign-in succeeds
    And a session token is issued
    And the sign-in is recorded in the audit trail

  @smoke @security
  Scenario: Sign-in is refused when the password is wrong
    When someone attempts to sign in as the active trader with the password "not-the-password"
    Then the sign-in attempt is refused as unauthorised
    And the failure message does not reveal whether the username exists

  @security
  Scenario: Sign-in is refused for an unknown username
    When someone attempts to sign in as "no.such.user" with the password "irrelevant"
    Then the sign-in attempt is refused as unauthorised
    And the failure message does not reveal whether the username exists

  # A suspended account is a different rule from a wrong password: the credentials are correct,
  # but the account may not be used. Conflating the two would hide a genuine defect - a
  # suspended trader being allowed to place orders.
  @regression @security
  Scenario: A suspended trader is told their account is suspended
    When the suspended trader signs in
    Then the sign-in attempt is refused as forbidden
    And the failure message explains that the account is suspended

  # Boundary and negative input. Sent as a table so the same rule is verified across the whole
  # equivalence class rather than for one arbitrary example.
  #
  # The literal "null" is translated to a real null in the step definition. Gherkin has no null,
  # and the alternative - treating an empty cell as absent - would stop the suite distinguishing
  # "sent as an empty string" from "not sent at all". Those are different requests and an API is
  # entitled to answer them differently, so the distinction is worth preserving.
  @regression
  Scenario Outline: Sign-in requires both a username and a password
    When someone attempts to sign in with username "<username>" and password "<password>"
    Then the sign-in attempt is rejected as a bad request
    And the validation error mentions the field "<field>"

    Examples:
      | username    | password     | field    | case                        |
      |             | Demo!Pass123 | username | username sent as empty      |
      | trader.demo |              | password | password sent as empty      |
      | null        | null         | username | neither field sent at all   |

  @contract
  Scenario: The sign-in response matches its published contract
    When the active trader signs in
    Then the sign-in response matches the login schema
