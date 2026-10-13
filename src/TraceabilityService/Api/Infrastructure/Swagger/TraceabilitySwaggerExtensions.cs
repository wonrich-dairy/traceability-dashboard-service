using Microsoft.OpenApi;

namespace TraceabilityService.Api.Infrastructure
{
    /// <summary>
    /// Swagger for Traceability Service (SCRUM-125), on the shared service template (SCRUM-49, as in
    /// MCC and Intake and Processing): own UI at /swagger, spec at /swagger/v1/swagger.json, Bearer
    /// tokens, XML comments rendered. On in Development and Staging, off in Production.
    /// Every controller action is picked up through the API explorer, so a new endpoint needs no
    /// configuration here: its XML comments, [ProducesResponseType]s and authorization are read
    /// from the action itself.
    /// </summary>
    public static class TraceabilitySwaggerExtensions
    {
        public const string RoutePrefix = "swagger";
        public const string DocumentName = "v1";
        public const string SpecPath = $"/{RoutePrefix}/{DocumentName}/swagger.json";

        public static IServiceCollection AddTraceabilitySwagger(this IServiceCollection services)
        {
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc(DocumentName, new OpenApiInfo
                {
                    Title = "Traceability Service API",
                    Version = DocumentName,
                    Description = "Batch records, backward traces and the QC dashboard for Wonrich Dairy, "
                                  + "read from the Traceability Service's read model."
                });

                // The scheme Swagger UI's Authorize button fills in. Which operations need it, and which
                // roles, is decided per operation by AuthorizationOperationFilter rather than globally.
                options.AddSecurityDefinition(AuthorizationOperationFilter.BearerScheme, new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "Access token from POST /api/auth/login on the auth service. Paste the token only."
                });

                // XML documentation comments (GenerateDocumentationFile in the csproj)
                var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
                if (File.Exists(xmlPath))
                {
                    options.IncludeXmlComments(xmlPath);
                }

                // After the XML comments, which would otherwise overwrite the roles it adds to the description.
                options.OperationFilter<AuthorizationOperationFilter>();

                // A property whose type is another schema (an enum, a nested DTO) keeps its own comment:
                // OpenAPI 3.0 allows no description beside a bare $ref.
                options.UseAllOfToExtendReferenceSchemas();
                // string is required-not-null and string? nullable, as the nullable annotations say.
                options.SupportNonNullableReferenceTypes();
            });

            return services;
        }

        /// <summary>
        /// Serves the spec and the UI, anonymously so the page loads before a token is entered.
        /// Nothing at all in Production: both paths are 404 there.
        /// </summary>
        public static WebApplication UseTraceabilitySwagger(this WebApplication app)
        {
            if (app.Environment.IsProduction())
            {
                return app;
            }

            // Ahead of authorization in the pipeline, so the deny-by-default fallback policy never sees them.
            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint(SpecPath, "Traceability Service v1");
                options.RoutePrefix = RoutePrefix;
                options.DisplayRequestDuration();
            });

            return app;
        }
    }
}
