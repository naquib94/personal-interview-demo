@mobile
Feature: Signing in to the mobile trading app
    As a trader using the mobile app
    I want my credentials checked before I reach my account
    So that nobody else can see my positions or place orders in my name

    # Users are named by role, never by user name and password. The credential lives in one place
    # per environment, so rotating it is a settings change rather than a search across features.
    #
    # Every scenario below runs against the simulated target in CI: no device, no emulator, no
    # Appium server. A pass therefore demonstrates that the framework is wired correctly, not that
    # a real application signs anybody in. The same feature file runs unchanged against a device.

    Background:
        Given the mobile trading app is open

    @smoke
    Scenario: An active trader reaches their account
        When an active trader signs in
        Then their account summary is shown
        And the summary identifies them as the trader who signed in

    @regression
    Scenario: An incorrect password is refused
        When an active trader signs in with an incorrect password
        Then sign-in is refused with the message "Sign in failed. Check your username and password."
        And their account summary is not shown
