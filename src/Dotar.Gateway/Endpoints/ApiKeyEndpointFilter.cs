using Dotar.Gateway.Infrastructure.Services;

namespace Dotar.Gateway.Endpoints;

/// <summary>
/// Endpoint filter que exige el header X-Gateway-Api-Key en cada request.
/// Devuelve 401 si falta o no coincide. Sin información sobre tenants.
/// </summary>
public class ApiKeyEndpointFilter : IEndpointFilter
{
    private readonly ApiKeyService _apiKeyService;

    public ApiKeyEndpointFilter(ApiKeyService apiKeyService)
    {
        _apiKeyService = apiKeyService;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var providedKey = context.HttpContext.Request.Headers[ApiKeyService.HeaderName].FirstOrDefault();
        if (!_apiKeyService.Validate(providedKey))
            return Results.Unauthorized();

        return await next(context);
    }
}
