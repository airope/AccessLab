using System.Net.Http.Json;
using System.Text.Json;
using AccessLab.Api;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class AccountScopeTests
{
    [Theory]
    [InlineData(ApiFixture.Bob, false)]
    [InlineData("00000000-0000-0000-0000-000000000006", false)]
    [InlineData(ApiFixture.Alice, true)]
    [InlineData(ApiFixture.Claire, true)]
    public async Task Account_projects_are_scoped_except_for_self_and_operator(
        string actor,
        bool full
    )
    {
        await using var f = new ApiFixture();
        var client = f.As(actor);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            var alice = (await db.Accounts.FindAsync(ApiFixture.Alice))!;
            alice.ProjectIds = ["atlas", "nova"];
            await db.SaveChangesAsync();
        }
        var response = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        var target = response
            .GetProperty("items")
            .EnumerateArray()
            .Single(a => a.GetProperty("id").GetString() == ApiFixture.Alice);
        var projects = target
            .GetProperty("projectIds")
            .EnumerateArray()
            .Select(p => p.GetString())
            .ToArray();
        Assert.Equal(full ? ["atlas", "nova"] : new[] { "atlas" }, projects);
        if (!full)
            Assert.DoesNotContain(
                response.GetProperty("items").EnumerateArray(),
                a => a.GetProperty("id").GetString() == ApiFixture.Eve
            );
    }
}
