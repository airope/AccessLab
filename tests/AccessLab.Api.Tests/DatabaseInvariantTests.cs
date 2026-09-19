using AccessLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class DatabaseInvariantTests
{
    [Fact]
    public async Task Database_itself_rejects_overlapping_memberships()
    {
        await using var f = new ApiFixture();
        _ = f.As(ApiFixture.Alice);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        db.Memberships.Add(
            new()
            {
                AccountId = ApiFixture.Alice,
                GroupId = "knowledge",
                StartsAt = DateTimeOffset.UtcNow,
                EndsAt = DateTimeOffset.UtcNow.AddDays(2),
            }
        );
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23P01", ((Npgsql.PostgresException)e.InnerException!).SqlState);
    }

    [Fact]
    public async Task Simulator_cannot_overwrite_new_revision_with_old_grant()
    {
        await using var f = new ApiFixture();
        _ = f.As(ApiFixture.Alice);
        await using var scope = f.Services.CreateAsyncScope();
        var dest = scope.ServiceProvider.GetRequiredService<ISimulatedDestination>();
        await dest.Apply(ApiFixture.Alice, 10, []);
        await dest.Apply(ApiFixture.Alice, 9, ["results"]);
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        var s = await db.DestinationStates.SingleAsync(s => s.AccountId == ApiFixture.Alice);
        Assert.Equal(10, s.Revision);
        Assert.Empty(s.Resources);
    }
}
