using System.Text.RegularExpressions;
using Framework.Tests.Support;
using QaFramework.Core.TestData;

namespace Framework.Tests.TestData;

/// <summary>
/// Tests for the reproducibility and uniqueness guarantees of <see cref="DataGenerator"/>.
/// </summary>
/// <remarks>
/// Both guarantees fail silently. Data that is not unique produces collisions that look like
/// product defects and only appear under parallel load; data that is not reproducible produces
/// the worst class of failure there is - one that cannot be replayed, and therefore cannot be
/// triaged or proven fixed. Neither shows up as a red test in the suite that depends on them.
/// </remarks>
[TestFixture]
public sealed class DataGeneratorTests
{
    [Test]
    public void TwoGeneratorsWithTheSameSeed_ProduceIdenticalSequences()
    {
        // The property that makes QA_DATA_SEED useful. It only holds because the generator keeps
        // one Faker for its lifetime; constructing a Faker per call - the obvious-looking
        // implementation - reseeds it each time and destroys reproducibility while every
        // individual value still looks perfectly random.
        DataGenerator first = new(seed: 12_345);
        DataGenerator second = new(seed: 12_345);

        using (new AssertionScope())
        {
            Sequence(first).Should().Equal(Sequence(second));
            first.Seed.Should().Be(12_345);
        }
    }

    [Test]
    public void TwoGeneratorsWithDifferentSeeds_ProduceDifferentSequences()
    {
        // The counterpart. A generator that ignored its seed would pass the test above
        // perfectly, so reproducibility on its own proves nothing.
        DataGenerator first = new(seed: 1);
        DataGenerator second = new(seed: 2);

        Sequence(first).Should().NotEqual(Sequence(second));
    }

    [Test]
    public void UniqueIdentifier_IsUniqueAcrossManyCalls()
    {
        // Uniqueness is asserted at volume because the counter is the part that could fail: a
        // non-incrementing or non-atomic counter yields duplicates only occasionally, which
        // surfaces as an intermittent constraint violation attributed to the product.
        DataGenerator generator = new(seed: 99);

        List<string> identifiers = Enumerable.Range(0, 2_000).Select(_ => generator.UniqueIdentifier()).ToList();

        identifiers.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void UniqueIdentifier_CarriesTheGreppablePrefix()
    {
        // The prefix is a contract with the cleanup queries in database/validation, which find
        // automation-created rows by it. Changing the format silently orphans every row those
        // queries were written to remove.
        DataGenerator generator = new(seed: 7);

        string identifier = generator.UniqueIdentifier();

        identifier.Should().StartWith($"{DataGenerator.Prefix}-");
        Regex.IsMatch(identifier, "^QA-[0-9A-F]{6}-[0-9]{4}$").Should().BeTrue(
            "the cleanup queries match on this exact shape, so it is a contract rather than cosmetic");
    }

    [Test]
    public void UniqueIdentifier_SharesOneRunSegmentWithinAGenerator()
    {
        // The shared middle segment is what lets one query remove exactly one run's data. If it
        // varied per call, cleanup would have to enumerate every identifier the run produced.
        DataGenerator generator = new(seed: 7);

        IEnumerable<string> segments = Enumerable.Range(0, 5)
            .Select(_ => generator.UniqueIdentifier().Split('-')[1]);

        segments.Distinct().Should().HaveCount(1);
    }

    [Test]
    public void TwoGeneratorsWithTheSameSeed_StillProduceDifferentIdentifiers()
    {
        // Reproducibility and uniqueness pull in opposite directions, and this is where the
        // tension is resolved: the run segment is a GUID, not seeded. Deriving it from the seed
        // would look tidier and would make two parallel workers replaying the same seed collide
        // on every single identifier.
        DataGenerator first = new(seed: 4_242);
        DataGenerator second = new(seed: 4_242);

        first.UniqueIdentifier().Should().NotBe(second.UniqueIdentifier());
    }

    [Test]
    public void EmailAddress_AlwaysUsesTheReservedInvalidDomain()
    {
        // Asserted over many values, not one, because a generator that picks a domain at random
        // would pass a single-value test. A test system that emails a real stranger is a real
        // incident, and .invalid is guaranteed by RFC 2606 never to resolve.
        DataGenerator generator = new(seed: 31);

        IEnumerable<string> addresses = Enumerable.Range(0, 250).Select(_ => generator.EmailAddress());

        addresses.Should().OnlyContain(address => address.EndsWith("@example.invalid"));
    }

    [Test]
    public void Username_IsPrefixedAndLowercase()
    {
        // Lower case because usernames are frequently compared case-sensitively somewhere in a
        // stack, and a generator that emits mixed case makes that inconsistency intermittent.
        DataGenerator generator = new(seed: 31);

        IEnumerable<string> usernames = Enumerable.Range(0, 100).Select(_ => generator.Username()).ToList();

        usernames.Should().OnlyContain(name => name.StartsWith("qa.") && name == name.ToLowerInvariant());
    }

    [Test]
    public void QuantityBetween_StaysWithinBoundsAndIsRoundedToTwoDecimalPlaces()
    {
        // Two decimal places is not cosmetic: an unrounded value survives neither a round trip
        // through a SQLite REAL column nor a JSON number, so a quantity asserted as 1.2300000001
        // fails an equality check that has nothing wrong with it. Checked over many values
        // because a rounding bug that only bites on some inputs is the usual kind.
        DataGenerator generator = new(seed: 55);

        List<decimal> quantities = Enumerable.Range(0, 500)
            .Select(_ => generator.QuantityBetween(1.5m, 99.5m))
            .ToList();

        using (new AssertionScope())
        {
            quantities.Should().OnlyContain(quantity => quantity >= 1.5m && quantity <= 99.5m);
            quantities.Should().OnlyContain(quantity => quantity == Math.Round(quantity, 2));
        }
    }

    [Test]
    public void QuantityBetween_WithAnEqualMinimumAndMaximum_ReturnsThatValue()
    {
        // The degenerate range. A "between" helper that returned the minimum plus a fraction of
        // a zero-width range would drift off the boundary, and boundary values are exactly what
        // a test asking for one wants.
        DataGenerator generator = new(seed: 55);

        generator.QuantityBetween(10m, 10m).Should().Be(10m);
    }

    [Test]
    public void OneOf_OnlyEverReturnsAnOfferedOption()
    {
        // Cheap, and it protects against an off-by-one in the index arithmetic that would
        // otherwise appear as an occasional out-of-range exception rather than as bad data.
        DataGenerator generator = new(seed: 8);
        string[] options = ["BUY", "SELL"];

        IEnumerable<string> picks = Enumerable.Range(0, 200).Select(_ => generator.OneOf(options));

        picks.Should().OnlyContain(pick => options.Contains(pick));
    }

    /// <remarks>
    /// The seed tests are <see cref="NonParallelizableAttribute"/> individually because
    /// QA_DATA_SEED is process-global: another fixture constructing a DataGenerator while this
    /// one has the variable set would silently pick up this test's seed.
    /// </remarks>
    [Test]
    [NonParallelizable]
    public void Seed_HonoursTheEnvironmentVariable()
    {
        // This is the mechanism that makes a failed run reproducible: the seed is logged, and
        // setting it replays exactly the same data. If the variable were ignored, the suite
        // would still pass and the replay instructions in the README would quietly be a lie.
        using EnvironmentVariableScope environment = new();
        environment.Set("QA_DATA_SEED", "20250101");

        DataGenerator generator = new();

        generator.Seed.Should().Be(20_250_101);
    }

    [Test]
    [NonParallelizable]
    public void Seed_PrefersAnExplicitSeedOverTheEnvironmentVariable()
    {
        // A test that pins its own seed - because it depends on a specific generated value - must
        // not have that pin overridden by a replay variable, or the replay breaks the very tests
        // it was set to investigate.
        using EnvironmentVariableScope environment = new();
        environment.Set("QA_DATA_SEED", "20250101");

        DataGenerator generator = new(seed: 777);

        generator.Seed.Should().Be(777);
    }

    [Test]
    [NonParallelizable]
    public void Seed_WithAMalformedEnvironmentVariable_FallsBackWithoutFailingTheRun()
    {
        // A deliberate asymmetry with QA_ENVIRONMENT, which fails hard on a bad value. A wrong
        // environment runs the suite against the wrong system; a wrong seed only costs
        // reproducibility for that one run. Bringing the whole suite down over it would be a
        // worse trade, so this asserts the tolerant behaviour on purpose.
        using EnvironmentVariableScope environment = new();
        environment.Set("QA_DATA_SEED", "not-a-number");

        Action construct = () => _ = new DataGenerator();

        construct.Should().NotThrow();
    }

    [Test]
    [NonParallelizable]
    public void Seed_WithNoEnvironmentVariable_IsStillRecorded()
    {
        // The seed is time-derived when nothing sets it, so successive local runs exercise
        // different data - but it must always be readable, because an unrecorded seed is what
        // makes a random-data failure untriageable.
        using EnvironmentVariableScope environment = new();
        environment.Clear("QA_DATA_SEED");

        DataGenerator generator = new();

        generator.Seed.Should().NotBe(0);
    }

    /// <summary>
    /// A deterministic sample across every generator that draws on the seeded randomiser.
    /// </summary>
    /// <remarks>
    /// UniqueIdentifier is excluded on purpose: it is intentionally not reproducible, and
    /// including it would make this helper assert the opposite of what it is for.
    /// </remarks>
    private static IReadOnlyList<string> Sequence(DataGenerator generator) =>
    [
        generator.Username(),
        generator.EmailAddress(),
        generator.QuantityBetween(1m, 1_000m).ToString(System.Globalization.CultureInfo.InvariantCulture),
        generator.OneOf("BUY", "SELL", "HOLD"),
        generator.Username()
    ];
}
