using AccessLab.Api;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// Never log callback URLs, authorization codes, state, token payloads or SQL values.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = false;
builder
    .Services.AddAuthentication(o =>
    {
        o.DefaultScheme = "session";
        o.DefaultChallengeScheme = "oidc";
    })
    .AddCookie(
        "session",
        o =>
        {
            o.Cookie.Name = "__Host-AccessLab";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(1);
            o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = c =>
            {
                c.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
        }
    )
    .AddOpenIdConnect(
        "oidc",
        o =>
        {
            o.Authority =
                builder.Configuration["Oidc:Authority"]
                ?? "http://localhost:58080/realms/accesslab";
            o.ClientId = builder.Configuration["Oidc:ClientId"] ?? "accesslab-web";
            o.ClientSecret = builder.Configuration["Oidc:ClientSecret"];
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.UsePkce = true;
            o.SaveTokens = false;
            o.MapInboundClaims = false;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.TokenValidationParameters.NameClaimType = "name";
            o.Events.OnTokenValidated = context =>
            {
                var identity = (System.Security.Claims.ClaimsIdentity)context.Principal!.Identity!;
                identity.AddClaim(
                    new System.Security.Claims.Claim(
                        "accesslab_issuer",
                        context.SecurityToken.Issuer
                    )
                );
                return Task.CompletedTask;
            };
        }
    );
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "__Host-AccessLab-Csrf";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
var protection = builder.Services.AddDataProtection().SetApplicationName("AccessLab");
if (builder.Configuration["AccessLab:DataProtectionPath"] is { } path)
    protection.PersistKeysToFileSystem(new DirectoryInfo(path));
builder.Services.AddAuthorization();
builder.Services.AddDbContext<AccessLab.Api.LabDb>(o =>
    Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql(
        o,
        builder.Configuration.GetConnectionString("Database") ?? ""
    )
);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AccessLab.Api.ActorService>();
builder.Services.AddScoped<AccessService>();
builder
    .Services.AddOptions<AccessLabOptions>()
    .Bind(builder.Configuration.GetSection("AccessLab"))
    .Validate(
        o => o.MaxMembershipDays is >= 1 and <= 3650,
        "AccessLab:MaxMembershipDays must be between 1 and 3650."
    )
    .ValidateOnStart();
builder.Services.AddScoped<Workflow>();
builder.Services.AddScoped<MutationRunner>();
builder.Services.AddSingleton<IFaults, NoFaults>();
builder.Services.AddScoped<ISimulatedDestination, SimulatedDestination>();
builder.Services.AddScoped<Reconciler>();
if (builder.Configuration.GetValue("AccessLab:WorkerEnabled", true))
    builder.Services.AddHostedService<ProvisioningWorker>();
builder.Services.AddOpenApi();
var app = builder.Build();
if (builder.Configuration.GetValue<bool>("AccessLab:Seed"))
{
    if (!app.Environment.IsDevelopment())
        throw new InvalidOperationException("Fixtures require Development.");
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AccessLab.Api.LabDb>();
    await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(
        db.Database
    );
    await AccessLab.Api.Seed.Run(
        db,
        builder.Configuration["Oidc:Authority"] ?? "http://localhost:58080/realms/accesslab"
    );
}
app.UseMiddleware<ProblemMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.Use(
    async (c, next) =>
    {
        if (c.Request.Path.StartsWithSegments("/api") && HttpMethods.IsPost(c.Request.Method))
        {
            await c.RequestServices.GetRequiredService<ActorService>().Get(c);
            await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c);
        }
        await next();
    }
);
app.MapGet(
    "/auth/login",
    (string? returnUrl, string? prompt) =>
    {
        if (prompt is not null && prompt != "login")
            return Results.BadRequest();
        var url = returnUrl ?? "/";
        // Validate every escaped form, but preserve the original local URL for OIDC.
        // Nested escapes must not hide an authority, backslash or control character.
        var candidate = url;
        while (true)
        {
            if (
                !candidate.StartsWith('/')
                || candidate.StartsWith("//")
                || candidate.Contains('\\')
                || candidate.Any(char.IsControl)
            )
                return Results.BadRequest();
            var decoded = Uri.UnescapeDataString(candidate);
            if (decoded == candidate)
                break;
            candidate = decoded;
        }
        var properties = new OpenIdConnectChallengeProperties { RedirectUri = url };
        if (prompt == "login")
            properties.Prompt = "login";
        return Results.Challenge(properties, ["oidc"]);
    }
);
app.MapGet(
    "/api/csrf",
    (HttpContext ctx, IAntiforgery csrf) =>
        Results.Ok(new { token = csrf.GetAndStoreTokens(ctx).RequestToken })
);
app.MapPost(
    "/auth/logout",
    async (HttpContext ctx, IAntiforgery csrf) =>
    {
        await csrf.ValidateRequestAsync(ctx);
        await ctx.SignOutAsync("session");
        return Results.Ok(new { signedOut = true });
    }
);
app.MapGet(
    "/api/session",
    async (
        HttpContext ctx,
        AccessLab.Api.ActorService actors,
        Microsoft.Extensions.Options.IOptions<AccessLabOptions> options
    ) =>
    {
        var a = await actors.Get(ctx);
        return Results.Ok(
            new
            {
                accountId = a.Id,
                maxMembershipDays = options.Value.MaxMembershipDays,
                a.DisplayName,
                a.Status,
                roles = a.Roles.Select(r => new { r.Role, r.ProjectId }),
                a.ProjectIds,
                demoMode = app.Environment.IsDevelopment()
                    && builder.Configuration.GetValue<bool>("AccessLab:DemoMode"),
            }
        );
    }
);
app.MapReads();
app.MapWorkflow();
app.MapLifecycle();
app.MapOperations();
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet(
    "/health/ready",
    async (LabDb db) =>
    {
        try
        {
            return await db.Database.CanConnectAsync()
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(503);
        }
        catch
        {
            return Results.StatusCode(503);
        }
    }
);
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.UseStaticFiles();
app.MapFallback(
    async (HttpContext c) =>
    {
        if (c.Request.Path.StartsWithSegments("/api") || c.Request.Path.StartsWithSegments("/auth"))
        {
            c.Response.StatusCode = 404;
            return;
        }
        var file = Path.Combine(
            app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"),
            "index.html"
        );
        if (File.Exists(file))
        {
            c.Response.ContentType = "text/html";
            await c.Response.SendFileAsync(file);
        }
        else
            c.Response.StatusCode = 404;
    }
);
app.Run();

public partial class Program;
