using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace TraceabilityService.Api.Infrastructure
{
    /// <summary>
    /// Documents each operation's authentication from the endpoint itself, so the spec follows the
    /// code. Everything is deny-by-default (the fallback policy), so every operation needs the Bearer
    /// token, and documents 401, unless it is [AllowAnonymous]. When its [Authorize] names roles,
    /// directly or through a policy, they are listed in the description and 403 is documented.
    /// </summary>
    public sealed class AuthorizationOperationFilter(IOptions<AuthorizationOptions> authorization) : IOperationFilter
    {
        public const string BearerScheme = "Bearer";

        private readonly AuthorizationOptions _authorization = authorization.Value;

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAllowAnonymous>().Any())
            {
                return;
            }

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = []
            });

            operation.Responses ??= [];
            operation.Responses.TryAdd("401", new OpenApiResponse
            {
                Description = "No token, or the token is invalid or expired."
            });

            var roles = metadata.OfType<IAuthorizeData>().SelectMany(RolesOf).Distinct().ToList();
            if (roles.Count == 0)
            {
                return;
            }

            operation.Responses.TryAdd("403", new OpenApiResponse
            {
                Description = "The token's role is not allowed."
            });

            var requirement = $"Requires a token with one of the roles: {string.Join(", ", roles)}.";
            operation.Description = string.IsNullOrWhiteSpace(operation.Description)
                ? requirement
                : $"{operation.Description}\n\n{requirement}";
        }

        // Roles named on the attribute, plus those its policy requires.
        private IEnumerable<string> RolesOf(IAuthorizeData data)
        {
            var named = data.Roles?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
            var fromPolicy = data.Policy is null
                ? []
                : _authorization.GetPolicy(data.Policy)?.Requirements
                      .OfType<RolesAuthorizationRequirement>()
                      .SelectMany(r => r.AllowedRoles)
                  ?? [];

            return named.Concat(fromPolicy);
        }
    }
}
