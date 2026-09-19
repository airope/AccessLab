using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class SecurityRegressionTests
{
    [Fact]
    public async Task Submission_replay_checks_current_reviewer_authority()
    {
        await using var f = new ApiFixture();
        var bob = f.As(ApiFixture.Bob);
        var command = new
        {
            accountId = ApiFixture.Alice,
            groupId = "analysis",
            operation = "Add",
            startsAt = f.Clock.Now.AddMinutes(-1),
            endsAt = f.Clock.Now.AddDays(1),
            reason = "On behalf of member",
        };
        var p = await ApiFixture.Post(bob, "/api/previews", command);
        p.EnsureSuccessStatusCode();
        var previewId = (await p.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetString();
        var key = Guid.NewGuid().ToString();
        (
            await ApiFixture.Post(bob, "/api/requests", new { previewId }, key)
        ).EnsureSuccessStatusCode();
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await MutationRunner.Lock(db);
            db.Roles.RemoveRange(
                await db
                    .Roles.Where(r => r.AccountId == ApiFixture.Bob && r.Role == "Reviewer")
                    .ToListAsync()
            );
            (await db.Accounts.FindAsync(ApiFixture.Bob))!.Revision++;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        var replay = await ApiFixture.Post(bob, "/api/requests", new { previewId }, key);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
    }

    [Fact]
    public async Task Malformed_mutation_and_missing_csrf_are_safe_problem_documents()
    {
        await using var f = new ApiFixture();
        var c = f.As(ApiFixture.Alice);
        var missing = await c.PostAsJsonAsync("/api/previews", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        var unknown = await c.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Actor_suspension_invalidates_even_idempotent_replay()
    {
        await using var f = new ApiFixture();
        var alice = f.As(ApiFixture.Alice);
        var command = new
        {
            accountId = ApiFixture.Alice,
            groupId = "analysis",
            operation = "Add",
            startsAt = f.Clock.Now.AddMinutes(-1),
            endsAt = f.Clock.Now.AddDays(1),
            reason = "A legitimate request",
        };
        var key = Guid.NewGuid().ToString();
        (await ApiFixture.Post(alice, "/api/previews", command, key)).EnsureSuccessStatusCode();
        (
            await ApiFixture.Post(
                f.As(ApiFixture.Claire),
                $"/api/accounts/{ApiFixture.Alice}/status",
                new { status = "Suspended", reason = "Suspend the actor" }
            )
        ).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await ApiFixture.Post(alice, "/api/previews", command, key)).StatusCode
        );
    }
}
