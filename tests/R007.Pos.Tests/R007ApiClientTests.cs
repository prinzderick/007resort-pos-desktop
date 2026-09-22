using System.Net;
using System.Text;
using R007.Pos.Core.Api;

namespace R007.Pos.Tests;

public sealed class R007ApiClientTests
{
    [Fact]
    public async Task GetSystemInfo_CallsVersionedEndpoint_AndParsesResponse()
    {
        using var handler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"service":"007resort-api","version":"0.1.0","environment":"Development","mode":"Site"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5080/") };
        var client = new R007ApiClient(http);

        var info = await client.GetSystemInfoAsync();

        Assert.Equal(new SystemInfo("007resort-api", "0.1.0", "Development", "Site"), info);
        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
        Assert.Equal("http://localhost:5080/api/v1/system/info", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetSystemInfo_ThrowsOnServerError()
    {
        using var handler = new FakeHttpMessageHandler(HttpStatusCode.ServiceUnavailable,
            """{"title":"Service Unavailable","status":503}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5080/") };
        var client = new R007ApiClient(http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSystemInfoAsync());
    }

    private sealed class FakeHttpMessageHandler(HttpStatusCode statusCode, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
