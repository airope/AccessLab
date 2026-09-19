using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class ReviewRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Ahead_destination_converges_from_pending_or_failed(bool failed, bool retry)
    {
        await using var f = new ApiFixture();
        var claire = f.As(ApiFixture.Claire);
        await Scenarios.Tick(f, 6);
        (
            await ApiFixture.Post(
                claire,
                $"/api/accounts/{ApiFixture.Alice}/status",
                new { status = "Suspended", reason = "Revoke access" }
            )
        ).EnsureSuccessStatusCode();
        if (failed)
        {
            (
                await ApiFixture.Post(
                    claire,
                    "/api/operations/failure-mode",
                    new { enabled = true }
                )
            ).EnsureSuccessStatusCode();
            for (int i = 0; i < 5; i++)
            {
                await Scenarios.Tick(f);
                f.Clock.Now = f.Clock.Now.AddSeconds(3);
            }
            (
                await ApiFixture.Post(
                    claire,
                    "/api/operations/failure-mode",
                    new { enabled = false }
                )
            ).EnsureSuccessStatusCode();
        }
        long original;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            var state = (await db.SyncStates.FindAsync(ApiFixture.Alice))!;
            Assert.Equal(failed ? "Failed" : "Pending", state.Status);
            original = state.DesiredRevision;
            await scope
                .ServiceProvider.GetRequiredService<ISimulatedDestination>()
                .Apply(ApiFixture.Alice, original + 1, ["library"]);
        }
        if (retry)
            (
                await ApiFixture.Post(
                    claire,
                    $"/api/operations/{ApiFixture.Alice}/retry",
                    new { reason = "Recover divergence" }
                )
            ).EnsureSuccessStatusCode();
        await Scenarios.Tick(f, 10);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            var state = (await db.SyncStates.FindAsync(ApiFixture.Alice))!;
            var actual = (await db.DestinationStates.FindAsync(ApiFixture.Alice))!;
            Assert.Equal("InSync", state.Status);
            Assert.True(state.DesiredRevision > original + 1);
            Assert.Equal(state.DesiredRevision, actual.Revision);
            Assert.Empty(actual.Resources);
            Assert.Empty(
                (
                    await db.Projections.SingleAsync(p =>
                        p.AccountId == ApiFixture.Alice && p.Revision == original
                    )
                ).Resources
            );
            Assert.Equal(3, await db.Projections.CountAsync(p => p.AccountId == ApiFixture.Alice));
            Assert.False(
                await db.Outbox.AnyAsync(o =>
                    o.AccountId == ApiFixture.Alice && o.Status == "Pending"
                )
            );
        }
    }

    [Fact]
    public async Task Unchanged_desired_projection_respects_a_higher_observed_floor()
    {
        await using var f = new ApiFixture();
        _ = f.As(ApiFixture.Alice);
        await Scenarios.Tick(f, 6);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        var state = (await db.SyncStates.FindAsync(ApiFixture.Alice))!;
        var floor = state.DesiredRevision + 1;
        await scope
            .ServiceProvider.GetRequiredService<Workflow>()
            .Project((await db.Accounts.FindAsync(ApiFixture.Alice))!, observedFloor: floor);
        await db.SaveChangesAsync();
        Assert.True(state.DesiredRevision > floor);
        Assert.Equal(2, await db.Projections.CountAsync(p => p.AccountId == ApiFixture.Alice));
    }

    [Fact]
    public async Task Unrelated_pending_grant_keeps_present_resource_effective()
    {
        await using var f = new ApiFixture();
        var alice = f.As(ApiFixture.Alice);
        await Scenarios.Tick(f, 6);
        var (request, preview) = await Scenarios.Pending(f);
        (
            await ApiFixture.Post(
                f.As(ApiFixture.Bob),
                $"/api/requests/{request}/approve",
                new { previewId = preview }
            )
        ).EnsureSuccessStatusCode();
        var access = await alice.GetFromJsonAsync<JsonElement>("/api/access");
        Assert.Equal("Pending", access.GetProperty("sync").GetProperty("status").GetString());
        var library = access
            .GetProperty("resources")
            .EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == "library");
        Assert.True(library.GetProperty("effective").GetBoolean());
        Assert.Equal(
            HttpStatusCode.OK,
            (await alice.GetAsync("/api/resources/library/content")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await alice.GetAsync("/api/resources/results/content")).StatusCode
        );
    }

    [Theory]
    [InlineData("content")]
    [InlineData("effective")]
    public async Task Resume_after_unacknowledged_revocation_requires_destination_presence(
        string check
    )
    {
        await using var f = new ApiFixture();
        var alice = f.As(ApiFixture.Alice);
        var claire = f.As(ApiFixture.Claire);
        await Scenarios.Tick(f, 6);
        (
            await ApiFixture.Post(
                claire,
                $"/api/accounts/{ApiFixture.Alice}/status",
                new { status = "Suspended", reason = "Review access" }
            )
        ).EnsureSuccessStatusCode();
        f.Faults.Point = "after_destination";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Scenarios.Tick(f));
        f.Faults.Point = null;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            Assert.Empty((await db.DestinationStates.FindAsync(ApiFixture.Alice))!.Resources);
            Assert.Contains("library", (await db.SyncStates.FindAsync(ApiFixture.Alice))!.Observed);
        }
        (
            await ApiFixture.Post(
                claire,
                $"/api/accounts/{ApiFixture.Alice}/status",
                new { status = "Active", reason = "Review complete" }
            )
        ).EnsureSuccessStatusCode();
        if (check == "content")
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await alice.GetAsync("/api/resources/library/content")).StatusCode
            );
        else
        {
            var access = await alice.GetFromJsonAsync<JsonElement>("/api/access");
            var library = access
                .GetProperty("resources")
                .EnumerateArray()
                .Single(r => r.GetProperty("id").GetString() == "library");
            Assert.True(library.GetProperty("allowed").GetBoolean());
            Assert.False(library.GetProperty("effective").GetBoolean());
        }
        f.Clock.Now = f.Clock.Now.AddSeconds(31);
        await Scenarios.Tick(f, 8);
        Assert.Equal(
            HttpStatusCode.OK,
            (await alice.GetAsync("/api/resources/library/content")).StatusCode
        );
    }
}
