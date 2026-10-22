using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DotNetSqlPerformanceLab.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task GetHealth_ReturnsHealthyResponse()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DevelopmentSwagger_ExposesDocumentAndUiForRegisteredRoutes()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        using var documentResponse = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, documentResponse.StatusCode);
        using var document = await JsonDocument.ParseAsync(await documentResponse.Content.ReadAsStreamAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/health", out _));
        Assert.True(paths.TryGetProperty("/api/queries/orders/search/optimized", out _));

        using var uiResponse = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, uiResponse.StatusCode);
        Assert.Equal("text/html", uiResponse.Content.Headers.ContentType?.MediaType);
        var uiHtml = await uiResponse.Content.ReadAsStringAsync();
        Assert.Contains("id=\"swagger-ui\"", uiHtml, StringComparison.Ordinal);
    }
}
