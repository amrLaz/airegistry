using Microsoft.AspNetCore.Hosting;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MyRegistry.Integration.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client() =>
        factory.WithWebHostBuilder(b => b.UseEnvironment("Testing")).CreateClient();

    [Fact]
    public async Task Health_returns_200()
    {
        var res = await Client().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Home_page_renders()
    {
        var res = await Client().GetAsync("/");
        res.EnsureSuccessStatusCode();
        Assert.Contains("MyRegistry", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        var res = await Client().GetAsync("/openapi/v1.json");
        res.EnsureSuccessStatusCode();
        Assert.Contains("/health", await res.Content.ReadAsStringAsync());
    }
}
