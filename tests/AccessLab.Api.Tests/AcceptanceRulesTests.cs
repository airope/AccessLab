using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class AcceptanceRulesTests
{
    [Fact]
    public async Task Lifecycle_mutation_stales_preview_without_silently_changing_approved_arguments()
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f);
        var claire = f.As(ApiFixture.Claire);
        foreach (var status in new[] { "Suspended", "Active" })
            (
                await ApiFixture.Post(
                    claire,
                    $"/api/accounts/{ApiFixture.Alice}/status",
                    new { status, reason = "Revalidate membership context" }
                )
            ).EnsureSuccessStatusCode();
        var denied = await ApiFixture.Post(
            f.As(ApiFixture.Bob),
            $"/api/requests/{r}/approve",
            new { previewId = p }
        );
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var request = await f.As(ApiFixture.Alice)
            .GetFromJsonAsync<JsonElement>($"/api/requests/{r}");
        Assert.Equal("Pending", request.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Author_and_beneficiary_cannot_review_on_behalf_request()
    {
        await using var f = new ApiFixture();
        var (r, _) = await Scenarios.Pending(f, actor: ApiFixture.Bob, reviewer: ApiFixture.Dana);
        foreach (var actor in new[] { ApiFixture.Bob, ApiFixture.Alice })
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (
                    await ApiFixture.Post(f.As(actor), $"/api/requests/{r}/preview", new { })
                ).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await ApiFixture.Post(
                    f.As(ApiFixture.Eve),
                    $"/api/requests/{r}/approve",
                    new { previewId = "anything" }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await f.As(ApiFixture.Claire).GetAsync($"/api/memberships?accountId=not-present")
            ).StatusCode
        );
    }

    [Fact]
    public async Task Future_preview_includes_scheduled_added_rights_and_expires_at_start()
    {
        await using var f = new ApiFixture();
        var start = f.Clock.Now.AddMinutes(2);
        var r = await ApiFixture.Post(
            f.As(ApiFixture.Alice),
            "/api/previews",
            new
            {
                accountId = ApiFixture.Alice,
                groupId = "analysis",
                operation = "Add",
                startsAt = start,
                endsAt = start.AddDays(1),
                reason = "Scheduled research",
            }
        );
        r.EnsureSuccessStatusCode();
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(start, j.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(
            "Unchanged",
            j.GetProperty("changes")
                .EnumerateArray()
                .Single(c => c.GetProperty("resourceId").GetString() == "results")
                .GetProperty("effect")
                .GetString()
        );
        Assert.Equal(
            "Added",
            j.GetProperty("scheduledChanges")[0]
                .GetProperty("changes")
                .EnumerateArray()
                .Single(c => c.GetProperty("resourceId").GetString() == "results")
                .GetProperty("effect")
                .GetString()
        );
    }

    [Fact]
    public async Task Removal_retains_library_through_knowledge()
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f);
        (
            await ApiFixture.Post(
                f.As(ApiFixture.Bob),
                $"/api/requests/{r}/approve",
                new { previewId = p }
            )
        ).EnsureSuccessStatusCode();
        var alice = f.As(ApiFixture.Alice);
        var response = await ApiFixture.Post(
            alice,
            "/api/previews",
            new
            {
                accountId = ApiFixture.Alice,
                groupId = "analysis",
                operation = "Remove",
                startsAt = f.Clock.Now,
                endsAt = f.Clock.Now.AddDays(1),
                reason = "Analysis finished",
            }
        );
        response.EnsureSuccessStatusCode();
        var j = await response.Content.ReadFromJsonAsync<JsonElement>();
        var changes = j.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(
            "Removed",
            changes
                .Single(c => c.GetProperty("resourceId").GetString() == "results")
                .GetProperty("effect")
                .GetString()
        );
        var retained = changes.Single(c => c.GetProperty("resourceId").GetString() == "library");
        Assert.Equal("Retained", retained.GetProperty("effect").GetString());
        Assert.Contains(
            retained.GetProperty("reasons").EnumerateArray(),
            r => r.GetString()!.Contains("Knowledge")
        );
    }

    [Fact]
    public async Task Target_and_actor_authority_are_rechecked_after_transaction_wait()
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await MutationRunner.Lock(db);
        var pending = ApiFixture.Post(
            f.As(ApiFixture.Bob),
            $"/api/requests/{r}/approve",
            new { previewId = p }
        );
        await Task.Delay(100);
        var bob = await db.Accounts.FindAsync(ApiFixture.Bob);
        bob!.Status = "Suspended";
        bob.Revision++;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await pending).StatusCode);
        Assert.Equal("Pending", (await db.Requests.FindAsync(r))!.Status);
    }

    [Theory]
    [InlineData("reject", "Rejected")]
    [InlineData("cancel", "Cancelled")]
    public async Task Terminal_decisions_do_not_provision(string action, string expected)
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f);
        var a = f.As(action == "reject" ? ApiFixture.Bob : ApiFixture.Alice);
        var response = await ApiFixture.Post(
            a,
            $"/api/requests/{r}/{action}",
            new { reason = "No longer required" }
        );
        response.EnsureSuccessStatusCode();
        Assert.Equal(
            expected,
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("status")
                .GetString()
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await ApiFixture.Post(
                    f.As(ApiFixture.Bob),
                    $"/api/requests/{r}/approve",
                    new { previewId = p }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task Maximum_preview_age_is_five_minutes()
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f);
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await ApiFixture.Post(
                    f.As(ApiFixture.Bob),
                    $"/api/requests/{r}/approve",
                    new { previewId = p }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task Commit_response_loss_replays_without_duplicate_decision()
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f);
        var key = Guid.NewGuid().ToString();
        f.Faults.Point = "after_commit";
        var failed = await ApiFixture.Post(
            f.As(ApiFixture.Bob),
            $"/api/requests/{r}/approve",
            new { previewId = p },
            key
        );
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        f.Faults.Point = null;
        var replay = await ApiFixture.Post(
            f.As(ApiFixture.Bob),
            $"/api/requests/{r}/approve",
            new { previewId = p },
            key
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == "RequestApproved"));
    }
}
