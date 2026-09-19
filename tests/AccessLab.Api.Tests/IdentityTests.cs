using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AccessLab.Api.Tests;

public class IdentityTests
{
    [Fact]
    public async Task Session_requires_authentication_without_redirect()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_rejects_external_return()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var response = await factory
            .CreateClient()
            .GetAsync("/auth/login?returnUrl=https://evil.invalid");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
