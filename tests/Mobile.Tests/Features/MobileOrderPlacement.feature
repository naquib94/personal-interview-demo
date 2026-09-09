@mobile
Feature: Placing an order from the mobile app
    As an active trader
    I want an order I place to be acknowledged and to appear in my history
    So that I can trust what the app tells me about my own trading

    # Note what the scenarios do not say: no locators, no taps, no swipes, no screen names. A
    # scenario that narrates UI mechanics has to be rewritten when the UI changes, which is the
    # thing it was supposed to protect against.
    #
    # The order reference is not written into the Gherkin either. It is issued by the application
    # at run time and carried between steps in a scenario-scoped context object - so the feature
    # says "the order I just placed" and the step definitions do the correlating.

    Background:
        Given an active trader is signed in to the mobile app

    @smoke
    Scenario: A placed order is acknowledged and appears in the order history
        When they place a market order to buy 2 of "EURUSD"
        Then the order is acknowledged with a reference
        And the order appears in their order history

    @regression
    Scenario: An order with no quantity is rejected
        When they place an order without saying how much to trade
        Then the order is rejected with the message "Quantity is required."
        And no reference is issued
