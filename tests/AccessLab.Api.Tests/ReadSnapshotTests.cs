using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessLab.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccessLab.Api.Tests;

public class ReadSnapshotTests
{
    [Theory]
    [InlineData("/api/resources/results/content", false)]
    [InlineData("/api/access", false)]
    [InlineData("/api/resources/results/content", true)]
    [InlineData("/api/access", true)]
    public async Task Never_effective_grant_cannot_cross_local_and_destination_snapshots(
        string path,
        bool afterActor
    )
    {
        var query = new QueryGate(afterActor);
        var delivery = new AsyncGate();
        await using var f = new ApiFixture
        {
            ConfigureTestServices = services =>
            {
                services.AddDbContext<LabDb>(options => options.AddInterceptors(query));
                services.AddScoped<SimulatedDestination>();
                services.AddScoped<ISimulatedDestination>(sp => new GatedDestination(
                    sp.GetRequiredService<SimulatedDestination>(),
                    delivery
                ));
            },
        };
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
        delivery.Arm();
        var worker = Scenarios.Tick(f);
        Task<HttpResponseMessage>? read = null;
        try
        {
            // The real worker has committed its lease but has not applied the grant.
            await delivery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            query.Arm();
            read = alice.GetAsync(path);
            await query.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (afterActor)
            {
                // Actor was active in its query snapshot, but destination access was absent.
                (
                    await ApiFixture.Post(
                        f.As(ApiFixture.Claire),
                        $"/api/accounts/{ApiFixture.Alice}/status",
                        new { status = "Suspended", reason = "Revoke before destination grant" }
                    )
                ).EnsureSuccessStatusCode();
            }
            else
            {
                // Desired has been read by the request. Commit an independently approved removal.
                var p = await ApiFixture.Post(
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
                p.EnsureSuccessStatusCode();
                var pid = (await p.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("id")
                    .GetString();
                var submitted = await ApiFixture.Post(
                    alice,
                    "/api/requests",
                    new { previewId = pid }
                );
                submitted.EnsureSuccessStatusCode();
                var rid = (await submitted.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("id")
                    .GetString();
                var reviewer = f.As(ApiFixture.Bob);
                var review = await ApiFixture.Post(
                    reviewer,
                    $"/api/requests/{rid}/preview",
                    new { }
                );
                review.EnsureSuccessStatusCode();
                var reviewId = (await review.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("id")
                    .GetString();
                (
                    await ApiFixture.Post(
                        reviewer,
                        $"/api/requests/{rid}/approve",
                        new { previewId = reviewId }
                    )
                ).EnsureSuccessStatusCode();
            }
            await using (var scope = f.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LabDb>();
                Assert.DoesNotContain(
                    "results",
                    (await db.DestinationStates.FindAsync(ApiFixture.Alice))!.Resources
                );
                Assert.DoesNotContain(
                    "results",
                    await scope
                        .ServiceProvider.GetRequiredService<AccessService>()
                        .Desired((await db.Accounts.FindAsync(ApiFixture.Alice))!, f.Clock.Now)
                );
            }
            delivery.Release();
            await worker.WaitAsync(TimeSpan.FromSeconds(15));
            await using (var scope = f.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LabDb>();
                Assert.Contains(
                    "results",
                    (await db.DestinationStates.FindAsync(ApiFixture.Alice))!.Resources
                );
            }
            query.Gate.Release();
            var response = await read.WaitAsync(TimeSpan.FromSeconds(15));
            if (path.EndsWith("content", StringComparison.Ordinal))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            else
            {
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                var result = json.GetProperty("resources")
                    .EnumerateArray()
                    .Single(r => r.GetProperty("id").GetString() == "results");
                Assert.True(result.GetProperty("allowed").GetBoolean());
                Assert.False(result.GetProperty("effective").GetBoolean());
            }
        }
        finally
        {
            delivery.Release();
            query.Gate.Release();
            await worker.WaitAsync(TimeSpan.FromSeconds(15));
            if (read != null)
                await read.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private sealed class AsyncGate
    {
        private int armed;
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Resume { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public void Release() => Resume.TrySetResult();

        public async Task Pause()
        {
            if (Interlocked.Exchange(ref armed, 0) != 1)
                return;
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class QueryGate(bool afterActor) : DbCommandInterceptor
    {
        public AsyncGate Gate { get; } = new();

        public void Arm() => Gate.Arm();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                afterActor
                && command.CommandText.Contains("WHERE a.\"Subject\"", StringComparison.Ordinal)
            )
                await Gate.Pause();
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                !afterActor
                && command.CommandText.Contains(
                    "FROM \"Simulator\".\"DestinationStates\"",
                    StringComparison.Ordinal
                )
            )
                await Gate.Pause();
            return result;
        }
    }

    private sealed class GatedDestination(SimulatedDestination actual, AsyncGate gate)
        : ISimulatedDestination
    {
        public async Task Apply(string accountId, long revision, string[] resources)
        {
            await gate.Pause();
            await actual.Apply(accountId, revision, resources);
        }
    }
}
