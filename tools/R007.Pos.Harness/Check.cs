using System.Globalization;

namespace R007.Pos.Harness;

/// <summary>A scenario expectation that did not hold against the real node.</summary>
public sealed class ScenarioFailure(string message) : Exception(message);

/// <summary>Tiny assertion helpers (no test-framework dependency, so the console harness can use them too).</summary>
public static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new ScenarioFailure("Expected: " + what);
        }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new ScenarioFailure(string.Create(CultureInfo.InvariantCulture, $"{what}: expected <{expected}> but was <{actual}>"));
        }
    }

    public static T NotNull<T>(T? value, string what)
        where T : class =>
        value ?? throw new ScenarioFailure($"{what}: was null");

    public static void Contains(string haystack, string needle, string what)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new ScenarioFailure($"{what}: expected to contain <{needle}> in <{haystack}>");
        }
    }

    /// <summary>Runs <paramref name="action"/> and returns the API problem it must throw.</summary>
    public static async Task<R007.Pos.Core.Api.ApiException> ThrowsApiAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (R007.Pos.Core.Api.ApiException ex)
        {
            return ex;
        }

        throw new ScenarioFailure($"{what}: expected the API to refuse the request, but it succeeded");
    }

    public static ScenarioFailure Fail(string message) => new(message);
}
