using System.Net.Http.Json;
using System.Text.Json;

namespace Almirante.Api.Tests;

public class OpenApiTests
{
    [Theory]
    [InlineData("/api/Auth/login")]
    [InlineData("/api/Auth/refresh")]
    [InlineData("/api/Auth/logout")]
    public async Task Swagger_DocumentaCsrfEStatusDasOperacoesComCookie(string path)
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var doc = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var operation = doc.GetProperty("paths").GetProperty(path).GetProperty("post");

        var csrf = operation.GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "X-CSRF-TOKEN");
        Assert.Equal("header", csrf.GetProperty("in").GetString());
        Assert.True(csrf.GetProperty("required").GetBoolean());
        var responses = operation.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToArray();
        Assert.Contains("400", responses);
        Assert.Contains("401", responses);
        Assert.Contains(path.EndsWith("logout", StringComparison.Ordinal) ? "204" : "200", responses);
    }
}
