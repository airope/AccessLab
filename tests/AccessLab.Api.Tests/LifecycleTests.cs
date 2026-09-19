using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AccessLab.Api.Tests;

public class LifecycleTests
{
    [Fact]
    public async Task Suspension_immediately_blocks_existing_session_and_is_audited()
    {
        await using var f = new ApiFixture();
        var claire = f.As(ApiFixture.Claire);
        var alice = f.As(ApiFixture.Alice);
        var response = await ApiFixture.Post(
            claire,
            $"/api/accounts/{ApiFixture.Alice}/status",
            new { status = "Suspended", reason = "Temporary review" }
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/access")).StatusCode);
        var audit = await claire.GetFromJsonAsync<JsonElement>("/api/audit");
        Assert.Contains(
            audit.GetProperty("items").EnumerateArray(),
            e => e.GetProperty("action").GetString() == "AccountSuspended"
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await ApiFixture.Post(
                    claire,
                    $"/api/accounts/{ApiFixture.Alice}/status",
                    new { status = "Active", reason = "Review complete" }
                )
            ).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/access")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await ApiFixture.Post(
                    claire,
                    $"/api/accounts/{ApiFixture.Alice}/status",
                    new { status = "Ended", reason = "Departure confirmed" }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await ApiFixture.Post(
                    claire,
                    $"/api/accounts/{ApiFixture.Alice}/status",
                    new { status = "Active", reason = "Cannot return" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task Membership_read_does_not_reveal_other_accounts()
    {
        await using var f = new ApiFixture();
        var alice = f.As(ApiFixture.Alice);
        var own = await alice.GetAsync("/api/memberships");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await alice.GetAsync($"/api/memberships?accountId={ApiFixture.Bob}")).StatusCode
        );
        var j = await own.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            j.GetProperty("items").EnumerateArray(),
            m => m.GetProperty("membershipId").GetString() == "alice-knowledge"
        );
    }
}
