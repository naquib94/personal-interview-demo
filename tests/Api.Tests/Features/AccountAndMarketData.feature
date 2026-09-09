@api
Feature: Account and market information
  Reference and account data underpins every other function. It is read far more often than it
  is written, which makes it a high-frequency, low-complexity area: cheap to cover and
  disproportionately damaging when broken.

  @smoke
  Scenario: A trader can retrieve their own account
    Given the active trader is signed in
    When the trader requests their account
    Then the account details are returned
    And the account belongs to the signed-in trader

  # A real 404 path from the seeded data rather than a contrived one. analyst.readonly is an
  # active user with no trading account, so this exercises the genuine "no account" branch.
  @regression
  Scenario: A user with no trading account is told so
    Given the user without an account is signed in
    When the trader requests their account
    Then the request is refused as not found

  @smoke @security
  Scenario: Account details are not readable without a session
    When an unauthenticated caller requests an account
    Then the request is refused as unauthorised

  @smoke
  Scenario: The instrument list is available
    When the instrument list is requested
    Then at least 1 instrument is returned
    And every instrument has a non-negative spread

  @regression
  Scenario Outline: The instrument list can be filtered by asset class
    When the instrument list is requested for the asset class "<assetClass>"
    Then every returned instrument belongs to the asset class "<assetClass>"

    Examples:
      | assetClass |
      | FX         |
      | Metal      |
      | Crypto     |
      | Index      |

  @regression
  Scenario: An unknown instrument is reported as not found
    When the instrument "NOSUCHPAIR" is requested
    Then the request is refused as not found

  @contract
  Scenario: The instrument list matches its published contract
    When the instrument list is requested
    Then the instrument list response matches the instrument list schema

  @contract
  Scenario: The account response matches its published contract
    Given the active trader is signed in
    When the trader requests their account
    Then the account response matches the account schema
