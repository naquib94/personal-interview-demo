namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Picks the factory for a platform.
/// </summary>
/// <remarks>
/// Small, but it earns its place. Without it, every caller that needs a session writes its own
/// <c>switch</c> on <see cref="Platform"/>, and adding a platform means finding all of them - the
/// standard way a framework acquires a third copy of a decision it should have made once.
/// <para>
/// Takes its factories as a constructor argument rather than newing them up, so a test can
/// resolve a stub factory and assert on what was requested. <see cref="Default"/> supplies the
/// production set for the common case, which keeps the injection from becoming ceremony at every
/// call site.
/// </para>
/// </remarks>
public sealed class MobileDriverFactoryResolver
{
    private readonly Dictionary<Platform, IMobileDriverFactory> factories;

    /// <summary>Creates a resolver over the given factories.</summary>
    /// <exception cref="ArgumentException">
    /// Thrown when two factories claim the same platform. Silently keeping the last one would
    /// make the behaviour depend on registration order, which is the kind of bug that survives
    /// for months.
    /// </exception>
    public MobileDriverFactoryResolver(IEnumerable<IMobileDriverFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);

        this.factories = [];

        foreach (IMobileDriverFactory factory in factories)
        {
            if (!this.factories.TryAdd(factory.Platform, factory))
                throw new ArgumentException(
                    $"Two factories were registered for platform '{factory.Platform}': " +
                    $"{this.factories[factory.Platform].GetType().Name} and " +
                    $"{factory.GetType().Name}.", nameof(factories));
        }
    }

    /// <summary>The production set of factories.</summary>
    public static MobileDriverFactoryResolver Default() =>
        new([new AndroidDriverFactory(), new IOSDriverFactory()]);

    /// <summary>The platforms this resolver can serve.</summary>
    public IReadOnlyCollection<Platform> SupportedPlatforms => factories.Keys;

    /// <summary>
    /// Returns the factory for <paramref name="platform"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown, listing what is supported, when nothing is registered for the platform. The list
    /// is what turns "not supported" into a one-minute fix.
    /// </exception>
    public IMobileDriverFactory Resolve(Platform platform) =>
        factories.TryGetValue(platform, out IMobileDriverFactory? factory)
            ? factory
            : throw new NotSupportedException(
                $"No mobile driver factory is registered for platform '{platform}'. Registered " +
                $"platforms are: {string.Join(", ", factories.Keys.Order())}.");
}
