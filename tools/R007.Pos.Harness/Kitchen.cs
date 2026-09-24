using System.Net;
using System.Text.Json.Nodes;

namespace R007.Pos.Harness;

/// <summary>Plays the kitchen / bar screen (a KDS device + kitchen1) through the API, so a Restaurant order can reach READY and be served.</summary>
public static class Kitchen
{
    public const string KdsDeviceToken = "r7d_dev_kds_main_kitchen";

    /// <summary>Walks every prep ticket of the order NEW -> ACCEPTED -> IN_PROGRESS -> READY -> DISPENSED.</summary>
    public static async Task<int> CompleteOrderAsync(NodeContext node, Guid orderId, IEnumerable<Guid?> stationIds)
    {
        var token = await node.Raw.LoginPinAsync("S-0004", deviceToken: KdsDeviceToken);
        var done = 0;
        foreach (var station in stationIds.Where(s => s is not null).Select(s => s!.Value).Distinct())
        {
            var board = (await node.Raw.GetAsync($"kds/stations/{station:D}/tickets", token, KdsDeviceToken)).Ok("kds board").Json["items"]!.AsArray();
            foreach (var ticket in board.Where(t => t?["orderId"]?.GetValue<string>() == orderId.ToString("D")))
            {
                var id = ticket!["id"]!.GetValue<string>();
                var version = ticket["rowVersion"]!.GetValue<int>();
                foreach (var to in new[] { "ACCEPTED", "IN_PROGRESS", "READY", "DISPENSED" })
                {
                    var response = await node.Raw.PostAsync($"prep-tickets/{id}/transition", new JsonObject { ["to"] = to }, token, KdsDeviceToken, ifMatch: $"\"{version}\"");
                    response.Expect(HttpStatusCode.OK, $"ticket {ticket["number"]} -> {to}");
                    version = response.Json["rowVersion"]!.GetValue<int>();
                }

                done++;
            }
        }

        return done;
    }
}

/// <summary>
/// Demo tables are a finite resource and a failed or interrupted run leaves orders open on them. When no table is FREE the
/// harness has supervisor1 void the stray orders (a supervisor holds order.void.approve, so it applies at once) and frees the table.
/// </summary>
public static class Janitor
{
    public static async Task<R007.Pos.Core.Api.DiningTable> FreeTableAsync(PosRig rig)
    {
        var tables = await rig.Api.GetTablesAsync(rig.Ctx.FacilityId);
        if (tables.FirstOrDefault(t => t.Status == "FREE") is { } free)
        {
            return free;
        }

        var raw = rig.Node.Raw;
        var supervisor = await raw.LoginPinAsync("S-0008", deviceToken: rig.DeviceToken);
        foreach (var table in tables.Where(t => t.OpenTabId is null))
        {
            foreach (var orderId in table.OpenOrderIds ?? [])
            {
                var order = (await raw.GetAsync($"orders/{orderId:D}", supervisor, rig.DeviceToken)).Ok("stray order").Json;
                if (order["status"]!.GetValue<string>() is "SETTLED" or "VOIDED")
                {
                    continue;
                }

                await raw.PostAsync($"orders/{orderId:D}/void", new JsonObject { ["reason"] = "harness cleanup" }, supervisor, rig.DeviceToken, ifMatch: $"\"{order["rowVersion"]!.GetValue<int>()}\"");
            }

            var fresh = (await raw.GetAsync($"tables/{table.Id:D}", supervisor, rig.DeviceToken)).Ok("table").Json;
            var freed = await raw.SendAsync(HttpMethod.Patch, $"tables/{table.Id:D}", new JsonObject { ["status"] = "FREE" }, supervisor, rig.DeviceToken, ifMatch: $"\"{fresh["rowVersion"]!.GetValue<int>()}\"");
            if (freed.Code == 200)
            {
                rig.Node.Log($"janitor freed table {table.Label}");
                return (await rig.Api.GetTablesAsync(rig.Ctx.FacilityId)).First(t => t.Id == table.Id);
            }
        }

        throw new ScenarioFailure("No table could be freed (all have open tabs); reseed the node.");
    }
}
