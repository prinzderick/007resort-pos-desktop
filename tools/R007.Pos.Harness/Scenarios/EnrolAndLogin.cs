using System.Net;
using System.Text.Json.Nodes;
using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Harness;

/// <summary>Device enrolment (fresh registration code, mode POS, X-Device-Token), staff sign-in (PIN, password, NFC+PIN) and the catalog with server pricing.</summary>
public static class EnrolAndLogin
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("enrol", "Fresh registration code -> device token (mode POS) -> facility capabilities", Enrol),
        new("login", "PIN + password sign-in, wrong PIN, refresh, logout", Login),
        new("login-nfc-pin", "NFC_CARD + PIN station policy (card uid = identifier, PIN = secret)", LoginNfcPin),
        new("catalog", "Catalog load, server pricing, barcode/sku lookup", Catalog),
    ];

    private static async Task Enrol(NodeContext node)
    {
        using var rig = await PosRig.EnrolAsync(node, "RECEPTION");
        var reception = await node.FacilityIdAsync("RECEPTION");
        Check.True(rig.Ctx.IsRegistered, "terminal is registered");
        Check.Equal(reception, rig.Ctx.FacilityId, "device facility = the code's facility");
        Check.True(rig.DeviceToken.StartsWith("r7d_", StringComparison.Ordinal), "device token has the r7d_ prefix");
        node.Log($"enrolled device {rig.Ctx.Identity!.DeviceId} at {rig.Ctx.FacilityName}");

        Check.True(await rig.Api.PingAsync(), "GET /health/live (server root) answers: the connectivity probe works");
        var check = await rig.Ctx.CheckServerAsync();
        Check.True(check.Ok, "server check (minClientVersion) passes: " + check.Message);

        // The registration code is single use.
        var again = await rig.Node.Raw.PostAsync("devices/register", new JsonObject { ["name"] = "dup", ["kind"] = "POS_TERMINAL", ["mode"] = "POS", ["hardwareId"] = "dup", ["registrationCode"] = "R7-AAAAA-BBBBB" });
        Check.True(again.Code is 401 or 403 or 404 or 409 or 422, $"an unknown/used registration code is refused (got {again.Code})");

        // The device row is what IT sees: kind + explicit mode.
        var token = await node.ItAdminAsync();
        var devices = (await node.Raw.GetAsync("devices?limit=200", token)).Ok("devices").Json["items"]!.AsArray();
        var row = devices.First(d => d?["id"]?.GetValue<string>() == rig.Ctx.Identity!.DeviceId.ToString("D"))!;
        Check.Equal("POS_TERMINAL", row["kind"]!.GetValue<string>(), "device kind");
        Check.Equal("POS", row["mode"]!.GetValue<string>(), "device mode");

        await rig.SignInAsync("S-0005");
        var caps = rig.Ctx.Capabilities!;
        Check.True(caps.Has(Capabilities.Pos) && caps.Has(Capabilities.Ticketing), "Reception has POS + TICKETING");
        Check.Equal("PAY_FIRST", caps.OperatingRules!.PaymentTiming, "Reception is PAY_FIRST");
        Check.True(rig.Ctx.Features.CanSell && rig.Ctx.Features.CanBook, "cashier1 at Reception may sell and book");
    }

    private static async Task Login(NodeContext node)
    {
        using var rig = await PosRig.EnrolAsync(node, "RECEPTION");

        // wrong PIN -> the API's stable code
        var bad = await Check.ThrowsApiAsync(() => rig.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, "S-0005", "9999")), "wrong PIN");
        Check.Equal("invalid_credentials", bad.Code, "wrong PIN code");
        Check.Equal(HttpStatusCode.Unauthorized, bad.Status, "wrong PIN status");

        await rig.SignInAsync("S-0005");
        Check.Equal("Ngozi Eze", rig.Auth.Staff!.DisplayName, "staff name from PIN sign-in");
        Check.True(rig.Auth.Staff.Has(Permissions.PaymentTake), "cashier holds payment.take");

        // refresh tokens rotate: a second use of the same refresh token is refused (on a throw-away session, not the terminal's own)
        var throwaway = (await rig.Node.Raw.PostAsync("auth/staff/login", new JsonObject { ["credentialType"] = "PIN", ["identifier"] = "S-0005", ["secret"] = "1234" }, deviceToken: rig.DeviceToken)).Ok("second login").Json["refreshToken"]!.GetValue<string>();
        var refreshed = await rig.Node.Raw.PostAsync("auth/staff/refresh", new JsonObject { ["refreshToken"] = throwaway }, deviceToken: rig.DeviceToken);
        Check.Equal(HttpStatusCode.OK, refreshed.Status, "refresh");
        Check.True(refreshed.Json["accessToken"] is not null && refreshed.Json["refreshToken"] is not null, "refresh returns a token pair");
        var reused = await rig.Node.Raw.PostAsync("auth/staff/refresh", new JsonObject { ["refreshToken"] = throwaway }, deviceToken: rig.DeviceToken);
        Check.Equal(HttpStatusCode.Unauthorized, reused.Status, "a used refresh token is refused (rotation)");

        // an expired/invalid access token: the pipeline refreshes ONCE (single flight, rotating refresh token) and replays every waiting request
        var goodRefresh = rig.Auth.RefreshToken!;
        rig.Auth.UpdateTokens(new AuthResult("r7a_expired_or_bad", goodRefresh, 900, rig.Auth.Staff!, null), DateTimeOffset.UtcNow);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => rig.Api.GetOpenCashSessionAsync(rig.Ctx.FacilityId, rig.Auth.Staff!.Id)));
        Check.True(results.Length == 6 && rig.Auth.IsSignedIn, "six parallel requests survived a token refresh");
        Check.True(rig.Auth.AccessToken != "r7a_expired_or_bad" && rig.Auth.RefreshToken != goodRefresh, "tokens rotated exactly once");

        // the node accepts If-Match both as its own ETag form (\"3\") and prefixed (\"v3\")
        var probe = rig.NewSell();
        await rig.FreshCashSessionAsync();
        await probe.AddProductCommand.ExecuteAsync(new ProductTile(rig.Product("GOODS-SPORTS-DRINK")));
        var order = probe.Order!;
        foreach (var form in new[] { $"\"v{order.RowVersion}\"", $"\"{order.RowVersion + 1}\"" })
        {
            var line = new JsonObject { ["productId"] = rig.Product("GOODS-SPORTS-DRINK").Id.ToString("D"), ["quantity"] = 1 };
            var version = (await rig.Api.GetOrderAsync(order.Id)).RowVersion;
            var response = await rig.Node.Raw.PostAsync($"orders/{order.Id:D}/lines", line, rig.Auth.AccessToken, rig.DeviceToken, ifMatch: form.Contains('v') ? $"\"v{version}\"" : $"\"{version}\"");
            Check.Equal(HttpStatusCode.Created, response.Status, $"If-Match {form.Replace(order.RowVersion.ToString(), "n", StringComparison.Ordinal)}");
        }

        await rig.Ctx.SignOutAsync();
        Check.True(!rig.Auth.IsSignedIn, "signed out locally");

        // username + password through the real login view-model
        var login = new LoginViewModel(rig.Ctx);
        login.TogglePasswordCommand.Execute(null);
        login.Username = "cashier1";
        login.Password = "Dev#Pass1234";
        await login.SignInCommand.ExecuteAsync();
        Check.True(rig.Auth.IsSignedIn, "password sign-in: " + login.Error);
        Check.Equal("S-0005", rig.Auth.Staff!.StaffNumber, "password sign-in staff number");

        // an expired/unknown bearer is refused with the stable code
        var anon = await node.Raw.GetAsync("orders?limit=1", "r7a_not_a_token", rig.DeviceToken);
        Check.Equal(HttpStatusCode.Unauthorized, anon.Status, "bad bearer");
    }

    private static async Task LoginNfcPin(NodeContext node)
    {
        var uid = "04" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using var reg = await PosRig.EnrolAsync(node, "RECEPTION");
        await reg.SignInAsync("S-0005");
        var cashierId = reg.Auth.Staff!.Id;
        var admin = await node.OwnerAsync();
        (await node.Raw.SendAsync(HttpMethod.Put, $"staff/{cashierId:D}/credentials/nfc-card", new JsonObject { ["cardUid"] = uid }, admin)).Ok("register NFC card");
        node.Log($"card {uid} registered to cashier1");

        var options = new PosOptions { RequireNfcAndPin = true, Approvals = new ApprovalOptions { PollIntervalSeconds = 1, WaitTimeoutMinutes = 2 } };
        using var rig = await PosRig.EnrolAsync(node, "RECEPTION", options);
        var login = new LoginViewModel(rig.Ctx);
        Check.Equal(LoginStep.TapCard, login.Step, "NFC+PIN station starts at TapCard");

        await login.HandleScanAsync(uid);
        Check.Equal(LoginStep.EnterPin, login.Step, "card tap -> enter PIN");
        Check.True(!rig.Auth.IsSignedIn, "a card alone never signs in");

        foreach (var digit in "0000")
        {
            login.KeyCommand.Execute(digit.ToString());
        }

        await login.SignInCommand.ExecuteAsync();
        Check.True(!rig.Auth.IsSignedIn, "wrong PIN with a valid card does not sign in");

        await login.HandleScanAsync(uid);
        foreach (var digit in "1234")
        {
            login.KeyCommand.Execute(digit.ToString());
        }

        await login.SignInCommand.ExecuteAsync();
        Check.True(rig.Auth.IsSignedIn, "card + PIN signs in: " + login.Error);
        Check.Equal(cashierId, rig.Auth.Staff!.Id, "the card's owner is signed in");

        // the API refuses NFC alone
        var alone = await node.Raw.PostAsync("auth/staff/login", new JsonObject { ["credentialType"] = "NFC_CARD", ["identifier"] = uid, ["secret"] = "" }, deviceToken: rig.DeviceToken);
        Check.True(alone.Code is 401 or 422, $"NFC without PIN is refused (got {alone.Code})");
    }

    private static async Task Catalog(NodeContext node)
    {
        using var rig = await PosRig.EnrolAsync(node, "RECEPTION");
        await rig.SignInAsync("S-0005");
        Check.True(rig.Ctx.Products.Count >= 10, $"Reception catalog loaded ({rig.Ctx.Products.Count} products)");
        Check.True(rig.Ctx.Categories.Count > 0, "categories loaded");

        var pool = rig.Product("POOL-ADULT");
        Check.Equal(3000m, pool.Price, "API price for the adult pool ticket");
        Check.Equal(ProductKinds.Ticket, pool.Kind, "kind");

        // server-side search (barcode box) finds by sku
        var hits = await rig.Api.GetProductsAsync(rig.Ctx.FacilityId, "POOL-ADULT");
        Check.True(hits.Any(p => p.Id == pool.Id), "q= search finds the sku");

        // pricing is the server's: the POS sends no price, the line comes back priced
        var sell = rig.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pool));
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pool));
        Check.NotNull(sell.Order, "order created by the first add");
        Check.Equal(6000m, sell.Order!.Total!.Value, "server total for 2 x 3000");
        Check.Equal(2, sell.Order.Lines.Count, "two lines (each add is one client-id line)");
        Check.True(sell.TotalText.Contains("6,000.00", StringComparison.Ordinal), "cart shows the server total");
    }
}
