using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AccessLab.Api.Tests;

public class ApiFixture : WebApplicationFactory<Program>
{
    public const string Alice = "00000000-0000-0000-0000-000000000001",
        Bob = "00000000-0000-0000-0000-000000000002",
        Dana = "00000000-0000-0000-0000-000000000003",
        Claire = "00000000-0000-0000-0000-000000000004",
        Eve = "00000000-0000-0000-0000-000000000005";

    static string TestConnection()
    {
        return Environment.GetEnvironmentVariable("ACCESSLAB_TEST_DATABASE")
            ?? throw new InvalidOperationException(
                "Set ACCESSLAB_TEST_DATABASE to an isolated *_tests PostgreSQL database; no fallback is allowed."
            );
    }

    public int MaxMembershipDays { get; set; } = 90;
    public TestClock Clock { get; } = new();
    public TestFaults Faults { get; } = new();
    public Action<IServiceCollection>? ConfigureTestServices { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        var testConnection = TestConnection();
        var safe = new Npgsql.NpgsqlConnectionStringBuilder(testConnection);
        if (!safe.Database!.EndsWith("_tests", StringComparison.Ordinal))
            throw new InvalidOperationException("Tests require an explicit *_test database.");
        using (var conn = new Npgsql.NpgsqlConnection(testConnection))
        {
            conn.Open();
            using var cmd = new Npgsql.NpgsqlCommand(
                "DROP SCHEMA public CASCADE; DROP SCHEMA IF EXISTS \"Simulator\" CASCADE; CREATE SCHEMA public",
                conn
            );
            cmd.ExecuteNonQuery();
        }
        b.UseEnvironment("Development");
        b.ConfigureAppConfiguration(
            (_, c) =>
                c.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Database"] = TestConnection(),
                        ["AccessLab:Seed"] = "true",
                        ["AccessLab:MaxMembershipDays"] = MaxMembershipDays.ToString(),
                        ["AccessLab:WorkerEnabled"] = "false",
                        ["AccessLab:DemoMode"] = "true",
                    }
                )
        );
        b.ConfigureServices(s =>
        {
            foreach (
                var d in s.Where(d =>
                        d.ImplementationType == typeof(AccessLab.Api.ProvisioningWorker)
                    )
                    .ToList()
            )
                s.Remove(d);
            s.AddSingleton<TimeProvider>(Clock);
            s.AddSingleton<AccessLab.Api.IFaults>(Faults);
        });
        b.ConfigureServices(s =>
            s.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = "test-only";
                    o.DefaultChallengeScheme = "test-only";
                })
                .AddScheme<AuthenticationSchemeOptions, TestIdentity>("test-only", _ => { })
        );
        b.ConfigureServices(s => ConfigureTestServices?.Invoke(s));
    }

    public HttpClient As(string id)
    {
        var c = CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            }
        );
        c.DefaultRequestHeaders.Add("Test-Actor", id);
        return c;
    }

    public static async Task<HttpResponseMessage> Post(
        HttpClient c,
        string path,
        object body,
        string? key = null
    )
    {
        var csrf = await c.GetFromJsonAsync<JsonElement>("/api/csrf");
        using var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        msg.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        msg.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await c.SendAsync(msg);
    }
}

public class TestIdentity(
    IOptionsMonitor<AuthenticationSchemeOptions> o,
    ILoggerFactory l,
    UrlEncoder e
) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Test-Actor", out var id))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", id!),
                new Claim("accesslab_issuer", "http://localhost:58080/realms/accesslab"),
            ],
            Scheme.Name
        );
        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)
            )
        );
    }
}

public class DatabaseIdentityTests : IClassFixture<ApiFixture>
{
    readonly ApiFixture f;

    public DatabaseIdentityTests(ApiFixture f) => this.f = f;

    [Fact]
    public async Task Seeded_identity_resolves_from_database()
    {
        var r = await f.As(ApiFixture.Alice).GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ApiFixture.Alice, j.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task Unknown_identity_is_not_provisioned()
    {
        var r = await f.As("00000000-0000-0000-0000-000000000007").GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
