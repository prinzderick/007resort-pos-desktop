namespace R007.Pos.Harness;

public sealed record Scenario(string Name, string Description, Func<NodeContext, Task> Run, bool NeedsNodeControl = false);

public sealed record ScenarioResult(string Name, bool Passed, TimeSpan Elapsed, string? Failure, IReadOnlyList<string> Log);

public static class Scenarios
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        .. EnrolAndLogin.All,
        .. RestaurantFlow.All,
        .. ApprovalFlow.All,
        .. PaymentFlow.All,
        .. CashAndReceipt.All,
        .. ReceptionFlow.All,
        .. OutageFlow.All,
    ];

    public static async Task<ScenarioResult> RunAsync(Scenario scenario, NodeContext node)
    {
        var log = new List<string>();
        var ctx = new NodeContext(node.Root, node.NodeScript, line => log.Add(line));
        var started = DateTime.UtcNow;
        try
        {
            await scenario.Run(ctx).ConfigureAwait(false);
            return new ScenarioResult(scenario.Name, true, DateTime.UtcNow - started, null, log);
        }
        catch (Exception ex)
        {
            return new ScenarioResult(scenario.Name, false, DateTime.UtcNow - started, ex is ScenarioFailure ? ex.Message : ex.ToString(), log);
        }
    }
}
