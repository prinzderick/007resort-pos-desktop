using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Offline;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Tests;

public sealed class SellTests
{
    private static ProductTile Tile(TestPos pos, Guid id) => new(pos.Product(id));

    [Fact]
    public async Task Catalog_TabsAndGrid_FilterByCategoryAndSearch()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();

        Assert.Equal(["All", "Drinks", "Food", "Retail", "Tickets & Rentals"], sell.Categories.Select(c => c.Name));
        Assert.Equal(MockData.Products.Count, sell.VisibleProducts.Count);

        sell.SelectedCategory = sell.Categories.First(c => c.Name == "Food");
        Assert.All(sell.VisibleProducts, p => Assert.Equal(MockData.CatFood, p.Product.CategoryId));

        sell.SearchText = "lager";
        Assert.Equal("Star Lager 60cl", Assert.Single(sell.VisibleProducts).Name); // search overrides the category tab
    }

    [Fact]
    public async Task AddingItems_ShowsServerPricedTotals_NotLocalMaths()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();

        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Beer));
        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Jollof));

        Assert.Equal(2, sell.Lines.Count);
        Assert.Equal("₦5,000.00", sell.TotalText);
        Assert.Equal("₦1,500.00", sell.Lines[0].LineTotalText);
        // The total on screen is the server's own figure.
        Assert.Equal(pos.Server.PeekOrder(sell.Order!.Id)!.Total, sell.Order.Total);
        Assert.Equal(1, pos.Server.OrderCount);
    }

    [Fact]
    public async Task ServerVat_WhenEnabledByAdmin_IsShownAsReturned()
    {
        using var pos = new TestPos();
        pos.Env.Server.GetType(); // mock defaults: VAT off (ADR-0011)
        await pos.SignInAsync();
        var sell = pos.NewSell();

        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Beer));

        Assert.Equal(string.Empty, sell.TaxText); // no tax line when the API reports 0
    }

    [Fact]
    public async Task Vat_FromTheApi_IsDisplayedNotComputed()
    {
        using var env = new TestEnv(new MockOptions { VatRatePercent = 7.5m });
        await env.SignInAsync();
        var order = await env.Api.CreateOrderAsync(new CreateOrderRequest(env.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 2)]), IdempotencyKeys.New());

        Assert.Equal(3000m, order.Subtotal);
        Assert.Equal(225m, order.TaxTotal);
        Assert.Equal(3225m, order.Total);
    }

    [Fact]
    public async Task Barcode_Scan_AddsTheMatchingProduct()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();

        await sell.HandleScanAsync("6001234500028"); // Coca-Cola

        var line = Assert.Single(sell.Lines);
        Assert.Equal("Coca-Cola 50cl", line.Name);
        Assert.Equal(string.Empty, sell.ScanText);
    }

    [Fact]
    public async Task Barcode_ScanBox_EnterSubmits_AndUnknownCodeExplains()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();

        sell.ScanText = "0000000000000";
        await sell.ScanCommand.ExecuteAsync();

        Assert.Empty(sell.Lines);
        Assert.Contains("No product matches", sell.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sku_ScannedLocally_DoesNotNeedAServerRoundTrip()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();
        var before = pos.Server.Requests.Count(r => r.Path == "/api/v1/catalog/products");

        await sell.HandleScanAsync("DRK-WATR");

        Assert.Equal("Bottled Water", Assert.Single(sell.Lines).Name);
        Assert.Equal(before, pos.Server.Requests.Count(r => r.Path == "/api/v1/catalog/products"));
    }

    [Fact]
    public async Task QuickNote_IsSentAsLineNotes_ThenCleared()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();

        sell.ToggleNoteCommand.Execute("No ice");
        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Beer));

        Assert.Equal("No ice", sell.Lines[0].Notes);
        Assert.Null(sell.PendingNote);
    }

    [Fact]
    public async Task Quantity_Increment_Decrement_Remove_AllReturnServerPricedOrders()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Beer));

        await sell.IncrementCommand.ExecuteAsync(sell.Lines[0]);
        Assert.Equal(2, sell.Lines[0].Quantity);
        Assert.Equal("₦3,000.00", sell.TotalText);

        await sell.DecrementCommand.ExecuteAsync(sell.Lines[0]);
        Assert.Equal(1, sell.Lines[0].Quantity);

        await sell.RemoveLineCommand.ExecuteAsync(sell.Lines[0]);
        Assert.Empty(sell.Lines);
        Assert.Equal("₦0.00", sell.TotalText);
    }

    [Fact]
    public async Task Send_RoutesOrder_AndTheNextItemStartsANewOrder()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Jollof));
        var first = sell.Order!.Id;

        await sell.SendCommand.ExecuteAsync();
        Assert.Equal(OrderStatuses.Sent, sell.Order!.Status);
        Assert.False(sell.CanSend);

        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Fries));

        Assert.NotEqual(first, sell.Order!.Id);
        Assert.Equal(2, pos.Server.OrderCount);
    }

    [Fact]
    public async Task Waiter_CannotPayOrVoid_ButCanSell()
    {
        using var pos = new TestPos();
        await pos.SignInAsync(staffNumber: "S-1002", pin: MockData.WaiterPin);
        var sell = pos.NewSell();

        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Beer));

        Assert.False(sell.CanPayOrder);
        Assert.False(sell.CanVoid);
        Assert.False(sell.CanDiscount);
        Assert.True(sell.CanEditLines);
    }

    [Fact]
    public async Task ConcurrentEditFromAnotherDevice_IsRefused_AndExplainedToTheOperator()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Beer));
        // Another device edits the same order: our row version is now stale.
        var order = sell.Order!;
        await pos.Env.Api.AddLineAsync(order.Id, order.RowVersion, new OrderLineInput(MockData.Soda, 1), IdempotencyKeys.New());

        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Water));

        Assert.Contains("changed on another device", sell.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emergency_WhenServerDown_ItemsAreQueuedAndFlaggedNeverConfirmed()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        pos.Emergency.Policy = new(true, OfflinePaymentPolicy.CashOnly);
        var sell = pos.NewSell();
        pos.Server.Offline = true;

        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Beer));

        Assert.True(sell.IsPendingConfirmation);
        Assert.Contains("PENDING CONFIRMATION", sell.StatusBanner, StringComparison.Ordinal);
        Assert.Contains("ESTIMATE", sell.TotalText, StringComparison.Ordinal);
        Assert.Equal("pending", sell.Lines[0].LineTotalText);
        Assert.Equal(1, await pos.Queue.CountPendingAsync());
        Assert.False(sell.CanEditLines); // no edits to queued lines
    }

    [Fact]
    public async Task Emergency_WhenFacilityForbidsOffline_NothingIsQueued()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        pos.Emergency.Policy = OfflinePolicy.Disabled;
        var sell = pos.NewSell();
        pos.Server.Offline = true;

        await sell.AddProductCommand.ExecuteAsync(Tile(pos, MockData.Beer));

        Assert.Equal("Cannot reach the server. Check the network and try again.", sell.Error);
        Assert.Equal(0, await pos.Queue.CountPendingAsync());
    }
}
