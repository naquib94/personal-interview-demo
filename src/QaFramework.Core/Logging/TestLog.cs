using System.Text.Json;

namespace QaFramework.Core.Logging;

/// <summary>
/// The framework's logging seam.
/// </summary>
/// <remarks>
/// A facade rather than direct <c>Console.WriteLine</c> calls, for one reason that only becomes
/// obvious later: when the team adds a reporter (Allure, LivingDoc, an internal dashboard), the
/// integration happens by assigning <see cref="Sink"/> once, instead of editing several hundred
/// call sites. The reference frameworks that log directly to the console are the ones that
/// later cannot attach anything to a report.
/// <para>
/// <see cref="Failure"/> deserves attention: it <i>returns</i> the exception it has logged
/// rather than throwing it. That allows the uniform call site
/// <c>throw TestLog.Failure(...)</c>, which keeps the message and the throw in one expression
/// while leaving the throw visible to the compiler's flow analysis. A helper that both logs and
/// throws internally forces every caller to guess whether control returns.
/// </para>
/// </remarks>
public static class TestLog
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>
    /// Where log lines go. Replaceable so a reporter can be attached, and so unit tests of the
    /// framework can assert on what was logged.
    /// </summary>
    public static Action<LogEntry> Sink { get; set; } = entry =>
        Console.WriteLine($"{entry.Level,-7} {entry.Message}");

    public static void Step(string message) => Sink(new LogEntry(LogLevel.Step, message));

    public static void Info(string message) => Sink(new LogEntry(LogLevel.Info, message));

    public static void Warning(string message) => Sink(new LogEntry(LogLevel.Warning, message));

    /// <summary>
    /// Logs a failure and returns the exception to throw.
    /// </summary>
    /// <param name="context">
    /// Extra diagnostic material - a response body, a page URL, a SQL statement. Appended to
    /// the message so that the exception carries everything needed to understand it, rather
    /// than requiring the reader to correlate it with console output that a CI log may have
    /// truncated.
    /// </param>
    public static Exception Failure(string message, string? context = null)
    {
        string full = context is null ? message : $"{message}{Environment.NewLine}{context}";
        Sink(new LogEntry(LogLevel.Error, full));
        return new AutomationFailureException(full);
    }

    /// <summary>Pretty-prints an object for inclusion in a failure message.</summary>
    public static string Describe<T>(T value)
    {
        try
        {
            return JsonSerializer.Serialize(value, PrettyJson);
        }
        catch (NotSupportedException)
        {
            // Some types cannot be serialised. Falling back to ToString() is better than
            // letting a diagnostic helper become the cause of a failure.
            return value?.ToString() ?? "null";
        }
    }
}

public enum LogLevel { Step, Info, Warning, Error }

public sealed record LogEntry(LogLevel Level, string Message)
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Signals a failure in the automation or the application under test.
/// </summary>
/// <remarks>
/// A dedicated type so that framework failures are distinguishable from assertion failures.
/// The difference matters during triage: an assertion failure is a finding about the product,
/// whereas an <see cref="AutomationFailureException"/> is usually a finding about the tests.
/// </remarks>
public sealed class AutomationFailureException(string message, Exception? innerException = null)
    : Exception(message, innerException);
