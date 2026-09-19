using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AccessLab.Api.Tests;

public class AccessTests
{
    [Fact]
    public async Task Anonymous_access_is_denied_not_missing()
    {
        await using var f = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await f.CreateClient().GetAsync("/api/access")).StatusCode
        );
    }
}
