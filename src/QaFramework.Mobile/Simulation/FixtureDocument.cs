namespace QaFramework.Mobile.Simulation;

// The fixture schema, as plain data.
//
// These types are the deserialisation target only: SimulatedApp reads them once and builds its
// own runtime state from them. Keeping "the shape of the file" and "the state of the running
// application" as separate types costs one small mapping and buys the ability to change either
// without touching the other - the same reason the configuration layer binds into POCOs rather
// than reading IConfiguration everywhere.
//
// Every collection defaults to empty rather than null. A fixture that omits an optional section
// is not an error, and null-checking six collections at every use site is the kind of noise that
// hides the one check that actually matters.

/// <summary>The root of a simulation fixture file.</summary>
public sealed class FixtureDocument
{
    /// <summary>Screen shown when the session starts. Defaults to the first screen declared.</summary>
    public string InitialScreen { get; init; } = string.Empty;

    /// <summary>The screens the application has.</summary>
    public List<ScreenDefinition> Screens { get; init; } = [];

    /// <summary>Elements added to several screens at once, such as navigation chrome.</summary>
    public List<SharedElementGroup> SharedElements { get; init; } = [];
}

/// <summary>A single screen.</summary>
public sealed class ScreenDefinition
{
    /// <summary>Identifier used by navigation rules. Kebab-case, matching the demo application's pages.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The elements on it.</summary>
    public List<ElementDefinition> Elements { get; init; } = [];
}

/// <summary>A group of elements applied to more than one screen.</summary>
public sealed class SharedElementGroup
{
    /// <summary>Screens that receive these elements.</summary>
    public List<string> AppliesToScreens { get; init; } = [];

    /// <summary>The elements to add.</summary>
    public List<ElementDefinition> Elements { get; init; } = [];
}

/// <summary>An element as declared in the fixture.</summary>
public sealed class ElementDefinition
{
    /// <summary>Accessibility identifier. Matches the demo application's <c>data-testid</c> values.</summary>
    public string Selector { get; init; } = string.Empty;

    /// <summary>iOS identifier, when it differs from <see cref="Selector"/>.</summary>
    public string IosSelector { get; init; } = string.Empty;

    /// <summary>Initial text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Whether it starts visible. Defaults to true, because most elements do and an opt-in would
    /// mean writing <c>"visible": true</c> on every line of every fixture.
    /// </summary>
    public bool Visible { get; init; } = true;

    /// <summary>Whether text can be entered into it.</summary>
    public bool IsInput { get; init; }

    /// <summary>Whether it is a collection of rows.</summary>
    public bool IsList { get; init; }

    /// <summary>Initial rows, for a list.</summary>
    public List<string> Items { get; init; } = [];

    /// <summary>What tapping it does.</summary>
    public TapBehaviour? OnTap { get; init; }
}

/// <summary>
/// The rules evaluated when an element is tapped.
/// </summary>
/// <remarks>
/// First match wins, so a rule with no conditions acts as the default branch and belongs last.
/// That ordering convention is what lets the sign-in button express "correct credentials go to
/// the account screen, anything else shows an error" in two rules and no negation.
/// </remarks>
public sealed class TapBehaviour
{
    /// <summary>Rules, in priority order.</summary>
    public List<TapRule> Rules { get; init; } = [];
}

/// <summary>One tap rule: conditions, then the state changes to apply.</summary>
public sealed class TapRule
{
    /// <summary>All of these must hold. An empty list always holds.</summary>
    public List<TapCondition> When { get; init; } = [];

    /// <summary>Screen to move to, if any.</summary>
    public string NavigateTo { get; init; } = string.Empty;

    /// <summary>
    /// Text to assign, keyed by selector. Values may contain <c>{reference}</c> for a freshly
    /// generated order reference or <c>{text:selector}</c> to copy another element's text.
    /// </summary>
    public Dictionary<string, string> SetTexts { get; init; } = [];

    /// <summary>Elements whose text is emptied - the fixture's equivalent of a form reset.</summary>
    public List<string> ClearTexts { get; init; } = [];

    /// <summary>Elements to make visible.</summary>
    public List<string> Show { get; init; } = [];

    /// <summary>Elements to hide.</summary>
    public List<string> Hide { get; init; } = [];

    /// <summary>A row to append to a list element.</summary>
    public ListAppend? AppendToList { get; init; }
}

/// <summary>A condition on an element's text.</summary>
public sealed class TapCondition
{
    /// <summary>The element to inspect. May be on any screen.</summary>
    public string Selector { get; init; } = string.Empty;

    /// <summary>The comparison to make.</summary>
    public ConditionKind Condition { get; init; } = ConditionKind.EqualTo;

    /// <summary>
    /// The value to compare against. A leading <c>$</c> makes it a token supplied by the suite -
    /// see <see cref="SimulationTokens"/> - which is how credentials stay out of committed files.
    /// </summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>Appends a templated row to a list element.</summary>
public sealed class ListAppend
{
    /// <summary>The list element to grow.</summary>
    public string Selector { get; init; } = string.Empty;

    /// <summary>Row template, using the same placeholders as <see cref="TapRule.SetTexts"/>.</summary>
    public string Template { get; init; } = string.Empty;
}

/// <summary>
/// The comparisons a fixture condition can make.
/// </summary>
/// <remarks>
/// Named <c>EqualTo</c> rather than <c>Equals</c>: an enum member called <c>Equals</c> hides
/// <see cref="object.Equals(object)"/> and earns a compiler warning, and this repository builds
/// clean.
/// </remarks>
public enum ConditionKind
{
    /// <summary>Text matches exactly. The default.</summary>
    EqualTo,

    /// <summary>Text does not match.</summary>
    NotEqualTo,

    /// <summary>Text is empty or whitespace.</summary>
    Empty,

    /// <summary>Text has content.</summary>
    NotEmpty
}
