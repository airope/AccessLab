using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class CompletionTests
{
    [Fact]
    public async Task Accepted_context_cannot_be_rewritten_in_database()
    {
        await using var f = new ApiFixture();
        var (id, previewId) = await Scenarios.Pending(f);
        (
            await ApiFixture.Post(
                f.As(ApiFixture.Bob),
                $"/api/requests/{id}/approve",
                new { previewId }
            )
        ).EnsureSuccessStatusCode();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        var request = (await db.Requests.FindAsync(id))!;
        request.DecisionContextJson = "{}";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Membership_group_filter_precedes_pagination_and_count()
    {
        await using var f = new ApiFixture();
        var alice = f.As(ApiFixture.Alice);
        var empty = await alice.GetFromJsonAsync<JsonElement>(
            "/api/memberships?groupId=analysis&pageSize=1"
        );
        Assert.Equal(0, empty.GetProperty("total").GetInt32());
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await alice.GetAsync("/api/memberships?groupId=nova-analysis")).StatusCode
        );
    }

    [Fact]
    public async Task Direct_author_and_beneficiary_approval_has_no_side_effects()
    {
        await using var f = new ApiFixture();
        var (id, previewId) = await Scenarios.Pending(
            f,
            actor: ApiFixture.Bob,
            reviewer: ApiFixture.Dana
        );
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        var before = (
            await db.Memberships.CountAsync(),
            await db.Outbox.CountAsync(),
            await db.Audit.CountAsync(),
            await db.Idempotency.CountAsync()
        );
        foreach (var actor in new[] { ApiFixture.Bob, ApiFixture.Alice })
        {
            var response = await ApiFixture.Post(
                f.As(actor),
                $"/api/requests/{id}/approve",
                new { previewId }
            );
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(
            before,
            (
                await db.Memberships.CountAsync(),
                await db.Outbox.CountAsync(),
                await db.Audit.CountAsync(),
                await db.Idempotency.CountAsync()
            )
        );
        Assert.Equal("Pending", (await db.Requests.FindAsync(id))!.Status);
    }

    [Fact]
    public async Task Idempotency_key_reused_by_another_actor_never_returns_cached_response()
    {
        await using var f = new ApiFixture();
        var (id, previewId) = await Scenarios.Pending(f);
        const string key = "shared-fictional-key";
        var bob = await ApiFixture.Post(
            f.As(ApiFixture.Bob),
            $"/api/requests/{id}/approve",
            new { previewId },
            key
        );
        bob.EnsureSuccessStatusCode();
        var dana = await ApiFixture.Post(
            f.As(ApiFixture.Dana),
            $"/api/requests/{id}/approve",
            new { previewId },
            key
        );
        Assert.Equal(HttpStatusCode.Conflict, dana.StatusCode);
        Assert.Equal(
            "request_terminal",
            (await dana.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()
        );
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDb>();
        Assert.Single(await db.Idempotency.Where(i => i.Key == key).ToListAsync());
    }

    [Fact]
    public async Task Ended_account_cannot_use_existing_session()
    {
        await using var f = new ApiFixture();
        var alice = f.As(ApiFixture.Alice);
        (await alice.GetAsync("/api/session")).EnsureSuccessStatusCode();
        (
            await ApiFixture.Post(
                f.As(ApiFixture.Claire),
                $"/api/accounts/{ApiFixture.Alice}/status",
                new { status = "Ended", reason = "Fictional departure" }
            )
        ).EnsureSuccessStatusCode();
        foreach (
            var path in new[] { "/api/session", "/api/access", "/api/resources/library/content" }
        )
            Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData(100, "Active", true, 200)]
    [InlineData(101, "Active", false, 0)]
    [InlineData(101, "Removed", true, 1)]
    public async Task Scheduled_boundaries_are_complete_or_explicitly_rejected(
        int count,
        string status,
        bool succeeds,
        int expected
    )
    {
        await using var f = new ApiFixture();
        var alice = f.As(ApiFixture.Alice);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            db.Memberships.Remove((await db.Memberships.FindAsync("alice-knowledge"))!);
            for (var i = 0; i < count; i++)
                db.Memberships.Add(
                    new Membership
                    {
                        AccountId = ApiFixture.Alice,
                        GroupId = "knowledge",
                        StartsAt = f.Clock.Now.AddMinutes(i * 2 + 1),
                        EndsAt = f.Clock.Now.AddMinutes(i * 2 + 2),
                        Status = status,
                    }
                );
            await db.SaveChangesAsync();
        }
        var response = await ApiFixture.Post(
            alice,
            "/api/previews",
            new ChangeCommand(
                ApiFixture.Alice,
                "analysis",
                "Add",
                f.Clock.Now,
                f.Clock.Now.AddMinutes(count * 2),
                "Many future boundaries"
            )
        );
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (succeeds)
        {
            response.EnsureSuccessStatusCode();
            Assert.Equal(expected, json.GetProperty("scheduledChanges").GetArrayLength());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("preview_too_complex", json.GetProperty("code").GetString());
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            Assert.Empty(await db.Previews.ToListAsync());
            Assert.Empty(await db.Idempotency.ToListAsync());
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(90)]
    public async Task Configured_duration_is_exposed_and_enforced_at_exact_boundary(int days)
    {
        await using var f = new ApiFixture { MaxMembershipDays = days };
        var alice = f.As(ApiFixture.Alice);
        var session = await alice.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(days, session.GetProperty("maxMembershipDays").GetInt32());
        var command = new ChangeCommand(
            ApiFixture.Alice,
            "analysis",
            "Add",
            f.Clock.Now,
            f.Clock.Now.AddDays(days),
            "Duration boundary"
        );
        (await ApiFixture.Post(alice, "/api/previews", command)).EnsureSuccessStatusCode();
        var denied = await ApiFixture.Post(
            alice,
            "/api/previews",
            command with
            {
                EndsAt = command.EndsAt.AddSeconds(1),
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(
            "period_invalid",
            (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task Invalid_duration_configuration_prevents_startup(int days)
    {
        await using var f = new ApiFixture { MaxMembershipDays = days };
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            f.As(ApiFixture.Alice)
        );
    }

    [Theory]
    [InlineData("Suspended", false, "Account is Suspended.")]
    [InlineData("Active", true, "Account is outside its validity window.")]
    [InlineData("Active", false, "The simulated destination has not applied this access.")]
    public async Task Access_explanations_prioritize_account_validity_over_membership(
        string status,
        bool expired,
        string expected
    )
    {
        await using var f = new ApiFixture();
        var client = f.As(ApiFixture.Claire);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            var account = (await db.Accounts.FindAsync(ApiFixture.Alice))!;
            account.Status = status;
            if (expired)
                account.EndsAt = f.Clock.Now;
            await db.SaveChangesAsync();
        }
        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        var alice = accounts
            .GetProperty("items")
            .EnumerateArray()
            .Single(a => a.GetProperty("id").GetString() == ApiFixture.Alice);
        Assert.Equal(DateTimeOffset.UnixEpoch, alice.GetProperty("startsAt").GetDateTimeOffset());
        Assert.True(alice.TryGetProperty("endsAt", out _));
        var access = await client.GetFromJsonAsync<JsonElement>(
            "/api/access?accountId=" + ApiFixture.Alice
        );
        var library = access
            .GetProperty("resources")
            .EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == "library");
        Assert.Equal(expected, library.GetProperty("denialReason").GetString());
        Assert.Contains("Knowledge", library.GetProperty("ruleDescription").GetString());
        Assert.False(library.GetProperty("effective").GetBoolean());
        var results = access
            .GetProperty("resources")
            .EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == "results");
        Assert.Equal(
            status != "Active" || expired
                ? expected
                : "No active membership is valid for this resource.",
            results.GetProperty("denialReason").GetString()
        );
    }

    [Fact]
    public async Task Request_group_filter_and_scoped_counts_do_not_reveal_unrelated_member()
    {
        await using var f = new ApiFixture();
        var (id, _) = await Scenarios.Pending(f);
        const string other = "unrelated-member";
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            db.Accounts.Add(
                new Account
                {
                    Id = other,
                    Subject = other,
                    Issuer = "http://localhost:58080/realms/accesslab",
                    DisplayName = "Fictional member",
                    ProjectIds = ["atlas"],
                    Roles = [new RoleGrant { Role = "Member", ProjectId = "atlas" }],
                }
            );
            await db.SaveChangesAsync();
        }
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await f.As(other).GetAsync($"/api/requests/{id}")).StatusCode
        );
        foreach (var suffix in new[] { "", "?search=" + id, "?groupId=analysis" })
        {
            var hidden = await f.As(other).GetFromJsonAsync<JsonElement>("/api/requests" + suffix);
            Assert.Equal(0, hidden.GetProperty("total").GetInt32());
            Assert.Empty(hidden.GetProperty("items").EnumerateArray());
        }
        var filtered = await f.As(ApiFixture.Bob)
            .GetFromJsonAsync<JsonElement>("/api/requests?groupId=knowledge");
        Assert.Equal(0, filtered.GetProperty("total").GetInt32());
        var included = await f.As(ApiFixture.Bob)
            .GetFromJsonAsync<JsonElement>("/api/requests?groupId=analysis&pageSize=1");
        Assert.Equal(1, included.GetProperty("total").GetInt32());
        Assert.Equal(id, included.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await f.As(ApiFixture.Eve).GetAsync("/api/requests?groupId=analysis")).StatusCode
        );
        var audit = await f.As(ApiFixture.Eve)
            .GetFromJsonAsync<JsonElement>("/api/audit?search=RequestSubmitted");
        Assert.Equal(0, audit.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("/api/groups/analysis/members", 110)]
    [InlineData("/api/memberships", 111)]
    [InlineData("/api/operations", 110)]
    [InlineData("/api/operations/" + ApiFixture.Alice + "/attempts", 110)]
    public async Task Growing_lists_have_bounded_scoped_pages(string path, int total)
    {
        await using var f = new ApiFixture();
        var client = f.As(ApiFixture.Claire);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            for (var i = 0; i < 110; i++)
            {
                db.Memberships.Add(
                    new Membership
                    {
                        AccountId = ApiFixture.Alice,
                        GroupId = "analysis",
                        StartsAt = f.Clock.Now.AddDays(i * 2),
                        EndsAt = f.Clock.Now.AddDays(i * 2 + 1),
                    }
                );
                var id = "page-account-" + i;
                db.Accounts.Add(
                    new Account
                    {
                        Id = id,
                        Subject = id,
                        DisplayName = id,
                    }
                );
                db.SyncStates.Add(new SyncState { AccountId = id });
                db.SyncAttempts.Add(
                    new SyncAttempt
                    {
                        AccountId = ApiFixture.Alice,
                        Revision = i,
                        At = f.Clock.Now,
                        Success = i % 2 == 0,
                    }
                );
            }
            db.SyncAttempts.Add(new SyncAttempt { AccountId = ApiFixture.Eve, At = f.Clock.Now });
            await db.SaveChangesAsync();
        }
        var query = path == "/api/memberships" ? "?accountId=" + ApiFixture.Alice + "&" : "?";
        var first = await client.GetFromJsonAsync<JsonElement>(path + query);
        Assert.Equal(total, first.GetProperty("total").GetInt32());
        Assert.Equal(25, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(25, first.GetProperty("items").GetArrayLength());
        var last = await client.GetFromJsonAsync<JsonElement>(path + query + "page=2&pageSize=999");
        Assert.Equal(100, last.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, last.GetProperty("page").GetInt32());
        Assert.Equal(total - 100, last.GetProperty("items").GetArrayLength());
        if (path.EndsWith("attempts"))
        {
            var row = first.GetProperty("items")[0];
            Assert.Equal(ApiFixture.Alice, row.GetProperty("accountId").GetString());
            Assert.Equal("Failed", row.GetProperty("outcome").GetString());
            Assert.Equal(f.Clock.Now, row.GetProperty("attemptedAt").GetDateTimeOffset());
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await f.As(ApiFixture.Alice).GetAsync(path)).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.GetAsync("/api/operations/unknown/attempts")).StatusCode
            );
        }
    }

    [Fact]
    public async Task Accepted_decision_preserves_preview_context_after_dependencies_change()
    {
        await using var f = new ApiFixture();
        var (id, previewId) = await Scenarios.Pending(f);
        var response = await ApiFixture.Post(
            f.As(ApiFixture.Bob),
            $"/api/requests/{id}/approve",
            new { previewId }
        );
        response.EnsureSuccessStatusCode();
        var approved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(previewId, approved.GetProperty("acceptedPreviewId").GetString());
        var context = approved.GetProperty("decisionContext");
        Assert.Equal(1, context.GetProperty("accountRevision").GetInt64());
        Assert.Equal(1, context.GetProperty("ruleVersion").GetInt64());
        Assert.DoesNotContain("nova", context.GetRawText());
        Assert.Equal(f.Clock.Now, context.GetProperty("calculatedAt").GetDateTimeOffset());
        Assert.True(context.GetProperty("expiresAt").GetDateTimeOffset() > f.Clock.Now);
        Assert.Equal(64, context.GetProperty("fingerprint").GetString()!.Length);
        Assert.Equal("analysis", context.GetProperty("command").GetProperty("groupId").GetString());
        Assert.NotEmpty(
            context.GetProperty("dependencyVersions").GetProperty("memberships").EnumerateArray()
        );
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LabDb>();
            (await db.Resources.FindAsync("library"))!.RuleVersion++;
            (await db.Memberships.FindAsync("alice-knowledge"))!.Version++;
            await db.SaveChangesAsync();
        }
        var reread = await f.As(ApiFixture.Alice)
            .GetFromJsonAsync<JsonElement>($"/api/requests/{id}");
        Assert.True(JsonElement.DeepEquals(context, reread.GetProperty("decisionContext")));
        var audit = await f.As(ApiFixture.Claire)
            .GetFromJsonAsync<JsonElement>("/api/audit?search=RequestApproved");
        Assert.Equal(2, audit.GetProperty("items")[0].GetProperty("accountRevision").GetInt64());
    }
}
