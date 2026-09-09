using Bogus;

namespace QaFramework.Core.TestData;

/// <summary>
/// Generates unique, reproducible test data.
/// </summary>
/// <remarks>
/// Two properties, and the second is the one usually missing:
/// <list type="number">
/// <item><b>Unique.</b> Data collides when tests run in parallel, and a collision produces a
/// failure that looks like a product defect. Generated values carry a run-scoped prefix so
/// they cannot clash with each other or with seeded data.</item>
/// <item><b>Reproducible.</b> The seed is fixed per run and <i>logged</i>. Random data with an
/// unlogged seed produces the worst class of failure there is: one that cannot be reproduced,
/// and therefore cannot be triaged or proven fixed. Setting <c>QA_DATA_SEED</c> to the value
/// from a failed run replays exactly the same data.</item>
/// </list>
/// </remarks>
public sealed class DataGenerator
{
    /// <summary>Prefix on every generated identifier, so automation-created data is greppable.</summary>
    public const string Prefix = "QA";

    private readonly Faker faker;

    public int Seed { get; }

    public DataGenerator(int? seed = null)
    {
        Seed = seed
            ?? (int.TryParse(Environment.GetEnvironmentVariable("QA_DATA_SEED"), out int fromEnv)
                ? fromEnv
                // Time-derived rather than fixed, so successive local runs exercise different
                // data - but recorded, so any single run can be replayed.
                : Environment.TickCount);

        // One Faker instance held for the lifetime of the generator. Constructing a new Faker
        // per call - a common mistake - reseeds it each time and destroys reproducibility.
        faker = new Faker("en") { Random = new Randomizer(Seed) };
    }

    /// <summary>
    /// A unique identifier of the form <c>QA-4F2A9C-0001</c>.
    /// </summary>
    /// <remarks>
    /// The run segment is shared by everything one run creates, and the counter makes each value
    /// unique within it. That structure is what allows the cleanup query in
    /// database/validation to find and remove exactly one run's data.
    /// </remarks>
    public string UniqueIdentifier() =>
        $"{Prefix}-{runSegment}-{Interlocked.Increment(ref counter):D4}";

    public string Username() => $"{Prefix.ToLowerInvariant()}.{faker.Internet.UserName().ToLowerInvariant()}";

    /// <summary>
    /// Uses the reserved <c>.invalid</c> TLD (RFC 2606), which is guaranteed never to resolve.
    /// Generating addresses at real domains risks a test system emailing a stranger - a small
    /// detail that has caused real incidents.
    /// </summary>
    public string EmailAddress() => $"{Prefix.ToLowerInvariant()}.{faker.Random.AlphaNumeric(8).ToLowerInvariant()}@example.invalid";

    /// <summary>
    /// A quantity inside a valid range, rounded to two decimal places so it survives a round
    /// trip through JSON and a REAL column without a floating-point surprise.
    /// </summary>
    public decimal QuantityBetween(decimal min, decimal max) =>
        Math.Round(min + (decimal)faker.Random.Double() * (max - min), 2);

    public T OneOf<T>(params T[] options) => faker.PickRandom(options);

    private readonly string runSegment = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    private int counter;
}
