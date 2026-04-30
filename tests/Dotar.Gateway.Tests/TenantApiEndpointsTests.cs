using System.Net;
using System.Net.Http.Json;
using Dotar.Gateway.Domain.Entities;
using Dotar.Gateway.Infrastructure.Data;
using Dotar.Gateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dotar.Gateway.Tests;

public class TenantApiEndpointsTests : IClassFixture<GatewayWebApplicationFactory>
{
    private readonly GatewayWebApplicationFactory _factory;

    public TenantApiEndpointsTests(GatewayWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private string ApiKey => _factory.Services.GetRequiredService<ApiKeyService>().GetCurrent()!;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyService.HeaderName, ApiKey);
        return client;
    }

    [Fact]
    public async Task Post_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X", slug = "x-no-key", targetUrl = "https://t.com/h"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Post_WithBadApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyService.HeaderName, "definitely-not-the-key");
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X", slug = "x-bad-key", targetUrl = "https://t.com/h"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Post_CreatesTenant_AndReturnsSecret()
    {
        var client = AuthedClient();
        var slug = $"test-create-{Guid.NewGuid():N}".Substring(0, 30);
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Test Tenant",
            slug,
            targetUrl = "https://destino.com/api/webhooks",
            signatureScheme = SignatureScheme.GitHub
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal($"/api/tenants/{slug}", resp.Headers.Location?.ToString());

        var body = await resp.Content.ReadFromJsonAsync<CreatedResponse>();
        Assert.NotNull(body);
        Assert.Equal(slug, body!.Slug);
        Assert.False(string.IsNullOrWhiteSpace(body.WebhookSecret));
        Assert.Equal("GitHub", body.SignatureScheme);

        // Verificamos en la DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
        Assert.NotNull(tenant);
        Assert.Equal(SignatureScheme.GitHub, tenant!.SignatureScheme);
    }

    [Fact]
    public async Task Post_DuplicateSlug_Returns409()
    {
        var client = AuthedClient();
        var slug = $"dup-{Guid.NewGuid():N}".Substring(0, 20);

        var first = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "First", slug, targetUrl = "https://a.com/h"
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Second", slug, targetUrl = "https://b.com/h"
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("with space")]
    [InlineData("with/slash")]
    public async Task Post_InvalidSlug_Returns400(string slug)
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X", slug, targetUrl = "https://x.com/h"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_NormalizesSlugToLowercase()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X", slug = "UpperCaseSlug", targetUrl = "https://x.com/h"
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CreatedResponse>();
        Assert.Equal("uppercaseslug", body!.Slug);
    }

    [Fact]
    public async Task Post_InvalidUrl_Returns400()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X", slug = "ok-slug", targetUrl = "ftp://not-allowed.com/x"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("WooCommerce")]
    [InlineData("GitHub")]
    [InlineData("Generic")]
    [InlineData("None")]
    public async Task Post_AcceptsSignatureSchemeAsString(string scheme)
    {
        var client = AuthedClient();
        var slug = $"sig-str-{Guid.NewGuid():N}".Substring(0, 25);
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X",
            slug,
            targetUrl = "https://x.com/h",
            signatureScheme = scheme
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CreatedResponse>();
        Assert.Equal(scheme, body!.SignatureScheme);
    }

    [Fact]
    public async Task Post_AcceptsSignatureSchemeAsInteger()
    {
        var client = AuthedClient();
        var slug = $"sig-int-{Guid.NewGuid():N}".Substring(0, 25);
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X",
            slug,
            targetUrl = "https://x.com/h",
            signatureScheme = 1   // GitHub
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CreatedResponse>();
        Assert.Equal("GitHub", body!.SignatureScheme);
    }

    [Fact]
    public async Task Post_AcceptsCustomSecret()
    {
        var client = AuthedClient();
        var slug = $"custom-secret-{Guid.NewGuid():N}".Substring(0, 30);
        var resp = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "X", slug, targetUrl = "https://x.com/h",
            webhookSecret = "my-explicit-secret"
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CreatedResponse>();
        Assert.Equal("my-explicit-secret", body!.WebhookSecret);
    }

    [Fact]
    public async Task Get_ReturnsTenant_AfterCreate()
    {
        var client = AuthedClient();
        var slug = $"get-test-{Guid.NewGuid():N}".Substring(0, 25);
        await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Get Test", slug, targetUrl = "https://get.com/h"
        });

        var resp = await client.GetAsync($"/api/tenants/{slug}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<GetResponse>();
        Assert.Equal(slug, body!.Slug);
        Assert.Equal("https://get.com/h", body.TargetUrl);
    }

    [Fact]
    public async Task Get_NonExistent_Returns404()
    {
        var client = AuthedClient();
        var resp = await client.GetAsync("/api/tenants/this-slug-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PutTargetUrl_UpdatesAndInvalidatesCache()
    {
        var client = AuthedClient();
        var slug = $"put-test-{Guid.NewGuid():N}".Substring(0, 25);
        await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Put Test", slug, targetUrl = "https://old.com/h"
        });

        var resp = await client.PutAsJsonAsync($"/api/tenants/{slug}/target-url", new
        {
            targetUrl = "https://new.com/h"
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == slug);
        Assert.Equal("https://new.com/h", tenant.TargetUrl);
        Assert.NotNull(tenant.UpdatedAt);
    }

    [Fact]
    public async Task Delete_RemovesTenant_AndInvalidatesCache()
    {
        var client = AuthedClient();
        var slug = $"del-test-{Guid.NewGuid():N}".Substring(0, 25);
        await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Del Test", slug, targetUrl = "https://x.com/h"
        });

        var resp = await client.DeleteAsync($"/api/tenants/{slug}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        Assert.False(await db.Tenants.AnyAsync(t => t.Slug == slug));

        var get = await client.GetAsync($"/api/tenants/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Delete_NonExistent_Returns404()
    {
        var client = AuthedClient();
        var resp = await client.DeleteAsync("/api/tenants/nope-nope-delete");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.DeleteAsync("/api/tenants/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PutTargetUrl_NonExistent_Returns404()
    {
        var client = AuthedClient();
        var resp = await client.PutAsJsonAsync("/api/tenants/nope-nope/target-url", new
        {
            targetUrl = "https://new.com/h"
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private record CreatedResponse(
        string Slug,
        string Name,
        string TargetUrl,
        string WebhookSecret,
        string SignatureScheme,
        string? SignatureHeader,
        bool IsActive);

    private record GetResponse(
        string Slug,
        string Name,
        string TargetUrl,
        string SignatureScheme,
        string? SignatureHeader,
        bool IsActive);
}
