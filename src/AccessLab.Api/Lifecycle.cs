using Microsoft.EntityFrameworkCore;

namespace AccessLab.Api;

public record StatusCommand(string Status, string Reason);

public static class LifecycleEndpoints
{
    public static void MapLifecycle(this WebApplication app)
    {
        app.MapGet(
            "/api/memberships",
            async (
                HttpContext c,
                ActorService actors,
                LabDb db,
                TimeProvider clock,
                string? accountId,
                string? groupId,
                int? page,
                int? pageSize
            ) =>
            {
                var a = await actors.Get(c);
                var target = await db.Accounts.FindAsync(accountId ?? a.Id);
                Scope.Require(target != null && Scope.Account(a, target), 404);
                var projects = a
                    .Roles.Where(r => r.Role is "Reviewer" or "Auditor")
                    .Select(r => r.ProjectId)
                    .ToArray();
                if (groupId != null)
                {
                    var group = await db.Groups.FindAsync(groupId);
                    Scope.Require(group != null && Scope.Project(a, group.ProjectId), 404);
                }
                var selfOrOperator = a.Id == target!.Id || a.Has("Operator");
                var now = clock.GetUtcNow();
                var ms = db
                    .Memberships.Where(m =>
                        m.AccountId == target.Id && (groupId == null || m.GroupId == groupId)
                    )
                    .Where(m =>
                        selfOrOperator
                        || db.Groups.Any(g => g.Id == m.GroupId && projects.Contains(g.ProjectId))
                    )
                    .OrderBy(m => m.StartsAt)
                    .ThenBy(m => m.Id)
                    .Select(m => new
                    {
                        membershipId = m.Id,
                        m.AccountId,
                        displayName = target.DisplayName,
                        m.GroupId,
                        m.StartsAt,
                        m.EndsAt,
                        status = m.Status == "Active" && m.EndsAt <= now ? "Expired" : m.Status,
                    });
                return Results.Ok(await Page.Query(ms, page, pageSize));
            }
        );
        app.MapPost(
            "/api/accounts/{id}/status",
            (
                string id,
                StatusCommand body,
                HttpContext c,
                MutationRunner run,
                Workflow w,
                LabDb db
            ) =>
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
                        if (body.Status is not ("Active" or "Suspended" or "Ended"))
                            throw new ApiError(
                                400,
                                "status_invalid",
                                "Choose Active, Suspended, or Ended."
                            );
                        if (target.Status == "Ended")
                            throw new ApiError(409, "account_ended", "Ended is terminal.");
                        target.Status = body.Status;
                        target.Revision++;
                        await w.Project(target);
                        w.Audit(
                            a,
                            "Account" + body.Status,
                            target.Id,
                            null,
                            reason,
                            c.TraceIdentifier,
                            target.Revision
                        );
                        return new
                        {
                            target.Id,
                            target.DisplayName,
                            target.Status,
                            target.StartsAt,
                            target.EndsAt,
                            target.ProjectIds,
                            target.Revision,
                        };
                    }
                )
        );
        app.MapGet(
            "/api/audit",
            async (
                HttpContext c,
                ActorService actors,
                LabDb db,
                int? page,
                int? pageSize,
                string? search
            ) =>
            {
                var a = await actors.Get(c);
                if (search?.Length > 200)
                    throw new ApiError(
                        400,
                        "search_too_long",
                        "Search is limited to 200 characters."
                    );
                var projects = a
                    .Roles.Where(r => r.Role is "Reviewer" or "Auditor")
                    .Select(r => r.ProjectId)
                    .ToArray();
                var isOperator = a.Has("Operator");
                var events = db.Audit.Where(e =>
                    isOperator
                    || e.TargetId == a.Id
                    || e.ActorId == a.Id
                    || (e.ProjectId != null && projects.Contains(e.ProjectId))
                );
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var term = search.ToLower();
                    events = events.Where(e =>
                        (e.CorrelationId + " " + e.Action + " " + e.TargetId + " " + e.Reason)
                            .ToLower()
                            .Contains(term)
                    );
                }
                var visible = events
                    .OrderByDescending(e => e.Id)
                    .Select(e => new
                    {
                        e.Id,
                        e.At,
                        e.ActorName,
                        e.Action,
                        e.TargetId,
                        e.ProjectId,
                        e.Outcome,
                        e.Reason,
                        e.CorrelationId,
                        e.AccountRevision,
                    });
                return Results.Ok(await Page.Query(visible, page, pageSize));
            }
        );
    }
}
