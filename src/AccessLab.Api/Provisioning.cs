using Microsoft.EntityFrameworkCore;

namespace AccessLab.Api;

public class DemoSetting
{
    public int Id { get; set; } = 1;
    public bool FailureMode { get; set; }
}

public class DestinationState
{
    public string AccountId { get; set; } = "";
    public long Revision { get; set; }
    public string[] Resources { get; set; } = [];
}

public class SyncAttempt
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public long Revision { get; set; }
    public DateTimeOffset At { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public interface ISimulatedDestination
{
    Task Apply(string accountId, long revision, string[] resources);
}

public class SimulatedDestination(LabDb db, IConfiguration config, IHostEnvironment environment)
    : ISimulatedDestination
{
    public async Task Apply(string accountId, long revision, string[] resources)
    {
        if (
            environment.IsDevelopment()
            && config.GetValue<bool>("AccessLab:DemoMode")
            && await db.Settings.AnyAsync(s => s.FailureMode)
        )
            throw new SimulatedUnavailable();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "Simulator"."DestinationStates" ("AccountId", "Revision", "Resources") VALUES ({accountId}, {revision}, {resources})
            ON CONFLICT ("AccountId") DO UPDATE SET "Revision" = EXCLUDED."Revision", "Resources" = EXCLUDED."Resources"
            WHERE "DestinationStates"."Revision" < EXCLUDED."Revision"
            """
        );
    }
}

public class SimulatedUnavailable : Exception;

public class Reconciler(
    LabDb db,
    Workflow w,
    TimeProvider clock,
    ISimulatedDestination destination,
    IFaults faults
)
{
    public async Task<bool> Tick()
    {
        var now = clock.GetUtcNow();
        Outbox? item;
        Projection? projection;
        string lease = Guid.NewGuid().ToString();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await MutationRunner.Lock(db);
            db.ChangeTracker.Clear();
            var accounts = await db.Accounts.OrderBy(a => a.Id).ToListAsync();
            foreach (var a in accounts)
            {
                var snapshot = await db.SyncStates.FindAsync(a.Id);
                var actual = await db.DestinationStates.FindAsync(a.Id);
                bool drift =
                    snapshot != null
                    && (
                        actual?.Revision > snapshot.DesiredRevision
                        || (
                            snapshot.Status == "InSync"
                            && (
                                actual == null
                                || actual.Revision != snapshot.DesiredRevision
                                || !actual.Resources.Order().SequenceEqual(snapshot.Desired.Order())
                            )
                        )
                    );
                if (snapshot != null)
                {
                    snapshot.ObservedRevision = actual?.Revision ?? 0;
                    snapshot.Observed = actual?.Resources ?? [];
                }
                await w.Project(a, drift, actual?.Revision ?? 0);
                if (drift)
                    db.Audit.Add(
                        new()
                        {
                            ActorId = "system",
                            ActorName = "Reconciler",
                            Action = "DestinationDriftDetected",
                            TargetId = a.Id,
                            At = now,
                            Reason =
                                "Observed state diverged; a new immutable projection was queued.",
                            CorrelationId = Guid.NewGuid().ToString(),
                            AccountRevision = a.Revision,
                        }
                    );
            }
            foreach (
                var m in await db
                    .Memberships.Where(m => m.Status == "Active" && m.EndsAt <= now)
                    .ToListAsync()
            )
            {
                m.Status = "Expired";
                m.Version++;
                var a = accounts.Single(a => a.Id == m.AccountId);
                a.Revision++;
                db.Audit.Add(
                    new()
                    {
                        ActorId = "system",
                        ActorName = "Reconciler",
                        Action = "MembershipExpired",
                        TargetId = a.Id,
                        ProjectId = (await db.Groups.FindAsync(m.GroupId))!.ProjectId,
                        At = now,
                        Reason = "The membership reached its exclusive end boundary.",
                        CorrelationId = Guid.NewGuid().ToString(),
                        AccountRevision = a.Revision,
                    }
                );
            }
            foreach (
                var r in await db
                    .Requests.Where(r => r.Status == "Pending" && r.ExpiresAt <= now)
                    .ToListAsync()
            )
            {
                r.Status = "Expired";
                db.Audit.Add(
                    new()
                    {
                        ActorId = "system",
                        ActorName = "Reconciler",
                        Action = "RequestExpired",
                        TargetId = r.AccountId,
                        ProjectId = r.ProjectId,
                        At = now,
                        Reason = "Request validity ended without a decision.",
                        CorrelationId = r.CorrelationId,
                    }
                );
            }
            await db.SaveChangesAsync();
            item = await db
                .Outbox.Where(o =>
                    o.Status == "Pending"
                    && o.AvailableAt <= now
                    && (o.LeaseUntil == null || o.LeaseUntil <= now)
                )
                .OrderBy(o => o.Id)
                .FirstOrDefaultAsync();
            if (item == null)
            {
                await tx.CommitAsync();
                return false;
            }
            var state = await db.SyncStates.FindAsync(item.AccountId);
            if (item.Revision < state!.DesiredRevision)
            {
                item.Status = "Superseded";
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return true;
            }
            projection = await db.Projections.SingleAsync(p =>
                p.AccountId == item.AccountId && p.Revision == item.Revision
            );
            item.LeaseOwner = lease;
            item.LeaseUntil = now.AddSeconds(30);
            await db.SaveChangesAsync();
            faults.Hit("before_lease_commit");
            await tx.CommitAsync();
        }
        faults.Hit("after_lease_commit");
        var success = true;
        try
        {
            await destination.Apply(item.AccountId, projection.Revision, projection.Resources);
        }
        catch (SimulatedUnavailable)
        {
            success = false;
        }
        faults.Hit("after_destination");
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await MutationRunner.Lock(db);
            db.ChangeTracker.Clear();
            var current = await db.Outbox.FindAsync(item.Id);
            if (current!.LeaseOwner != lease)
            {
                await tx.CommitAsync();
                return true;
            }
            var state = await db.SyncStates.FindAsync(item.AccountId);
            var observed = await db.DestinationStates.FindAsync(item.AccountId);
            if (observed != null)
            {
                state!.ObservedRevision = observed.Revision;
                state.Observed = observed.Resources;
            }
            current.LeaseUntil = null;
            current.LeaseOwner = null;
            current.Attempts++;
            current.Status =
                success ? "Done"
                : current.Attempts >= 5 ? "Failed"
                : "Pending";
            current.AvailableAt = clock.GetUtcNow().AddSeconds(2);
            db.SyncAttempts.Add(
                new()
                {
                    AccountId = item.AccountId,
                    Revision = item.Revision,
                    At = clock.GetUtcNow(),
                    Success = success,
                    Error = success ? null : "Simulated destination unavailable.",
                }
            );
            if (state!.DesiredRevision == item.Revision)
            {
                state.Attempts = current.Attempts;
                state.LastError = success ? null : "Simulated destination unavailable.";
                state.Status =
                    state.DesiredRevision == state.ObservedRevision ? "InSync"
                    : current.Status == "Failed" ? "Failed"
                    : "Pending";
                state.UpdatedAt = clock.GetUtcNow();
            }
            await db.SaveChangesAsync();
            faults.Hit("before_ack_commit");
            await tx.CommitAsync();
        }
        return true;
    }
}

public class ProvisioningWorker(IServiceScopeFactory scopes, ILogger<ProvisioningWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<Reconciler>().Tick();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogWarning(
                    "Reconciliation failed; durable work remains available for retry."
                );
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public record FailureCommand(bool Enabled);

public static class OperationsEndpoints
{
    public static void MapOperations(this WebApplication app)
    {
        app.MapGet(
            "/api/operations",
            async (HttpContext c, ActorService actors, LabDb db, int? page, int? pageSize) =>
            {
                var a = await actors.Get(c);
                Scope.Require(a.Has("Operator"));
                var query = db
                    .SyncStates.Join(
                        db.Accounts,
                        s => s.AccountId,
                        a => a.Id,
                        (s, a) =>
                            new
                            {
                                s.AccountId,
                                accountName = a.DisplayName,
                                s.Status,
                                s.DesiredRevision,
                                s.ObservedRevision,
                                s.Attempts,
                                s.LastError,
                                s.UpdatedAt,
                            }
                    )
                    .OrderBy(s => s.AccountId);
                var rows = await Page.Query(query, page, pageSize);
                return Results.Ok(
                    new
                    {
                        rows.Items,
                        rows.Total,
                        rows.Page,
                        rows.PageSize,
                        failureMode = app.Environment.IsDevelopment()
                            && app.Configuration.GetValue<bool>("AccessLab:DemoMode")
                            && await db.Settings.AnyAsync(s => s.FailureMode),
                    }
                );
            }
        );
        app.MapGet(
            "/api/operations/{accountId}/attempts",
            async (
                string accountId,
                HttpContext c,
                ActorService actors,
                LabDb db,
                int? page,
                int? pageSize
            ) =>
            {
                var actor = await actors.Get(c);
                Scope.Require(actor.Has("Operator"));
                Scope.Require(await db.Accounts.AnyAsync(a => a.Id == accountId), 404);
                var query = db
                    .SyncAttempts.Where(s => s.AccountId == accountId)
                    .OrderByDescending(s => s.Id)
                    .Select(s => new
                    {
                        s.Id,
                        s.AccountId,
                        s.Revision,
                        attemptedAt = s.At,
                        outcome = s.Success ? "Success" : "Failed",
                        s.Error,
                    });
                return Results.Ok(await Page.Query(query, page, pageSize));
            }
        );
        app.MapPost(
            "/api/operations/{id}/retry",
            (string id, ReasonBody body, HttpContext c, MutationRunner run, LabDb db, Workflow w) =>
                run.Run(
                    c,
                    body,
                    a =>
                    {
                        Scope.Require(a.Has("Operator"));
                        return Task.CompletedTask;
                    },
                    async a =>
                    {
                        var target =
                            await db.Accounts.FindAsync(id)
                            ?? throw new ApiError(404, "not_found", "Account not found.");
                        var reason = Workflow.Reason(body.Reason);
                        await w.Project(target);
                        var s = await db.SyncStates.FindAsync(id);
                        s!.Status = "Pending";
                        s.Attempts = 0;
                        s.LastError = null;
                        s.UpdatedAt = w.Now;
                        foreach (
                            var old in await db
                                .Outbox.Where(o =>
                                    o.AccountId == id
                                    && (o.Status == "Pending" || o.Status == "Failed")
                                )
                                .ToListAsync()
                        )
                        {
                            old.Status = "Superseded";
                        }
                        db.Outbox.Add(
                            new()
                            {
                                AccountId = id,
                                Revision = s.DesiredRevision,
                                AvailableAt = w.Now,
                            }
                        );
                        w.Audit(
                            a,
                            "SyncRetry",
                            id,
                            null,
                            reason,
                            c.TraceIdentifier,
                            target.Revision
                        );
                        return new { accepted = true };
                    }
                )
        );
        if (
            app.Environment.IsDevelopment()
            && app.Configuration.GetValue<bool>("AccessLab:DemoMode")
        )
            app.MapPost(
                "/api/operations/failure-mode",
                (FailureCommand body, HttpContext c, MutationRunner run, LabDb db, Workflow w) =>
                    run.Run(
                        c,
                        body,
                        a =>
                        {
                            Scope.Require(a.Has("Operator"));
                            return Task.CompletedTask;
                        },
                        async a =>
                        {
                            var s = await db.Settings.FindAsync(1);
                            if (s == null)
                            {
                                s = new();
                                db.Settings.Add(s);
                            }
                            s.FailureMode = body.Enabled;
                            w.Audit(
                                a,
                                "SimulationFailureMode",
                                a.Id,
                                null,
                                body.Enabled
                                    ? "Enabled simulated failure"
                                    : "Disabled simulated failure",
                                c.TraceIdentifier
                            );
                            return new { enabled = s.FailureMode };
                        }
                    )
            );
    }
}
