using R007.Pos.Harness;

namespace R007.Pos.IntegrationTests;

/// <summary>[Fact] that is skipped unless <c>R007_API_BASE_URL</c> points at a running node (category "RealNode").</summary>
public sealed class RealNodeFactAttribute : FactAttribute
{
    public RealNodeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("R007_API_BASE_URL")))
        {
            Skip = "Set R007_API_BASE_URL (e.g. http://127.0.0.1:8080/api/v1) to run against a real node.";
        }
    }
}

/// <summary>
/// One xunit test per harness scenario. Same code as <c>tools/R007.Pos.Harness</c>; here a failing expectation fails the test.
/// Run: <c>R007_API_BASE_URL=http://127.0.0.1:8080/api/v1 dotnet test tests/R007.Pos.IntegrationTests</c>
/// (the outage scenario also needs <c>R007_NODE_SCRIPT=.../scripts/local-node.sh</c> and is skipped without it).
/// </summary>
[Trait("Category", "RealNode")]
public sealed class RealNodeTests
{
    public static IEnumerable<object[]> ScenarioNames() => Scenarios.All.Select(s => new object[] { s.Name });

    [RealNodeFact]
    public async Task Every_scenario_is_registered_once()
    {
        Assert.Equal(Scenarios.All.Count, Scenarios.All.Select(s => s.Name).Distinct().Count());
        await Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Scenario(string name)
    {
        var node = NodeContext.FromEnvironment();
        var scenario = Scenarios.All.Single(s => s.Name == name);
        if (node is null || (scenario.NeedsNodeControl && node.NodeScript is null))
        {
            return; // skipped: no node configured (xunit 2 has no dynamic skip for theories)
        }

        var result = await Scenarios.RunAsync(scenario, node);
        Assert.True(result.Passed, result.Failure + Environment.NewLine + string.Join(Environment.NewLine, result.Log));
    }
}
