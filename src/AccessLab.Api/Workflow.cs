using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AccessLab.Api;

public record ChangeCommand(
    string AccountId,
    string GroupId,
    string Operation,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason,
    string? MembershipId = null
);

public class ChangeRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Operation { get; set; } = "Add";
    public string? MembershipId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? DecisionReason { get; set; }
    public string? DecidedBy { get; set; }
    public string? AcceptedPreviewId { get; set; }
    public string? DecisionContextJson { get; set; }
    public string CorrelationId { get; set; } = "";

    public ChangeCommand Command() =>
        new(AccountId, GroupId, Operation, StartsAt, EndsAt, Reason, MembershipId);
}

public class ImpactPreview
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ActorId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string? RequestId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long AccountRevision { get; set; }
    public string Fingerprint { get; set; } = "";
    public string CommandJson { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public string? DecisionContextJson { get; set; }
}

public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string ActorId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string? ProjectId { get; set; }
    public string Outcome { get; set; } = "Success";
    public string Reason { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public long AccountRevision { get; set; }
}

public class IdempotentResult
{
    public long Id { get; set; }
    public string ActorId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Response { get; set; } = "";
    public int StatusCode { get; set; }
}

public class Projection
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public long Revision { get; set; }
    public string[] Resources { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public class Outbox
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public long Revision { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LeaseOwner { get; set; }
}

public interface IFaults
{
    void Hit(string point);
}

public class NoFaults : IFaults
{
    public void Hit(string point) { }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Decode<T>(string s) => JsonSerializer.Deserialize<T>(s, Options)!;

    public static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

public class MutationRunner(LabDb db, ActorService actors, IFaults faults)
{
    // One database authority lock for this deliberately small, single-organization demo.
    // Every writer (including worker) uses it before account locks in stable ID order.
    public static async Task Lock(LabDb db)
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(74192031)");
        await db
            .Accounts.FromSqlRaw("SELECT * FROM \"Accounts\" ORDER BY \"Id\" FOR UPDATE")
            .LoadAsync();
        await db.Roles.FromSqlRaw("SELECT * FROM \"Roles\" ORDER BY \"Id\" FOR UPDATE").LoadAsync();
        await db
            .Groups.FromSqlRaw("SELECT * FROM \"Groups\" ORDER BY \"Id\" FOR UPDATE")
            .LoadAsync();
        await db
            .Resources.FromSqlRaw("SELECT * FROM \"Resources\" ORDER BY \"Id\" FOR UPDATE")
            .LoadAsync();
    }

    public async Task<IResult> Run(
        HttpContext c,
        object payload,
        Func<Account, Task> authorize,
        Func<Account, Task<object>> action,
        int status = 200
    )
    {
        var key = c.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is < 1 or > 128)
            throw new ApiError(
                400,
                "idempotency_key_required",
                "Provide an Idempotency-Key of at most 128 characters."
            );
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(db);
        db.ChangeTracker.Clear();
        var actor = await actors.Get(c);
        await authorize(actor);
        string op = c.Request.Path,
            hash = Json.Hash(Json.Encode(payload));
        var prior = await db.Idempotency.SingleOrDefaultAsync(i =>
            i.ActorId == actor.Id && i.Operation == op && i.Key == key
        );
        if (prior != null)
        {
            if (prior.Hash != hash)
                throw new ApiError(
                    409,
                    "idempotency_mismatch",
                    "This key was already used with different arguments."
                );
            await tx.CommitAsync();
            return Results.Content(
                prior.Response,
                "application/json",
                statusCode: prior.StatusCode
            );
        }
        var result = await action(actor);
        faults.Hit("before_save");
        var json = Json.Encode(result);
        db.Idempotency.Add(
            new()
            {
                ActorId = actor.Id,
                Operation = op,
                Key = key,
                Hash = hash,
                Response = json,
                StatusCode = status,
            }
        );
        await db.SaveChangesAsync();
        faults.Hit("after_save");
        faults.Hit("before_commit");
        await tx.CommitAsync();
        faults.Hit("after_commit");
        return Results.Content(json, "application/json", statusCode: status);
    }
}

public class Workflow(
    LabDb db,
    TimeProvider clock,
    AccessService access,
    Microsoft.Extensions.Options.IOptions<AccessLabOptions> options
)
{
    public DateTimeOffset Now => clock.GetUtcNow();

    public static string Reason(string? reason)
    {
        var s = reason?.Trim() ?? "";
        if (s.Length is < 3 or > 1000)
            throw new ApiError(400, "reason_invalid", "A reason of 3–1000 characters is required.");
        return s;
    }

    public bool Visible(Account a, ChangeRequest r) =>
        r.ActorId == a.Id || r.AccountId == a.Id || Scope.Privileged(a, r.ProjectId);

    public bool Reviewer(Account a, ChangeRequest r) =>
        a.Has("Reviewer", r.ProjectId) && r.ActorId != a.Id && r.AccountId != a.Id;

    public string Status(ChangeRequest r) =>
        r.Status == "Pending" && r.ExpiresAt <= Now ? "Expired" : r.Status;

    public async Task<ChangeRequest> Request(Account a, string id)
    {
        var r = await db.Requests.FindAsync(id);
        Scope.Require(r != null && Visible(a, r), 404);
        return r!;
    }

    public void Pending(ChangeRequest r)
    {
        if (Status(r) != "Pending")
            throw new ApiError(409, "request_terminal", "This request is no longer pending.");
    }

    public async Task<object> Dto(Account actor, ChangeRequest r)
    {
        var target = await db.Accounts.FindAsync(r.AccountId);
        var author = await db.Accounts.FindAsync(r.ActorId);
        var g = await db.Groups.FindAsync(r.GroupId);
        var s = await db.SyncStates.FindAsync(r.AccountId);
        return new
        {
            r.Id,
            r.AccountId,
            accountName = target!.DisplayName,
            r.ActorId,
            actorName = author!.DisplayName,
            r.GroupId,
            groupName = g!.Name,
            r.ProjectId,
            r.Operation,
            r.StartsAt,
            r.EndsAt,
            r.Reason,
            status = Status(r),
            r.CreatedAt,
            r.ExpiresAt,
            r.DecisionReason,
            r.DecidedBy,
            r.AcceptedPreviewId,
            decisionContext = r.DecisionContextJson == null
                ? (JsonElement?)null
                : Json.Decode<JsonElement>(r.DecisionContextJson),
            r.CorrelationId,
            canReview = Status(r) == "Pending" && Reviewer(actor, r),
            canCancel = Status(r) == "Pending" && r.ActorId == actor.Id,
            syncStatus = r.Status == "Approved" ? s?.Status : null,
        };
    }

    public async Task AuthorizeCommand(Account actor, ChangeCommand cmd)
    {
        if (
            string.IsNullOrWhiteSpace(cmd.AccountId)
            || string.IsNullOrWhiteSpace(cmd.GroupId)
            || string.IsNullOrWhiteSpace(cmd.Operation)
            || string.IsNullOrWhiteSpace(cmd.Reason)
        )
            throw new ApiError(
                400,
                "invalid_request",
                "Account, group, operation and reason are required."
            );
        var target = await db.Accounts.FindAsync(cmd.AccountId);
        var group = await db.Groups.FindAsync(cmd.GroupId);
        Scope.Require(target != null && Scope.Account(actor, target), 404);
        Scope.Require(group != null && Scope.Project(actor, group.ProjectId), 404);
        Scope.Require(
            target!.ProjectIds.Contains(group!.ProjectId)
                && (actor.Id == target.Id || actor.Has("Reviewer", group.ProjectId))
        );
    }

    public async Task AuthorizeSubmission(Account actor, string previewId)
    {
        if (string.IsNullOrWhiteSpace(previewId))
            throw new ApiError(400, "invalid_request", "A preview ID is required.");
        var preview = await db.Previews.FindAsync(previewId);
        Scope.Require(preview != null && preview.ActorId == actor.Id, 404);
        await AuthorizeCommand(actor, Json.Decode<ChangeCommand>(preview!.CommandJson));
    }

    public async Task<(Account, LabGroup, ChangeCommand)> Validate(
        Account actor,
        ChangeCommand cmd,
        bool reviewing = false
    )
    {
        var target = await db.Accounts.FindAsync(cmd.AccountId);
        var g = await db.Groups.FindAsync(cmd.GroupId);
        Scope.Require(target != null && Scope.Account(actor, target), 404);
        Scope.Require(g != null && Scope.Project(actor, g.ProjectId), 404);
        Scope.Require(target!.ProjectIds.Contains(g!.ProjectId));
        if (!reviewing)
            Scope.Require(actor.Id == target.Id || actor.Has("Reviewer", g.ProjectId));
        if (!target.Active(Now))
            throw new ApiError(
                409,
                "target_inactive",
                "The target account is not currently active."
            );
        Scope.Require(g.Requestable);
        var reason = Reason(cmd.Reason);
        if (cmd.Operation is not ("Add" or "Remove"))
            throw new ApiError(400, "operation_invalid", "Operation must be Add or Remove.");
        if (cmd.Operation == "Add")
        {
            if (
                cmd.StartsAt.Offset != TimeSpan.Zero
                || cmd.EndsAt.Offset != TimeSpan.Zero
                || cmd.StartsAt >= cmd.EndsAt
                || cmd.EndsAt <= Now
                || cmd.EndsAt - cmd.StartsAt > TimeSpan.FromDays(options.Value.MaxMembershipDays)
            )
                throw new ApiError(
                    400,
                    "period_invalid",
                    $"Use UTC dates, a future end, and a positive period no longer than {options.Value.MaxMembershipDays} days."
                );
            if (
                await db.Memberships.AnyAsync(m =>
                    m.AccountId == target.Id
                    && m.GroupId == g.Id
                    && m.Status == "Active"
                    && m.StartsAt < cmd.EndsAt
                    && cmd.StartsAt < m.EndsAt
                )
            )
                throw new ApiError(
                    409,
                    "membership_overlap",
                    "An active membership overlaps this period."
                );
        }
        else
        {
            var matches = await db
                .Memberships.Where(m =>
                    m.AccountId == target.Id
                    && m.GroupId == g.Id
                    && m.Status == "Active"
                    && m.EndsAt > Now
                    && (cmd.MembershipId == null || m.Id == cmd.MembershipId)
                )
                .ToListAsync();
            if (matches.Count != 1)
                throw new ApiError(
                    409,
                    "membership_ambiguous",
                    "Select one existing, unexpired membership to remove."
                );
            var m = matches[0];
            cmd = cmd with { MembershipId = m.Id, StartsAt = m.StartsAt, EndsAt = m.EndsAt };
        }
        return (target, g, cmd with { Reason = reason });
    }

    public async Task<string> Fingerprint(Account actor, Account target)
    {
        var ms = await db
            .Memberships.Where(m => m.AccountId == target.Id)
            .OrderBy(m => m.Id)
            .ToListAsync();
        var gs = await db.Groups.OrderBy(g => g.Id).ToListAsync();
        var rs = await db.Resources.OrderBy(r => r.Id).ToListAsync();
        return Json.Hash(
            Json.Encode(
                new
                {
                    target.Id,
                    target.Status,
                    target.StartsAt,
                    target.EndsAt,
                    target.Revision,
                    target.ProjectIds,
                    memberships = ms,
                    groups = gs,
                    rules = rs,
                    actor = new
                    {
                        actor.Id,
                        actor.Status,
                        actor.StartsAt,
                        actor.EndsAt,
                        actor.Revision,
                        actor.ProjectIds,
                        roles = actor.Roles.OrderBy(r => r.Id),
                    },
                }
            )
        );
    }

    public async Task<object> Preview(
        Account actor,
        ChangeCommand command,
        string? requestId = null
    )
    {
        var (target, g, cmd) = await Validate(actor, command, requestId != null);
        var now = Now;
        var ms = await db.Memberships.Where(m => m.AccountId == target.Id).ToListAsync();
        var after = ms.Where(m => cmd.Operation != "Remove" || m.Id != cmd.MembershipId).ToList();
        if (cmd.Operation == "Add")
            after.Add(
                new()
                {
                    AccountId = target.Id,
                    GroupId = g.Id,
                    StartsAt = cmd.StartsAt,
                    EndsAt = cmd.EndsAt,
                }
            );
        var groups = await db.Groups.ToListAsync();
        var resources = await db.Resources.Where(r => r.ProjectId == g.ProjectId).ToListAsync();
        async Task<object[]> Diff(DateTimeOffset at)
        {
            var beforeRights = await access.Desired(target, at, ms);
            var afterRights = await access.Desired(target, at, after);
            return resources
                .Select(r =>
                    (object)
                        new
                        {
                            resourceId = r.Id,
                            resourceName = r.Name,
                            effect = beforeRights.Contains(r.Id)
                                ? afterRights.Contains(r.Id)
                                    ? "Retained"
                                    : "Removed"
                                : afterRights.Contains(r.Id)
                                    ? "Added"
                                    : "Unchanged",
                            reasons = after
                                .Where(m =>
                                    m.Status == "Active" && m.StartsAt <= at && at < m.EndsAt
                                )
                                .Where(m =>
                                    groups.Any(gp =>
                                        gp.Id == m.GroupId && gp.ResourceIds.Contains(r.Id)
                                    )
                                )
                                .Select(m =>
                                    $"{groups.Single(gp => gp.Id == m.GroupId).Name} · {m.StartsAt:O} – {m.EndsAt:O} · rule {r.RuleVersion}"
                                )
                                .ToArray(),
                        }
                )
                .ToArray();
        }
        var boundaries = ms.Where(m =>
                m.Status == "Active"
                && groups.Any(gp => gp.Id == m.GroupId && gp.ProjectId == g.ProjectId)
            )
            .SelectMany(m => new[] { m.StartsAt, m.EndsAt })
            .Concat([
                cmd.StartsAt,
                cmd.EndsAt,
                target.StartsAt,
                target.EndsAt,
                actor.StartsAt,
                actor.EndsAt,
            ])
            .Where(t => t > now)
            .Distinct()
            .Order()
            .ToList();
        var scheduledBoundaries = boundaries.Where(t => t <= cmd.EndsAt).ToList();
        if (scheduledBoundaries.Count > 200)
            throw new ApiError(
                400,
                "preview_too_complex",
                "Preview supports at most 200 relevant future boundaries. Narrow the requested period."
            );
        var expiry = boundaries.Append(now.AddMinutes(5)).Min();
        var preview = new ImpactPreview
        {
            ActorId = actor.Id,
            AccountId = target.Id,
            RequestId = requestId,
            AccountRevision = target.Revision,
            ExpiresAt = expiry,
            Fingerprint = await Fingerprint(actor, target),
            CommandJson = Json.Encode(cmd),
        };
        // Whitelisted diagnostics only: no issuer, subject, credentials or cached responses.
        preview.DecisionContextJson = Json.Encode(
            new
            {
                accountRevision = target.Revision,
                ruleVersion = resources.Select(r => r.RuleVersion).DefaultIfEmpty(0).Max(),
                preview.Fingerprint,
                calculatedAt = now,
                preview.ExpiresAt,
                command = cmd,
                dependencyVersions = new
                {
                    memberships = ms.Where(m =>
                            groups.Any(gp => gp.Id == m.GroupId && gp.ProjectId == g.ProjectId)
                        )
                        .OrderBy(m => m.Id)
                        .Select(m => new { m.Id, m.Version }),
                    rules = resources.OrderBy(r => r.Id).Select(r => new { r.Id, r.RuleVersion }),
                    groups = groups
                        .Where(gp => gp.ProjectId == g.ProjectId)
                        .OrderBy(gp => gp.Id)
                        .Select(gp => new { gp.Id, fingerprint = Json.Hash(Json.Encode(gp)) }),
                    targetAuthority = new
                    {
                        target.Revision,
                        fingerprint = Json.Hash(
                            Json.Encode(
                                new
                                {
                                    target.Status,
                                    target.StartsAt,
                                    target.EndsAt,
                                    target.ProjectIds,
                                }
                            )
                        ),
                    },
                    reviewerAuthority = new
                    {
                        actor.Id,
                        actor.Revision,
                        fingerprint = Json.Hash(
                            Json.Encode(
                                new
                                {
                                    actor.Status,
                                    actor.StartsAt,
                                    actor.EndsAt,
                                    actor.ProjectIds,
                                    roles = actor.Roles.OrderBy(r => r.Id),
                                }
                            )
                        ),
                    },
                },
            }
        );
        if (Encoding.UTF8.GetByteCount(preview.DecisionContextJson) > 65536)
            throw new ApiError(
                400,
                "preview_too_complex",
                "Preview diagnostics exceed the supported 64 KiB limit."
            );
        var scheduled = new List<object>();
        foreach (var at in scheduledBoundaries)
            scheduled.Add(new { at, changes = await Diff(at) });
        var dto = new
        {
            preview.Id,
            preview.ExpiresAt,
            preview.AccountRevision,
            requestId,
            changes = await Diff(now),
            scheduledChanges = scheduled,
            warnings = new[]
            {
                "Approval records desired state; the simulated destination applies it asynchronously.",
            },
        };
        preview.ResultJson = Json.Encode(dto);
        db.Previews.Add(preview);
        return dto;
    }

    public async Task<ImpactPreview> Fresh(Account actor, string id, string? requestId)
    {
        var p = await db.Previews.FindAsync(id);
        Scope.Require(p != null && p.ActorId == actor.Id, 404);
        if (p!.RequestId != requestId)
            throw new ApiError(409, "preview_kind", "A separate reviewer preview is required.");
        var a = await db.Accounts.FindAsync(p.AccountId);
        if (p.ExpiresAt <= Now || a == null || p.Fingerprint != await Fingerprint(actor, a))
            throw new ApiError(
                409,
                "preview_stale",
                "The impact preview expired or its dependencies changed. Refresh impact."
            );
        return p;
    }

    public void Audit(
        Account actor,
        string action,
        string target,
        string? project,
        string reason,
        string correlation,
        long revision = 0
    ) =>
        db.Audit.Add(
            new()
            {
                At = Now,
                ActorId = actor.Id,
                ActorName = actor.DisplayName,
                Action = action,
                TargetId = target,
                ProjectId = project,
                Reason = reason,
                CorrelationId = correlation,
                AccountRevision = revision,
            }
        );

    public async Task Project(Account target, bool force = false, long observedFloor = 0)
    {
        var desired = await access.Desired(target, Now);
        var state = await db.SyncStates.FindAsync(target.Id);
        if (state == null)
        {
            state = new() { AccountId = target.Id };
            db.SyncStates.Add(state);
        }
        if (
            !force
            && state.DesiredRevision > 0
            && state.DesiredRevision >= observedFloor
            && state.Desired.SequenceEqual(desired)
        )
            return;
        state.DesiredRevision = Math.Max(state.DesiredRevision, observedFloor) + 1;
        state.Desired = desired;
        state.Status = "Pending";
        state.Attempts = 0;
        state.LastError = null;
        state.UpdatedAt = Now;
        db.Projections.Add(
            new()
            {
                AccountId = target.Id,
                Revision = state.DesiredRevision,
                Resources = desired,
                CreatedAt = Now,
            }
        );
        db.Outbox.Add(
            new()
            {
                AccountId = target.Id,
                Revision = state.DesiredRevision,
                AvailableAt = Now,
            }
        );
    }

    public async Task<object> Submit(Account actor, string previewId, string correlation)
    {
        var p = await Fresh(actor, previewId, null);
        var (target, g, cmd) = await Validate(actor, Json.Decode<ChangeCommand>(p.CommandJson));
        var r = new ChangeRequest
        {
            AccountId = target.Id,
            ActorId = actor.Id,
            GroupId = g.Id,
            ProjectId = g.ProjectId,
            Operation = cmd.Operation,
            MembershipId = cmd.MembershipId,
            StartsAt = cmd.StartsAt,
            EndsAt = cmd.EndsAt,
            Reason = cmd.Reason,
            CreatedAt = Now,
            ExpiresAt = cmd.Operation == "Add" ? cmd.EndsAt : Now.AddDays(7),
            CorrelationId = correlation,
        };
        db.Requests.Add(r);
        Audit(
            actor,
            "RequestSubmitted",
            target.Id,
            g.ProjectId,
            cmd.Reason,
            correlation,
            target.Revision
        );
        return await Dto(actor, r);
    }

    public async Task<object> Approve(
        Account actor,
        string id,
        string previewId,
        string correlation
    )
    {
        var r = await Request(actor, id);
        Scope.Require(Reviewer(actor, r));
        Pending(r);
        var p = await Fresh(actor, previewId, r.Id);
        var (target, g, cmd) = await Validate(
            actor,
            Json.Decode<ChangeCommand>(p.CommandJson),
            true
        );
        if (Json.Encode(cmd) != Json.Encode(r.Command()))
            throw new ApiError(
                409,
                "preview_command",
                "Preview arguments do not match this request."
            );
        if (cmd.Operation == "Add")
            db.Memberships.Add(
                new()
                {
                    AccountId = target.Id,
                    GroupId = g.Id,
                    StartsAt = cmd.StartsAt,
                    EndsAt = cmd.EndsAt,
                    RequestId = r.Id,
                }
            );
        else
        {
            var m = await db.Memberships.FindAsync(cmd.MembershipId);
            m!.Status = "Removed";
            m.Version++;
        }
        r.AcceptedPreviewId = p.Id;
        r.DecisionContextJson = p.DecisionContextJson;
        target.Revision++;
        r.Status = "Approved";
        r.DecidedBy = actor.Id;
        r.DecisionReason = r.Reason;
        // Flush within transaction so projection calculation sees the membership change.
        await db.SaveChangesAsync();
        await Project(target);
        Audit(
            actor,
            "RequestApproved",
            target.Id,
            g.ProjectId,
            r.Reason,
            r.CorrelationId,
            target.Revision
        );
        return await Dto(actor, r);
    }
}

public static class WorkflowEndpoints
{
    public static void MapWorkflow(this WebApplication app)
    {
        app.MapPost(
            "/api/previews",
            (ChangeCommand command, HttpContext c, MutationRunner run, Workflow w) =>
                run.Run(c, command, a => w.AuthorizeCommand(a, command), a => w.Preview(a, command))
        );
        app.MapPost(
            "/api/requests",
            (PreviewReference body, HttpContext c, MutationRunner run, Workflow w) =>
                run.Run(
                    c,
                    body,
                    a => w.AuthorizeSubmission(a, body.PreviewId),
                    a => w.Submit(a, body.PreviewId, c.TraceIdentifier),
                    201
                )
        );
        app.MapGet(
            "/api/requests/{id}",
            async (string id, HttpContext c, ActorService actors, Workflow w) =>
            {
                var a = await actors.Get(c);
                return Results.Ok(await w.Dto(a, await w.Request(a, id)));
            }
        );
        app.MapGet(
            "/api/requests",
            async (
                HttpContext c,
                ActorService actors,
                Workflow w,
                LabDb db,
                int? page,
                int? pageSize,
                string? search,
                string? status,
                string? groupId
            ) =>
            {
                var a = await actors.Get(c);
                if (search?.Length > 200)
                    throw new ApiError(
                        400,
                        "search_too_long",
                        "Search is limited to 200 characters."
                    );
                if (groupId != null)
                {
                    var group = await db.Groups.FindAsync(groupId);
                    Scope.Require(group != null && Scope.Project(a, group.ProjectId), 404);
                }
                var projects = a
                    .Roles.Where(r => r.Role is "Reviewer" or "Auditor")
                    .Select(r => r.ProjectId)
                    .ToArray();
                var isOperator = a.Has("Operator");
                var now = w.Now;
                var selected = db.Requests.Where(r =>
                    isOperator
                    || r.ActorId == a.Id
                    || r.AccountId == a.Id
                    || projects.Contains(r.ProjectId)
                );
                if (groupId != null)
                    selected = selected.Where(r => r.GroupId == groupId);
                if (status != null)
                    selected = selected.Where(r =>
                        (r.Status == "Pending" && r.ExpiresAt <= now ? "Expired" : r.Status)
                        == status
                    );
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var term = search.ToLower();
                    selected = selected.Where(r =>
                        (r.Id + " " + r.CorrelationId + " " + r.Reason).ToLower().Contains(term)
                    );
                }
                var rows = await Page.Query(
                    selected.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
                    page,
                    pageSize
                );
                var dto = new List<object>();
                foreach (var r in rows.Items)
                    dto.Add(await w.Dto(a, r));
                return Results.Ok(new Paged<object>(dto, rows.Total, rows.Page, rows.PageSize));
            }
        );
        app.MapPost(
            "/api/requests/{id}/preview",
            (string id, HttpContext c, MutationRunner run, Workflow w) =>
                run.Run(
                    c,
                    new { id },
                    async a =>
                    {
                        var r = await w.Request(a, id);
                        Scope.Require(w.Reviewer(a, r));
                    },
                    async a =>
                    {
                        var r = await w.Request(a, id);
                        w.Pending(r);
                        return await w.Preview(a, r.Command(), id);
                    }
                )
        );
        app.MapPost(
            "/api/requests/{id}/approve",
            (string id, PreviewReference body, HttpContext c, MutationRunner run, Workflow w) =>
                run.Run(
                    c,
                    body,
                    async a =>
                    {
                        var r = await w.Request(a, id);
                        Scope.Require(w.Reviewer(a, r));
                    },
                    a => w.Approve(a, id, body.PreviewId, c.TraceIdentifier)
                )
        );
        foreach (var decision in new[] { "reject", "cancel" })
            app.MapPost(
                $"/api/requests/{{id}}/{decision}",
                (string id, ReasonBody body, HttpContext c, MutationRunner run, Workflow w) =>
                    run.Run(
                        c,
                        body,
                        async a =>
                        {
                            var r = await w.Request(a, id);
                            Scope.Require(
                                decision == "reject" ? w.Reviewer(a, r) : r.ActorId == a.Id
                            );
                        },
                        async a =>
                        {
                            var r = await w.Request(a, id);
                            w.Pending(r);
                            r.Status = decision == "reject" ? "Rejected" : "Cancelled";
                            r.DecisionReason = Workflow.Reason(body.Reason);
                            r.DecidedBy = a.Id;
                            w.Audit(
                                a,
                                "Request" + r.Status,
                                r.AccountId,
                                r.ProjectId,
                                r.DecisionReason,
                                r.CorrelationId
                            );
                            return await w.Dto(a, r);
                        }
                    )
            );
    }
}

public record PreviewReference(string PreviewId);

public record ReasonBody(string Reason);
