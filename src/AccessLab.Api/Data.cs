using Microsoft.EntityFrameworkCore;

namespace AccessLab.Api;

public class Account
{
    public string Id { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTimeOffset StartsAt { get; set; } = DateTimeOffset.UnixEpoch;
    public DateTimeOffset EndsAt { get; set; } = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public long Revision { get; set; } = 1;
    public string[] ProjectIds { get; set; } = [];
    public List<RoleGrant> Roles { get; set; } = [];

    public bool Active(DateTimeOffset now) => Status == "Active" && StartsAt <= now && now < EndsAt;

    public bool Has(string role, string? project = null) =>
        Roles.Any(x => x.Role == role && (project == null || x.ProjectId == project));
}

public class RoleGrant
{
    public int Id { get; set; }
    public string AccountId { get; set; } = "";
    public string Role { get; set; } = "Member";
    public string? ProjectId { get; set; }
}

public class LabDb(DbContextOptions<LabDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<RoleGrant> Roles => Set<RoleGrant>();
    public DbSet<LabGroup> Groups => Set<LabGroup>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<ChangeRequest> Requests => Set<ChangeRequest>();
    public DbSet<ImpactPreview> Previews => Set<ImpactPreview>();
    public DbSet<AuditEvent> Audit => Set<AuditEvent>();
    public DbSet<IdempotentResult> Idempotency => Set<IdempotentResult>();
    public DbSet<Projection> Projections => Set<Projection>();
    public DbSet<Outbox> Outbox => Set<Outbox>();
    public DbSet<DemoSetting> Settings => Set<DemoSetting>();
    public DbSet<DestinationState> DestinationStates => Set<DestinationState>();
    public DbSet<SyncAttempt> SyncAttempts => Set<SyncAttempt>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<DestinationState>()
            .ToTable("DestinationStates", "Simulator")
            .HasKey(s => s.AccountId);
        m.Entity<Projection>().HasIndex(x => new { x.AccountId, x.Revision }).IsUnique();
        m.Entity<IdempotentResult>()
            .HasIndex(x => new
            {
                x.ActorId,
                x.Operation,
                x.Key,
            })
            .IsUnique();
        m.Entity<SyncState>().HasKey(s => s.AccountId);
        m.Entity<Membership>().HasIndex(m => new { m.AccountId, m.GroupId });
        m.Entity<Account>().HasIndex(a => new { a.Issuer, a.Subject }).IsUnique();
        m.Entity<Account>().HasMany(a => a.Roles).WithOne().HasForeignKey(r => r.AccountId);
    }
}

public static class Seed
{
    public static async Task Run(LabDb db, string issuer)
    {
        if (await db.Accounts.AnyAsync())
            return;
        var names = new[] { "Alice", "Bob", "Dana", "Claire", "Eve", "Frank" };
        for (var i = 0; i < names.Length; i++)
        {
            var id = $"00000000-0000-0000-0000-{i + 1:000000000000}";
            var project = i == 4 ? "nova" : "atlas";
            var a = new Account
            {
                Id = id,
                Subject = id,
                Issuer = issuer,
                DisplayName = names[i],
                ProjectIds = [project],
            };
            a.Roles.Add(new() { Role = "Member", ProjectId = project });
            if (i is 1 or 2 or 4)
                a.Roles.Add(new() { Role = "Reviewer", ProjectId = project });
            if (i == 3)
                a.Roles.Add(new() { Role = "Operator" });
            if (i == 5)
                a.Roles.Add(new() { Role = "Auditor", ProjectId = project });
            db.Add(a);
        }
        db.Groups.AddRange(
            new LabGroup
            {
                Id = "analysis",
                Name = "Analysis",
                ProjectId = "atlas",
                Description = "Results and shared library",
                ResourceIds = ["results", "library"],
            },
            new LabGroup
            {
                Id = "knowledge",
                Name = "Knowledge",
                ProjectId = "atlas",
                Description = "Shared library",
                ResourceIds = ["library"],
            },
            new LabGroup
            {
                Id = "nova-analysis",
                Name = "Nova Analysis",
                ProjectId = "nova",
                Description = "Nova results",
                ResourceIds = ["nova-results"],
            }
        );
        db.Resources.AddRange(
            new Resource
            {
                Id = "results",
                Name = "Results dashboard",
                ProjectId = "atlas",
                Description = "Atlas research results",
            },
            new Resource
            {
                Id = "library",
                Name = "Shared library",
                ProjectId = "atlas",
                Description = "Atlas documentation",
            },
            new Resource
            {
                Id = "nova-results",
                Name = "Nova results",
                ProjectId = "nova",
                Description = "Nova research results",
            }
        );
        db.Memberships.Add(
            new()
            {
                Id = "alice-knowledge",
                AccountId = "00000000-0000-0000-0000-000000000001",
                GroupId = "knowledge",
                StartsAt = DateTimeOffset.UnixEpoch,
                EndsAt = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
        );
        await db.SaveChangesAsync();
    }
}

public class ApiError(int status, string code, string detail) : Exception(detail)
{
    public int Status => status;
    public string Code => code;
}

public class ActorService(LabDb db, TimeProvider clock)
{
    public async Task<Account> Get(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
            throw new ApiError(401, "authentication_required", "Sign in to continue.");
        var subject = ctx.User.FindFirst("sub")?.Value;
        var issuer = ctx.User.FindFirst("accesslab_issuer")?.Value;
        var a = await db
            .Accounts.Include(a => a.Roles)
            .SingleOrDefaultAsync(a => a.Subject == subject && a.Issuer == issuer);
        if (a == null || !a.Active(clock.GetUtcNow()))
            throw new ApiError(
                403,
                "account_inactive",
                "No active application account is configured for this identity."
            );
        return a;
    }
}
