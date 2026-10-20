using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Trading.Tests;

public sealed class DashboardHubTests
{
    [Fact]
    public async Task Dashboard_clients_can_negotiate_a_signalr_connection()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        using var response = await client.PostAsync("/hubs/dashboard/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var negotiation = json.RootElement;

        Assert.Equal(1, negotiation.GetProperty("negotiateVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(negotiation.GetProperty("connectionId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(negotiation.GetProperty("connectionToken").GetString()));
        Assert.Contains(negotiation.GetProperty("availableTransports").EnumerateArray(),
            transport => transport.GetProperty("transport").GetString() == "WebSockets"
                && transport.GetProperty("transferFormats").EnumerateArray()
                    .Any(format => format.GetString() == "Text"));
    }
}
