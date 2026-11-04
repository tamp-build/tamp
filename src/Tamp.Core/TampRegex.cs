using System.Text.RegularExpressions;

namespace Tamp;

/// <summary>
/// The one regex match timeout Tamp.Core uses (Sonar S6444), and how a timeout surfaces.
/// </summary>
/// <remarks>
/// <para>
/// Every pattern Tamp.Core runs is anchored and linear-time, over small inputs: one line of
/// <c>.git/config</c>, one <c>Project(...)</c> line of a <c>.sln</c>, one target name. A normal match
/// takes microseconds, so <see cref="MatchTimeout"/> is a ceiling at least five orders of magnitude
/// above real use. It isn't a budget. It exists so that a future pattern edit that introduces
/// backtracking, or a pathological file, fails with a diagnostic instead of hanging the build.
/// </para>
/// <para>
/// A timeout is never read as "no match". A silent miss would mean a project quietly dropped from a
/// solution, or a remote URL quietly unknown. <see cref="Match"/> throws <see cref="InvalidDataException"/>
/// naming what was being parsed and how long the input was.
/// </para>
/// </remarks>
internal static class TampRegex
{
    /// <summary>The match timeout for every <see cref="Regex"/> in Tamp.Core.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A compiled regex with <see cref="MatchTimeout"/>.</summary>
    public static Regex Create(string pattern, RegexOptions options = RegexOptions.None)
        => new(pattern, options | RegexOptions.Compiled, MatchTimeout);

    /// <summary><paramref name="regex"/>.Match, with a timeout surfaced as a real error.</summary>
    /// <param name="regex">A regex built by <see cref="Create"/>.</param>
    /// <param name="input">The text to match.</param>
    /// <param name="what">What the input is, for the error (e.g. <c>"a Project line of build.sln"</c>).</param>
    public static Match Match(Regex regex, string input, string what)
    {
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new InvalidDataException(
                $"Parsing {what} timed out after {ex.MatchTimeout.TotalSeconds:0.###}s on {input.Length} characters of input. "
                + "The input is malformed or unexpectedly large.", ex);
        }
    }
}
