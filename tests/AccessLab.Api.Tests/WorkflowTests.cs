using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AccessLab.Api.Tests;

public class WorkflowTests : IClassFixture<ApiFixture>
{
    readonly ApiFixture f;

    public WorkflowTests(ApiFixture f) => this.f = f;

    [Fact]
    public async Task Preview_submit_separate_reviewer_approval_is_persistent()
    {
        var alice = f.As(ApiFixture.Alice);
        var bob = f.As(ApiFixture.Bob);
        var now = DateTimeOffset.UtcNow;
        var p = await ApiFixture.Post(
            alice,
            "/api/previews",
            new
            {
                accountId = ApiFixture.Alice,
                groupId = "analysis",
                operation = "Add",
                startsAt = now.AddMinutes(-1),
                endsAt = now.AddDays(1),
                reason = "Test legitimate research",
            }
        );
        Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        var preview = await p.Content.ReadFromJsonAsync<JsonElement>();
        var submit = await ApiFixture.Post(
            alice,
            "/api/requests",
            new { previewId = preview.GetProperty("id").GetString() }
        );
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        var request = await submit.Content.ReadFromJsonAsync<JsonElement>();
        var id = request.GetProperty("id").GetString();
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await ApiFixture.Post(alice, $"/api/requests/{id}/preview", new { })).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await f.As(ApiFixture.Eve).GetAsync($"/api/requests/{id}")).StatusCode
        );
        var rp = await ApiFixture.Post(bob, $"/api/requests/{id}/preview", new { });
        Assert.Equal(HttpStatusCode.OK, rp.StatusCode);
        var review = await rp.Content.ReadFromJsonAsync<JsonElement>();
        var key = Guid.NewGuid().ToString();
        var args = new { previewId = review.GetProperty("id").GetString() };
        var approved = await ApiFixture.Post(bob, $"/api/requests/{id}/approve", args, key);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var dto = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approved", dto.GetProperty("status").GetString());
        var replay = await ApiFixture.Post(bob, $"/api/requests/{id}/approve", args, key);
        Assert.Equal(
            await approved.Content.ReadAsStringAsync(),
            await replay.Content.ReadAsStringAsync()
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await ApiFixture.Post(
                    bob,
                    $"/api/requests/{id}/approve",
                    new { previewId = "other" },
                    key
                )
            ).StatusCode
        );
        var access = await alice.GetFromJsonAsync<JsonElement>("/api/access");
        Assert.True(
            access
                .GetProperty("resources")
                .EnumerateArray()
                .Single(r => r.GetProperty("id").GetString() == "results")
                .GetProperty("allowed")
                .GetBoolean()
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await alice.GetAsync("/api/resources/results/content")).StatusCode
        );
    }
}
