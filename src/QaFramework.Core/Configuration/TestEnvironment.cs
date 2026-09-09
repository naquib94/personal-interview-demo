namespace QaFramework.Core.Configuration;

/// <summary>
/// The environments this suite can be pointed at.
/// </summary>
/// <remarks>
/// An enum rather than a string, because the set of valid environments is knowable at compile
/// time. A typo in a pipeline variable then fails immediately with "'Prd' is not a valid
/// environment - expected one of Local, Ci, Staging" instead of quietly falling back to a
/// default and running the whole regression suite against the wrong system.
/// <para>
/// That failure mode is not hypothetical; it is one of the more common ways an automated suite
/// produces confidently wrong results.
/// </para>
/// </remarks>
public enum TestEnvironment
{
    /// <summary>A developer machine. The application under test is started by the suite.</summary>
    Local,

    /// <summary>A CI agent. Headless, no interactive login, artefacts published.</summary>
    Ci,

    /// <summary>
    /// A long-lived shared deployment. Present to demonstrate that the configuration model
    /// scales beyond two environments; this demo has no such deployment.
    /// </summary>
    Staging
}
