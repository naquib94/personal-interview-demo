namespace QaFramework.Mobile.Simulation;

/// <summary>
/// One element in the simulated application.
/// </summary>
/// <remarks>
/// Mutable, unlike almost everything else in this framework, because it models device state:
/// entering text changes it, and an assertion afterwards must see the change. Immutability here
/// would mean rebuilding the whole tree per interaction for no benefit.
/// </remarks>
public sealed class SimulatedElement
{
    private SimulatedElement(ElementDefinition definition)
    {
        Selector = definition.Selector;
        IosSelector = definition.IosSelector;
        Text = definition.Text;
        Visible = definition.Visible;
        IsInput = definition.IsInput;
        IsList = definition.IsList;
        OnTap = definition.OnTap;
        Items = [.. definition.Items];
    }

    /// <summary>The accessibility identifier, as it appears on Android.</summary>
    public string Selector { get; }

    /// <summary>
    /// The iOS identifier, when it differs.
    /// </summary>
    /// <remarks>
    /// Almost always empty, because accessibility identifiers are shared across platforms - which
    /// is the whole argument for preferring them. It exists so that the fixture can exercise the
    /// platform-pair half of <see cref="Elements.MobileLocator"/> rather than leaving that code
    /// path unexecuted.
    /// </remarks>
    public string IosSelector { get; }

    /// <summary>The element's current text.</summary>
    public string Text { get; set; }

    /// <summary>Whether the element is currently visible on its screen.</summary>
    public bool Visible { get; set; }

    /// <summary>Whether text can be entered into it.</summary>
    public bool IsInput { get; }

    /// <summary>Whether it holds a collection of rows rather than a single value.</summary>
    public bool IsList { get; }

    /// <summary>Rows, for a list element.</summary>
    public List<string> Items { get; }

    /// <summary>What happens when it is tapped, if anything.</summary>
    public TapBehaviour? OnTap { get; }

    /// <summary>Whether this element answers to the given platform selector.</summary>
    public bool Matches(string selector) =>
        string.Equals(Selector, selector, StringComparison.Ordinal)
        || (!string.IsNullOrEmpty(IosSelector)
            && string.Equals(IosSelector, selector, StringComparison.Ordinal));

    internal static SimulatedElement From(ElementDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Selector))
            throw new InvalidOperationException(
                "An element in the simulation fixture has no selector.");

        return new SimulatedElement(definition);
    }
}
