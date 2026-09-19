using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AccessLab.Api.Tests;

public class OperationsTests
{
    [Fact]
    public async Task Operator_can_enable_failure_simulation_but_member_cannot()
    {
        await using var f = new ApiFixture();
        var claire = f.As(ApiFixture.Claire);
        var r = await ApiFixture.Post(
            claire,
            "/api/operations/failure-mode",
            new { enabled = true }
        );
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var state = await claire.GetFromJsonAsync<JsonElement>("/api/operations");
        Assert.True(state.GetProperty("failureMode").GetBoolean());
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await f.As(ApiFixture.Alice).GetAsync("/api/operations")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await ApiFixture.Post(
                    f.As(ApiFixture.Alice),
                    "/api/operations/failure-mode",
                    new { enabled = false }
                )
            ).StatusCode
        );
    }
}
