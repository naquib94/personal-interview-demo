using QaFramework.Core.Configuration;
using QaFramework.Core.Synchronisation;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Screens.Trading;

/// <summary>
/// The order history list.
/// </summary>
public sealed class OrderHistoryScreen(IMobileDriver driver, TimeoutSettings timeouts)
    : BaseScreen(driver, timeouts)
{
    private static readonly MobileLocator Heading =
        MobileLocator.Shared("the order history heading", "order-history-heading");

    private static readonly MobileLocator Rows =
        MobileLocator.Shared("the order history rows", "order-history-grid-row");

    private static readonly MobileLocator EmptyState =
        MobileLocator.Shared("the empty order history message", "order-history-grid-empty");

    private static readonly MobileLocator Search =
        MobileLocator.Shared("the order history search field", "order-history-search-input");

    /// <inheritdoc />
    protected override MobileLocator Anchor => Heading;

    /// <inheritdoc />
    protected override string ScreenName => "the order history screen";

    /// <summary>Every row currently listed, as displayed.</summary>
    public Task<IReadOnlyList<string>> RowsAsync() => TextsAsync(Rows);

    /// <summary>Whether the empty-state message is shown.</summary>
    /// <remarks>
    /// Preferred over asserting that no rows exist. Asserting on absence costs the full absence
    /// timeout every time it passes; asserting that the empty-state message is present is
    /// instant and additionally checks that the application handles emptiness deliberately.
    /// </remarks>
    public Task<bool> IsEmptyStateShownAsync() => IsDisplayedAsync(EmptyState);

    /// <summary>Filters the list.</summary>
    public Task SearchForAsync(string term) => EnterTextAsync(Search, term);

    /// <summary>
    /// Waits until a row containing the reference appears, and returns it.
    /// </summary>
    /// <remarks>
    /// A poll rather than a single read, and this is the method that justifies the whole
    /// <see cref="Wait"/> helper existing. An order reaching a history list is eventually
    /// consistent in any real trading system: the write is acknowledged before the read model has
    /// caught up. A single read would be flaky, a fixed sleep would be slow and still flaky, and
    /// a poll with a named condition produces a message that says what was expected and what the
    /// list actually contained when it gave up.
    /// </remarks>
    public async Task<string> WaitForOrderAsync(string orderReference)
    {
        IReadOnlyList<string> rows = await Wait.ForValueAsync(
            RowsAsync,
            candidates => candidates.Any(
                row => row.Contains(orderReference, StringComparison.Ordinal)),
            $"order '{orderReference}' to appear in the order history",
            Timeouts.EventualConsistency,
            Timeouts.PollInterval);

        return rows.First(row => row.Contains(orderReference, StringComparison.Ordinal));
    }
}
