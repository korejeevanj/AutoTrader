using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Trading.Tests;

public sealed class HealthTests
{
    [Fact]
    public async Task Startup_health_reports_synthetic_mode_with_paper_auto_paused()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = json.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetRawText());

        Assert.Equal(4, properties.Count);
        Assert.Equal("\"ok\"", properties["status"]);
        Assert.Equal("\"AutoTrader\"", properties["application"]);
        Assert.Equal("\"SYNTHETIC TEST\"", properties["mode"]);
        Assert.Equal("false", properties["paperAutoEnabled"]);
    }
}
