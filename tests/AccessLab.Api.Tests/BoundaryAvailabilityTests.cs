using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AccessLab.Api.Tests;

public class BoundaryAvailabilityTests
{
    [Fact]
    public async Task Unreachable_decision_database_fails_closed_without_stopping_postgres()
    {
        await using var source = new ApiFixture();
        await using var host = source.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration(
                (_, c) =>
                    c.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AccessLab:Seed"] = "false",
                            ["AccessLab:WorkerEnabled"] = "false",
                            ["ConnectionStrings:Database"] =
                                "Host=127.0.0.1;Port=1;Database=accesslab_tests;Username=fictional;Timeout=1;Pooling=false",
                        }
                    )
            )
        );
        var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("Test-Actor", ApiFixture.Alice);
        foreach (
            var path in new[] { "/api/access", "/api/resources/library/content", "/api/session" }
        )
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(503, problem.GetProperty("status").GetInt32());
            Assert.DoesNotContain("fictional", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Failure_control_route_is_absent_when_demo_is_disabled()
    {
        await using var source = new ApiFixture();
        await using var host = source.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration(
                (_, c) =>
                    c.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AccessLab:DemoMode"] = "false" }
                    )
            )
        );
        var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("Test-Actor", ApiFixture.Claire);
        var response = await ApiFixture.Post(
            client,
            "/api/operations/failure-mode",
            new { enabled = true }
        );
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
