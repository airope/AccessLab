using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace AccessLab.Api.Tests;

public class InputValidationTests
{
    [Theory]
    [InlineData("/api/previews")]
    [InlineData("/api/requests")]
    public async Task Missing_command_fields_return_400_not_internal_error(string route)
    {
        await using var f = new ApiFixture();
        var r = await ApiFixture.Post(f.As(ApiFixture.Alice), route, new { });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("https://evil.invalid")]
    [InlineData("//evil.invalid")]
    [InlineData("/%5cevil.invalid")]
    [InlineData("/%2fevil.invalid")]
    public async Task Encoded_nonlocal_login_returns_are_rejected(string value)
    {
        await using var f = new ApiFixture();
        var r = await f.As(ApiFixture.Alice)
            .GetAsync("/auth/login?returnUrl=" + Uri.EscapeDataString(value));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.invalid")]
    [InlineData("//evil.invalid")]
    [InlineData("/%5cevil.invalid")]
    [InlineData("/%2fevil.invalid")]
    public async Task Account_switch_preserves_local_return_validation(string value)
    {
        await using var f = new ApiFixture();
        var r = await f.As(ApiFixture.Alice)
            .GetAsync("/auth/login?prompt=login&returnUrl=" + Uri.EscapeDataString(value));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unexpected")]
    public async Task Login_rejects_unrecognized_prompt(string value)
    {
        await using var f = new ApiFixture();
        var r = await f.As(ApiFixture.Alice)
            .GetAsync("/auth/login?prompt=" + Uri.EscapeDataString(value));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
