using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QaFramework.Mobile.Simulation;

/// <summary>
/// An in-memory application: screens, elements, and what happens when they are tapped.
/// </summary>
/// <remarks>
/// The state machine behind <see cref="SimulatedMobileDriver"/>. It is deliberately a tiny
/// interpreter over a JSON fixture rather than C# stubs, and the reason is that a stub written in
/// C# tends to grow into a second implementation of the product's rules, maintained by the QA
/// team, believed by nobody. A declarative fixture keeps the simulation obviously shallow: the
/// whole behaviour is visible in one readable file, so no reader can mistake it for a model of
/// the real application.
/// <para>
/// The interpreter supports exactly what the framework's wiring needs to be demonstrated -
/// presence, text, text entry, conditional navigation and list growth - and nothing more. Every
/// feature not in that list is a feature that would make the fixture look more capable than it is.
/// </para>
/// </remarks>
public sealed class SimulatedApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,

        // Comments and trailing commas are allowed because this fixture is documentation as much
        // as data: a reader has to be able to tell from the file why a rule exists. A format that
        // forbids comments would push those explanations into a separate document, which is where
        // explanations go to die.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // Without this, System.Text.Json expects enum values as integers, so "condition":
        // "EqualTo" fails to bind. Numeric conditions in a fixture would be unreadable, which
        // defeats the purpose of the file being declarative.
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Matches <c>{reference}</c> and <c>{text:some-selector}</c> in a template.</summary>
    private static readonly Regex TemplatePlaceholder =
        new(@"\{(?<kind>reference|text):?(?<argument>[^}]*)\}", RegexOptions.Compiled);

    private readonly Dictionary<string, List<SimulatedElement>> screens;
    private readonly IReadOnlyDictionary<string, string> tokens;
    private int referenceCounter;

    private SimulatedApp(
        Dictionary<string, List<SimulatedElement>> screens,
        string initialScreen,
        IReadOnlyDictionary<string, string> tokens)
    {
        this.screens = screens;
        this.tokens = tokens;
        CurrentScreen = initialScreen;
    }

    /// <summary>The screen currently on display. Only its elements are considered visible.</summary>
    public string CurrentScreen { get; private set; }

    /// <summary>
    /// The last order reference the fixture generated, if any.
    /// </summary>
    /// <remarks>
    /// Exposed because a scenario needs to correlate "the order I placed" with "the row in the
    /// history", and the alternative - parsing the reference back out of a screen - is the sort of
    /// indirection that makes a step definition unreadable.
    /// </remarks>
    public string? LastGeneratedReference { get; private set; }

    /// <summary>Loads a fixture from disk.</summary>
    /// <param name="path">Path to the fixture, normally under the test output directory.</param>
    /// <param name="tokens">Values the fixture refers to by name. See <see cref="SimulationTokens"/>.</param>
    public static SimulatedApp LoadFromFile(
        string path, IReadOnlyDictionary<string, string>? tokens = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"The simulation fixture '{path}' does not exist. Fixtures live in " +
                "QaFramework.Mobile/Simulation/Fixtures and are copied to the output directory; a " +
                "missing file usually means the project was not rebuilt.", path);

        return Parse(File.ReadAllText(path), tokens);
    }

    /// <summary>
    /// Builds an application from fixture JSON.
    /// </summary>
    /// <remarks>
    /// Public and string-based so that a unit test can define a two-element application inline
    /// instead of committing a file per test case.
    /// </remarks>
    public static SimulatedApp Parse(string json, IReadOnlyDictionary<string, string>? tokens = null)
    {
        FixtureDocument document =
            JsonSerializer.Deserialize<FixtureDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("The simulation fixture deserialised to null.");

        if (document.Screens.Count == 0)
            throw new InvalidOperationException("The simulation fixture defines no screens.");

        Dictionary<string, List<SimulatedElement>> screens = new(StringComparer.Ordinal);

        foreach (ScreenDefinition screen in document.Screens)
        {
            if (string.IsNullOrWhiteSpace(screen.Name))
                throw new InvalidOperationException("A screen in the fixture has no name.");

            if (screens.ContainsKey(screen.Name))
                throw new InvalidOperationException(
                    $"The fixture defines the screen '{screen.Name}' twice.");

            screens[screen.Name] = screen.Elements.Select(SimulatedElement.From).ToList();
        }

        // Shared elements are applied after the screens exist, so navigation chrome is declared
        // once instead of being copied into every screen. Copy-pasted fixture blocks drift in
        // exactly the way copy-pasted page objects do.
        foreach (SharedElementGroup group in document.SharedElements)
        {
            foreach (string target in group.AppliesToScreens)
            {
                if (!screens.TryGetValue(target, out List<SimulatedElement>? elements))
                    throw new InvalidOperationException(
                        $"A shared element group refers to the unknown screen '{target}'.");

                elements.AddRange(group.Elements.Select(SimulatedElement.From));
            }
        }

        string initial = string.IsNullOrWhiteSpace(document.InitialScreen)
            ? document.Screens[0].Name
            : document.InitialScreen;

        if (!screens.ContainsKey(initial))
            throw new InvalidOperationException(
                $"The fixture's initial screen '{initial}' is not one of the screens it defines.");

        return new SimulatedApp(
            screens, initial, tokens ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every element on the current screen, visible or not.
    /// </summary>
    /// <remarks>
    /// Exposed for the page-source capture, which must show hidden elements too: "the element was
    /// there but invisible" and "the element was never rendered" are different defects, and a
    /// capture that cannot distinguish them wastes the reader's time.
    /// </remarks>
    public IReadOnlyList<SimulatedElement> ElementsOnCurrentScreen => screens[CurrentScreen];

    /// <summary>Whether an element matching the selector is on the current screen.</summary>
    public bool IsOnCurrentScreen(string selector) => FindOnCurrentScreen(selector) is not null;

    /// <summary>The element on the current screen, or null when absent.</summary>
    public SimulatedElement? FindOnCurrentScreen(string selector) =>
        screens[CurrentScreen].FirstOrDefault(element => element.Matches(selector));

    /// <summary>
    /// The element wherever it lives in the application.
    /// </summary>
    /// <remarks>
    /// Needed because an action on one screen can legitimately change another - placing an order
    /// adds a row to the history the user has not navigated to yet. That is how the real
    /// application behaves, and a simulation that could only mutate the visible screen would make
    /// the most interesting scenario in the suite untestable.
    /// </remarks>
    public SimulatedElement? FindAnywhere(string selector) =>
        screens.Values
            .SelectMany(elements => elements)
            .FirstOrDefault(element => element.Matches(selector));

    /// <summary>Reads an element's text, or throws the exception a wait treats as transient.</summary>
    public string GetText(string selector) =>
        FindOnCurrentScreen(selector)?.Text
        ?? throw NotOnScreen(selector);

    /// <summary>Reads a list element's rows, or the single text of a non-list element.</summary>
    public IReadOnlyList<string> GetTexts(string selector)
    {
        SimulatedElement element = FindOnCurrentScreen(selector) ?? throw NotOnScreen(selector);

        return element.IsList ? element.Items : [element.Text];
    }

    /// <summary>Replaces the text of an input.</summary>
    public void EnterText(string selector, string text)
    {
        SimulatedElement element = FindOnCurrentScreen(selector) ?? throw NotOnScreen(selector);

        if (!element.IsInput)
            throw new InvalidOperationException(
                $"The simulated element '{selector}' is not an input, so text cannot be entered " +
                "into it. On a device this would be a driver error; the fixture reproduces the " +
                "same failure so that a locator pointing at the wrong element fails the same way.");

        element.Text = text;
    }

    /// <summary>Taps an element and applies the first behaviour rule whose conditions hold.</summary>
    public void Tap(string selector)
    {
        SimulatedElement element = FindOnCurrentScreen(selector) ?? throw NotOnScreen(selector);

        if (element.OnTap is null)
            return;

        foreach (TapRule rule in element.OnTap.Rules)
        {
            if (!rule.When.All(Holds))
                continue;

            Apply(rule);
            return;
        }

        // No matching rule is not an error: most elements do nothing when tapped, which is also
        // true of the real application.
    }

    private void Apply(TapRule rule)
    {
        // The reference is generated once per rule application, so the value written to the
        // confirmation element and the value appended to the history are necessarily the same.
        // Generating it lazily per substitution would produce two references and a scenario that
        // fails for a reason invented by the test double.
        string? reference = null;

        string Expand(string template) =>
            TemplatePlaceholder.Replace(template, match => match.Groups["kind"].Value switch
            {
                "reference" => reference ??= NextReference(),
                "text" => FindAnywhere(match.Groups["argument"].Value)?.Text
                          ?? throw new InvalidOperationException(
                              $"The fixture template refers to the unknown element " +
                              $"'{match.Groups["argument"].Value}'."),
                _ => match.Value
            });

        // Order of operations is deliberate and it is the sort of thing that is only obvious once
        // it has gone wrong: assignments and the list append both read other elements' text, so
        // they must happen before any clearing. Running clearTexts first - the order the class
        // originally used - appended a row with an empty quantity, because the form reset had
        // already emptied the field the template was reading.
        foreach (KeyValuePair<string, string> assignment in rule.SetTexts)
            Require(assignment.Key).Text = Expand(assignment.Value);

        foreach (string selector in rule.Show)
            Require(selector).Visible = true;

        foreach (string selector in rule.Hide)
            Require(selector).Visible = false;

        if (rule.AppendToList is not null)
        {
            SimulatedElement list = Require(rule.AppendToList.Selector);

            if (!list.IsList)
                throw new InvalidOperationException(
                    $"The fixture appends to '{rule.AppendToList.Selector}', which is not " +
                    "declared as a list. Add \"isList\": true to that element.");

            list.Items.Add(Expand(rule.AppendToList.Template));
        }

        // Clearing happens last, after everything that reads text. See the note above.
        foreach (string selector in rule.ClearTexts)
            Require(selector).Text = string.Empty;

        if (!string.IsNullOrWhiteSpace(rule.NavigateTo))
        {
            if (!screens.ContainsKey(rule.NavigateTo))
                throw new InvalidOperationException(
                    $"The fixture navigates to the unknown screen '{rule.NavigateTo}'.");

            CurrentScreen = rule.NavigateTo;
        }

        if (reference is not null)
            LastGeneratedReference = reference;
    }

    private bool Holds(TapCondition condition)
    {
        string actual = FindAnywhere(condition.Selector)?.Text
                        ?? throw new InvalidOperationException(
                            $"A fixture condition refers to the unknown element " +
                            $"'{condition.Selector}'.");

        string expected = Resolve(condition.Value);

        return condition.Condition switch
        {
            ConditionKind.EqualTo => string.Equals(actual, expected, StringComparison.Ordinal),
            ConditionKind.NotEqualTo => !string.Equals(actual, expected, StringComparison.Ordinal),
            ConditionKind.Empty => string.IsNullOrWhiteSpace(actual),
            ConditionKind.NotEmpty => !string.IsNullOrWhiteSpace(actual),
            _ => throw new InvalidOperationException(
                $"Unhandled fixture condition '{condition.Condition}'.")
        };
    }

    /// <summary>
    /// Substitutes a <c>$token</c> reference with the value the suite supplied.
    /// </summary>
    /// <remarks>
    /// Unknown tokens throw. The alternative - treating an unknown token as a literal - would
    /// mean a typo in a fixture silently compares the entered password against the string
    /// "$expectedPasword", which no user could ever type, so the negative scenario would pass and
    /// the positive one would fail with no clue as to why.
    /// </remarks>
    private string Resolve(string value)
    {
        if (!value.StartsWith('$'))
            return value;

        string name = value[1..];

        return tokens.TryGetValue(name, out string? resolved) && !string.IsNullOrWhiteSpace(resolved)
            ? resolved
            : throw new InvalidOperationException(
                $"The simulation fixture refers to the token '{name}' but the suite supplied no " +
                $"value for it. Known tokens: " +
                $"{(tokens.Count == 0 ? "none" : string.Join(", ", tokens.Keys.Order()))}. " +
                "Tokens exist so that fixtures never contain credentials; an unresolved one is a " +
                "hard failure rather than an empty comparison.");
    }

    private SimulatedElement Require(string selector) =>
        FindAnywhere(selector)
        ?? throw new InvalidOperationException(
            $"A fixture rule refers to the unknown element '{selector}'.");

    /// <summary>
    /// Deterministic references, in the demo application's own format.
    /// </summary>
    /// <remarks>
    /// Deliberately sequential rather than random. A deterministic double makes a failure
    /// reproducible, and there is no scenario here whose value depends on unpredictability -
    /// where a test does need unpredictable data it uses
    /// <c>QaFramework.Core.TestData.DataGenerator</c>, which is the right tool for that job.
    /// </remarks>
    private string NextReference() =>
        $"ORD-SIM-{(++referenceCounter).ToString("D4", CultureInfo.InvariantCulture)}";

    private KeyNotFoundException NotOnScreen(string selector) =>
        new($"No element matching '{selector}' is on the simulated screen '{CurrentScreen}'. " +
            $"Elements on this screen: " +
            $"{string.Join(", ", screens[CurrentScreen].Select(element => element.Selector))}.");
}
