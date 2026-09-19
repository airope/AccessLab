using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}

public class TestFaults : IFaults
{
    public string? Point { get; set; }

    public void Hit(string point)
    {
        if (Point == point)
            throw new InvalidOperationException("FAULT_SENTINEL_NOT_FOR_CLIENTS");
    }
}

public static class Scenarios
{
    public static async Task<(string request, string preview)> Pending(
        ApiFixture f,
        DateTimeOffset? starts = null,
        DateTimeOffset? ends = null,
        string actor = ApiFixture.Alice,
        string target = ApiFixture.Alice,
        string reviewer = ApiFixture.Bob
    )
    {
        var c = f.As(actor);
        var p = await ApiFixture.Post(
            c,
            "/api/previews",
            new
            {
                accountId = target,
                groupId = "analysis",
                operation = "Add",
                startsAt = starts ?? f.Clock.Now.AddMinutes(-1),
                endsAt = ends ?? f.Clock.Now.AddDays(1),
                reason = "Research test scenario",
            }
        );
        p.EnsureSuccessStatusCode();
        var id = (await p.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var s = await ApiFixture.Post(c, "/api/requests", new { previewId = id });
        s.EnsureSuccessStatusCode();
        var r = (await s.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var rp = await ApiFixture.Post(f.As(reviewer), $"/api/requests/{r}/preview", new { });
        rp.EnsureSuccessStatusCode();
        return (
            r,
            (await rp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!
        );
    }

    public static async Task Tick(ApiFixture f, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            await using var scope = f.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Reconciler>().Tick();
        }
    }
}

public class ReliabilityTests
{
    [Theory]
    [InlineData("before_save")]
    [InlineData("after_save")]
    [InlineData("before_commit")]
    public async Task Transaction_fault_rolls_back_membership_decision_outbox_and_audit(
        string point
    )
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f);
        f.Faults.Point = point;
        var response = await ApiFixture.Post(
            f.As(ApiFixture.Bob),
            $"/api/requests/{r}/approve",
            new { previewId = p }
        );
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("FAULT_SENTINEL", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        Assert.Equal("Pending", (await db.Requests.FindAsync(r))!.Status);
        Assert.False(await db.Memberships.AnyAsync(m => m.GroupId == "analysis"));
        Assert.False(await db.Outbox.AnyAsync());
        Assert.False(await db.Audit.AnyAsync(e => e.Action == "RequestApproved"));
    }

    [Fact]
    public async Task Time_boundary_invalidates_preview_without_version_change()
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f, f.Clock.Now.AddMinutes(1));
        f.Clock.Now = f.Clock.Now.AddMinutes(1);
        var response = await ApiFixture.Post(
            f.As(ApiFixture.Bob),
            $"/api/requests/{r}/approve",
            new { previewId = p }
        );
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("preview_stale", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_simultaneous_approvals_have_one_logical_winner()
    {
        await using var f = new ApiFixture();
        var (r, p) = await Scenarios.Pending(f);
        var results = await Task.WhenAll(
            ApiFixture.Post(
                f.As(ApiFixture.Bob),
                $"/api/requests/{r}/approve",
                new { previewId = p }
            ),
            ApiFixture.Post(
                f.As(ApiFixture.Bob),
                $"/api/requests/{r}/approve",
                new { previewId = p }
            )
        );
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        Assert.Equal(1, await db.Memberships.CountAsync(m => m.GroupId == "analysis"));
        Assert.Equal(1, await db.Audit.CountAsync(e => e.Action == "RequestApproved"));
        Assert.Equal(1, await db.Outbox.CountAsync());
    }

    [Fact]
    public async Task Expiration_denies_content_with_worker_stopped()
    {
        await using var f = new ApiFixture();
        var end = f.Clock.Now.AddMinutes(2);
        var (r, p) = await Scenarios.Pending(f, ends: end);
        (
            await ApiFixture.Post(
                f.As(ApiFixture.Bob),
                $"/api/requests/{r}/approve",
                new { previewId = p }
            )
        ).EnsureSuccessStatusCode();
        await Scenarios.Tick(f, 7);
        Assert.Equal(
            HttpStatusCode.OK,
            (await f.As(ApiFixture.Alice).GetAsync("/api/resources/results/content")).StatusCode
        );
        f.Clock.Now = end;
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await f.As(ApiFixture.Alice).GetAsync("/api/resources/results/content")).StatusCode
        );
    }

    [Fact]
    public async Task Five_failures_are_durable_then_retry_reconciles_current_suspended_state()
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
        var claire = f.As(ApiFixture.Claire);
        (
            await ApiFixture.Post(claire, "/api/operations/failure-mode", new { enabled = true })
        ).EnsureSuccessStatusCode();
        for (int i = 0; i < 30; i++)
        {
            await Scenarios.Tick(f);
            f.Clock.Now = f.Clock.Now.AddSeconds(3);
        }
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var s = await scope
                .ServiceProvider.GetRequiredService<LabDb>()
                .SyncStates.FindAsync(ApiFixture.Alice);
            Assert.Equal("Failed", s!.Status);
            Assert.Equal(5, s.Attempts);
            Assert.Equal(0, s.ObservedRevision);
        }
        (
            await ApiFixture.Post(
                claire,
                $"/api/accounts/{ApiFixture.Alice}/status",
                new { status = "Suspended", reason = "Stop all access" }
            )
        ).EnsureSuccessStatusCode();
        (
            await ApiFixture.Post(claire, "/api/operations/failure-mode", new { enabled = false })
        ).EnsureSuccessStatusCode();
        (
            await ApiFixture.Post(
                claire,
                $"/api/operations/{ApiFixture.Alice}/retry",
                new { reason = "Reconcile current state" }
            )
        ).EnsureSuccessStatusCode();
        await Scenarios.Tick(f, 10);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var s = await scope
                .ServiceProvider.GetRequiredService<LabDb>()
                .SyncStates.FindAsync(ApiFixture.Alice);
            Assert.Equal("InSync", s!.Status);
            Assert.Empty(s.Observed);
            Assert.Equal(s.DesiredRevision, s.ObservedRevision);
        }
    }
}
