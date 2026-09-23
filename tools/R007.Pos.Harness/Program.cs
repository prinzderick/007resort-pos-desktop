using R007.Pos.Harness;

// Real-node harness. Usage:
//   R007_API_BASE_URL=http://127.0.0.1:8080/api/v1 [R007_NODE_SCRIPT=.../scripts/local-node.sh] dotnet run --project tools/R007.Pos.Harness -- [--list] [--only a,b] [--skip-outage] [-v]
var args1 = args.ToList();
var verbose = args1.Remove("-v");
var list = args1.Remove("--list");
var skipOutage = args1.Remove("--skip-outage");
string[]? only = null;
var onlyAt = args1.IndexOf("--only");
if (onlyAt >= 0 && onlyAt + 1 < args1.Count)
{
    only = args1[onlyAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

if (list)
{
    foreach (var s in Scenarios.All)
    {
        Console.WriteLine($"{s.Name,-28} {s.Description}");
    }

    return 0;
}

var node = NodeContext.FromEnvironment();
if (node is null)
{
    Console.Error.WriteLine("Set R007_API_BASE_URL (e.g. http://127.0.0.1:8080/api/v1) to a running node.");
    return 2;
}

var failed = 0;
foreach (var scenario in Scenarios.All)
{
    if ((only is not null && !only.Contains(scenario.Name)) || (skipOutage && scenario.NeedsNodeControl))
    {
        continue;
    }

    var result = await Scenarios.RunAsync(scenario, node);
    Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")}  {result.Name,-28} {result.Elapsed.TotalSeconds,5:0.0}s");
    if (verbose || !result.Passed)
    {
        foreach (var line in result.Log)
        {
            Console.WriteLine("        " + line);
        }
    }

    if (!result.Passed)
    {
        failed++;
        Console.WriteLine("        !! " + result.Failure);
    }
}

Console.WriteLine(failed == 0 ? "ALL SCENARIOS PASSED" : $"{failed} scenario(s) FAILED");
return failed == 0 ? 0 : 1;
