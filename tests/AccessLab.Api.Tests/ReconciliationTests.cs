using AccessLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class ReconciliationTests
{
    [Fact]
    public async Task Periodic_comparison_repairs_destination_drift_with_a_new_immutable_revision()
    {
        await using var f = new ApiFixture();
        _ = f.As(ApiFixture.Alice);
        await Scenarios.Tick(f, 6);
        long before;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            var state = await db.DestinationStates.SingleAsync(s =>
                s.AccountId == ApiFixture.Alice
            );
            before = state.Revision;
            state.Resources = [];
            await db.SaveChangesAsync();
        }
        await Scenarios.Tick(f, 6);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            var state = await db.DestinationStates.SingleAsync(s =>
                s.AccountId == ApiFixture.Alice
            );
            Assert.True(state.Revision > before);
            Assert.Contains("library", state.Resources);
            var old = await db.Projections.SingleAsync(p =>
                p.AccountId == ApiFixture.Alice && p.Revision == before
            );
            Assert.Contains("library", old.Resources);
        }
    }

    [Theory]
    [InlineData("before_lease_commit")]
    [InlineData("after_lease_commit")]
    [InlineData("after_destination")]
    [InlineData("before_ack_commit")]
    public async Task Worker_crash_recovers_after_lease_expiry(string point)
    {
        await using var f = new ApiFixture();
        _ = f.As(ApiFixture.Alice);
        f.Faults.Point = point;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Scenarios.Tick(f));
        f.Faults.Point = null;
        f.Clock.Now = f.Clock.Now.AddSeconds(31);
        await Scenarios.Tick(f, 7);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        var state = await db.SyncStates.FindAsync(ApiFixture.Alice);
        Assert.Equal("InSync", state!.Status);
        Assert.Contains("library", state.Observed);
        Assert.Equal(1, await db.Projections.CountAsync(p => p.AccountId == ApiFixture.Alice));
    }
}
