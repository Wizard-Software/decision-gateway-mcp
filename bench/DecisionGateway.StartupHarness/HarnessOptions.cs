using System.Globalization;
using System.Text.RegularExpressions;

namespace DecisionGateway.StartupHarness;

/// <summary>Parsed command line of the startup harness.</summary>
/// <param name="BinaryPath">Fully qualified path of the executable to measure.</param>
/// <param name="Arguments">Arguments passed to the executable, in order.</param>
/// <param name="Runs">Number of measured runs, including the cold one.</param>
/// <param name="Rid">Runtime identifier; becomes part of the report file name, so it is strictly validated.</param>
/// <param name="OutputPath">Where the JSON report is written.</param>
/// <param name="Timeout">Per-run timeout.</param>
internal sealed partial record HarnessOptions(
    string BinaryPath,
    IReadOnlyList<string> Arguments,
    int Runs,
    string Rid,
    string OutputPath,
    TimeSpan Timeout)
{
    internal const int DefaultRuns = 50;
    internal const int MinRuns = 2;
    internal const int MaxRuns = 1000;
    internal const int DefaultTimeoutSeconds = 10;
    internal const int MaxTimeoutSeconds = 600;

    /// <summary>Parses the command line.</summary>
    /// <exception cref="ArgumentException">An option is unknown, duplicated, missing, or has an invalid value; the message names it.</exception>
    public static HarnessOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? binary = null;
        string? rid = null;
        string? output = null;
        var runs = DefaultRuns;
        var timeoutSeconds = DefaultTimeoutSeconds;
        var arguments = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name is not ("--binary" or "--arg" or "--runs" or "--rid" or "--output" or "--timeout-seconds"))
            {
                throw new ArgumentException($"Unknown option '{name}'.", nameof(args));
            }

            // --arg is the only repeatable option.
            if (name != "--arg" && !seen.Add(name))
            {
                throw new ArgumentException($"Option {name} was given more than once.", nameof(args));
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException($"Option {name} requires a value.", nameof(args));
            }

            // The next token is always the value, even when it starts with "--" (host arguments may).
            var value = args[++i];
            switch (name)
            {
                case "--binary":
                    binary = value;
                    break;
                case "--arg":
                    arguments.Add(value);
                    break;
                case "--runs":
                    runs = ParseInt(name, value, MinRuns, MaxRuns);
                    break;
                case "--rid":
                    rid = value;
                    break;
                case "--output":
                    output = value;
                    break;
                case "--timeout-seconds":
                    timeoutSeconds = ParseInt(name, value, 1, MaxTimeoutSeconds);
                    break;
            }
        }

        if (string.IsNullOrEmpty(binary))
        {
            throw new ArgumentException("Option --binary is required.", nameof(args));
        }

        if (!Path.IsPathFullyQualified(binary))
        {
            throw new ArgumentException("Option --binary must be a fully qualified path.", nameof(args));
        }

        if (rid is null)
        {
            throw new ArgumentException("Option --rid is required.", nameof(args));
        }

        // \A...\z (not ^...$): '$' would accept a trailing newline, and the RID ends up in a file name.
        if (!RidPattern().IsMatch(rid))
        {
            throw new ArgumentException("Option --rid must match \\A[a-z0-9-]+\\z.", nameof(args));
        }

        if (string.IsNullOrEmpty(output))
        {
            throw new ArgumentException("Option --output is required.", nameof(args));
        }

        return new HarnessOptions(binary, arguments, runs, rid, output, TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static int ParseInt(string name, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min
            || parsed > max)
        {
            throw new ArgumentException($"Option {name} must be an integer between {min} and {max}.", nameof(name));
        }

        return parsed;
    }

    [GeneratedRegex(@"\A[a-z0-9-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RidPattern();
}
