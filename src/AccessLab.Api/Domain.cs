using Microsoft.EntityFrameworkCore;

namespace AccessLab.Api;

public class LabGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Requestable { get; set; } = true;
    public string[] ResourceIds { get; set; } = [];
}

public class Resource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Description { get; set; } = "";
    public long RuleVersion { get; set; } = 1;
}

public class Membership
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Status { get; set; } = "Active";
    public string? RequestId { get; set; }
    public long Version { get; set; } = 1;
}

public class SyncState
{
    public string AccountId { get; set; } = "";
    public long DesiredRevision { get; set; }
    public long ObservedRevision { get; set; }
    public string[] Desired { get; set; } = [];
    public string[] Observed { get; set; } = [];
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class Scope
{
    public static bool Project(Account actor, string project) =>
        actor.Has("Operator") || actor.ProjectIds.Contains(project);

    public static bool Privileged(Account actor, string project) =>
        actor.Has("Operator") || actor.Has("Reviewer", project) || actor.Has("Auditor", project);

    public static bool Account(Account actor, Account target) =>
        actor.Id == target.Id
        || actor.Has("Operator")
        || target.ProjectIds.Any(p => Privileged(actor, p));

    public static void Require(bool ok, int status = 403, string code = "forbidden")
    {
        if (!ok)
            throw new ApiError(
                status,
                code,
                status == 404 ? "Object not found." : "This action is not permitted."
            );
    }
}

public class AccessService(LabDb db, TimeProvider clock)
{
    public async Task<string[]> Desired(
        Account a,
        DateTimeOffset now,
        IEnumerable<Membership>? memberships = null
    )
    {
        if (!a.Active(now))
            return [];
        var ms =
            memberships?.ToList()
            ?? await db.Memberships.Where(m => m.AccountId == a.Id).ToListAsync();
        var ids = ms.Where(m => m.Status == "Active" && m.StartsAt <= now && now < m.EndsAt)
            .Select(m => m.GroupId)
            .ToArray();
        var gs = await db.Groups.Where(g => ids.Contains(g.Id)).ToListAsync();
        return gs.Where(g => a.ProjectIds.Contains(g.ProjectId))
            .SelectMany(g => g.ResourceIds)
            .Distinct()
            .Order()
            .ToArray();
    }

    public async Task<object> Read(HttpContext context, ActorService actors, string? id)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead
        );
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var actor = await actors.Get(context);
        var a =
            await db.Accounts.FindAsync(id ?? actor.Id)
            ?? throw new ApiError(404, "not_found", "Account not found.");
        Scope.Require(Scope.Account(actor, a), 404);
        var now = clock.GetUtcNow();
        var ms = await db
            .Memberships.Where(m =>
                m.AccountId == a.Id && m.Status == "Active" && m.StartsAt <= now && m.EndsAt > now
            )
            .ToListAsync();
        var gs = await db.Groups.ToListAsync();
        var resources = await db.Resources.ToListAsync();
        var desired = await Desired(a, now);
        var sync = await db.SyncStates.FindAsync(a.Id);
        var destination = await db.DestinationStates.FindAsync(a.Id);
        return new
        {
            accountId = a.Id,
            accountName = a.DisplayName,
            resources = resources
                .Where(r =>
                    a.ProjectIds.Contains(r.ProjectId)
                    && (actor.Id == a.Id || Scope.Privileged(actor, r.ProjectId))
                )
                .Select(r => new
                {
                    r.Id,
                    r.Name,
                    r.ProjectId,
                    r.Description,
                    ruleDescription = "Active account within its validity window and an active, valid membership in "
                        + string.Join(
                            " or ",
                            gs.Where(g =>
                                    g.ProjectId == r.ProjectId && g.ResourceIds.Contains(r.Id)
                                )
                                .OrderBy(g => g.Name)
                                .Select(g => g.Name)
                        )
                        + ". Effective access also requires presence at the simulated destination.",
                    denialReason = a.Status != "Active" ? $"Account is {a.Status}."
                    : !a.Active(now) ? "Account is outside its validity window."
                    : !desired.Contains(r.Id) ? "No active membership is valid for this resource."
                    : !(destination?.Resources.Contains(r.Id) ?? false)
                        ? "The simulated destination has not applied this access."
                    : null,
                    allowed = desired.Contains(r.Id),
                    observed = sync?.Observed.Contains(r.Id) ?? false,
                    effective = desired.Contains(r.Id)
                        && (destination?.Resources.Contains(r.Id) ?? false),
                    reasons = ms.Where(m =>
                            gs.Any(g => g.Id == m.GroupId && g.ResourceIds.Contains(r.Id))
                        )
                        .Select(m => new
                        {
                            groupId = m.GroupId,
                            groupName = gs.Single(g => g.Id == m.GroupId).Name,
                            m.StartsAt,
                            m.EndsAt,
                        }),
                    r.RuleVersion,
                }),
            sync = new
            {
                status = sync?.Status ?? "Pending",
                desiredRevision = sync?.DesiredRevision ?? 0,
                observedRevision = sync?.ObservedRevision ?? 0,
            },
        };
    }
}

public static class ReadEndpoints
{
    public static void MapReads(this WebApplication app)
    {
        app.MapGet(
            "/api/access",
            async (HttpContext c, ActorService actors, AccessService access, string? accountId) =>
                Results.Ok(await access.Read(c, actors, accountId))
        );
        app.MapGet(
            "/api/resources/{id}/content",
            async (
                string id,
                HttpContext c,
                ActorService actors,
                AccessService access,
                LabDb db,
                TimeProvider clock
            ) =>
            {
                await using var snapshot = await db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.RepeatableRead
                );
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
                var a = await actors.Get(c);
                var r = await db.Resources.FindAsync(id);
                Scope.Require(r != null && a.ProjectIds.Contains(r.ProjectId), 404);
                var desired = await access.Desired(a, clock.GetUtcNow());
                var destination = await db.DestinationStates.FindAsync(a.Id);
                Scope.Require(
                    desired.Contains(id) && (destination?.Resources.Contains(id) ?? false)
                );
                return Results.Ok(
                    new
                    {
                        resourceId = id,
                        message = "Independent AccessLab demonstration content. Access checked locally and at the simulated destination.",
                    }
                );
            }
        );
        app.MapGet(
            "/api/groups",
            async (HttpContext c, ActorService actors, LabDb db) =>
            {
                var a = await actors.Get(c);
                var groups = await db.Groups.ToListAsync();
                return Results.Ok(new { items = groups.Where(g => Scope.Project(a, g.ProjectId)) });
            }
        );
        app.MapGet(
            "/api/groups/{id}/members",
            async (
                string id,
                HttpContext c,
                ActorService actors,
                LabDb db,
                TimeProvider clock,
                int? page,
                int? pageSize
            ) =>
            {
                var a = await actors.Get(c);
                var g = await db.Groups.FindAsync(id);
                Scope.Require(g != null && Scope.Project(a, g.ProjectId), 404);
                Scope.Require(Scope.Privileged(a, g!.ProjectId));
                var ms = db
                    .Memberships.Where(m => m.GroupId == id)
                    .OrderBy(m => m.Id)
                    .Join(
                        db.Accounts,
                        m => m.AccountId,
                        t => t.Id,
                        (m, t) =>
                            new
                            {
                                membershipId = m.Id,
                                m.AccountId,
                                t.DisplayName,
                                m.StartsAt,
                                m.EndsAt,
                                status = m.Status == "Active" && m.EndsAt <= clock.GetUtcNow()
                                    ? "Expired"
                                    : m.Status,
                            }
                    );
                return Results.Ok(await Page.Query(ms, page, pageSize));
            }
        );
        app.MapGet(
            "/api/accounts",
            async (HttpContext c, ActorService actors, LabDb db, int? page, int? pageSize) =>
            {
                var a = await actors.Get(c);
                var all = await db.Accounts.OrderBy(x => x.DisplayName).ToListAsync();
                var visible = all.Where(t => Scope.Account(a, t))
                    .Select(t => new
                    {
                        t.Id,
                        t.DisplayName,
                        t.Status,
                        t.StartsAt,
                        t.EndsAt,
                        ProjectIds = t
                            .ProjectIds.Where(p => a.Id == t.Id || Scope.Privileged(a, p))
                            .ToArray(),
                        t.Revision,
                    })
                    .ToList();
                return Results.Ok(Page.Of(visible, page, pageSize));
            }
        );
    }
}

public record Paged<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Page
{
    public static (int Number, int Size) Bounds(int? page, int? size) =>
        (Math.Clamp(page ?? 1, 1, 100000), Math.Clamp(size ?? 25, 1, 100));

    public static async Task<Paged<T>> Query<T>(IQueryable<T> source, int? page, int? size)
    {
        var (p, s) = Bounds(page, size);
        var total = await source.CountAsync();
        return new(await source.Skip((p - 1) * s).Take(s).ToListAsync(), total, p, s);
    }

    public static object Of<T>(IEnumerable<T> source, int? page, int? size)
    {
        int p = Math.Clamp(page ?? 1, 1, 100000),
            s = Math.Clamp(size ?? 25, 1, 100);
        var all = source.ToList();
        return new
        {
            items = all.Skip((p - 1) * s).Take(s),
            total = all.Count,
            page = p,
            pageSize = s,
        };
    }
}
