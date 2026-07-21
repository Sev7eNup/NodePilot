using Microsoft.OpenApi;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Swashbuckle wiring for the public REST surface — OpenAPI 3 doc + Swagger UI. Two security
/// schemes are registered (JWT Bearer for the browser/integration callers, X-Api-Key for
/// external triggers) so the "Authorize" button in the UI can exercise both auth paths.
/// SignalR hub and health probes are excluded from the spec — they are not REST and would
/// just clutter the listing.
/// </summary>
public static class OpenApiSetup
{
    public static IServiceCollection AddNodePilotOpenApi(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(opts =>
        {
            opts.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "NodePilot API",
                Version = "v1",
                Description = "Workflow orchestration API. All endpoints return JSON. "
                            + "Authenticate via POST /api/auth/login to obtain a JWT, then "
                            + "present it as `Authorization: Bearer <token>`. "
                            + "See the `/CLAUDE.md` in-repo docs for activity schemas and trigger semantics.",
                Contact = new OpenApiContact { Name = "NodePilot" },
            });

            // JWT Bearer security scheme — lets the "Authorize" button in Swagger UI carry
            // a token across all subsequent requests. `bearerFormat=JWT` is a hint to the
            // UI; the actual validation is done by JwtBearer middleware regardless.
            //
            // Microsoft.OpenApi 2.x API note: schemes and references are separated. The
            // scheme itself no longer carries a Reference — you register it by id via
            // AddSecurityDefinition and then point at it via OpenApiSecuritySchemeReference
            // in the requirement.
            opts.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Description = "JWT returned by POST /api/auth/login. Paste the raw token (no \"Bearer \" prefix needed — Swagger prepends it).",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
            });
            // Attach the scheme to every operation — [AllowAnonymous] endpoints simply
            // ignore the header if present, so this doesn't break login / health / webhook
            // endpoints. 2.x expects a doc-callback; we don't need the document.
            opts.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
            {
                { new OpenApiSecuritySchemeReference("Bearer"), new List<string>() }
            });

            // External-trigger API key: separate scheme so the Swagger UI shows it as a
            // distinct option and testers can exercise the X-Api-Key path without logging in.
            opts.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
            {
                Name = "X-Api-Key",
                Description = "External-trigger API key (ExternalTrigger:ApiKey in appsettings). Only consumed by POST /api/trigger/{workflowNameOrId}.",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
            });

            // Ingest XML comments (from /// summaries on controllers + DTOs) so endpoint
            // descriptions are rich. File path is relative to the emitted assembly.
            var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath)) opts.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);

            // Suppress controllers that shouldn't appear in the public spec: SignalR hub
            // (binary protocol, not REST), health probes (infra-only, cluttering).
            opts.DocInclusionPredicate((_, apiDesc) =>
            {
                var path = apiDesc.RelativePath ?? "";
                if (path.StartsWith("hubs/", StringComparison.OrdinalIgnoreCase)) return false;
                if (path.StartsWith("healthz/", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            });
        });
        return services;
    }

    /// <summary>
    /// Mounts the Swagger JSON + UI unless the operator explicitly opted out via
    /// <c>Swagger:DisableInNonDevelopment=true</c> in a non-Development environment
    /// (audit M-18). The JSON spec itself is documentation, not a secret, so the default
    /// keeps it exposed.
    /// </summary>
    public static WebApplication UseNodePilotOpenApi(this WebApplication app, ILogger logger)
    {
        var disabled = !app.Environment.IsDevelopment()
            && app.Configuration.GetValue<bool>("Swagger:DisableInNonDevelopment");
        if (disabled)
        {
            logger.LogInformation("Swagger UI is disabled in this environment (Swagger:DisableInNonDevelopment=true).");
            return app;
        }
        app.UseSwagger(c => c.RouteTemplate = "openapi/{documentName}.json");
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/openapi/v1.json", "NodePilot API v1");
            c.RoutePrefix = "swagger";
            c.DocumentTitle = "NodePilot API";
            c.DisplayRequestDuration();
            c.EnableDeepLinking();
            c.DefaultModelsExpandDepth(-1);  // hide the "Schemas" section by default
        });
        return app;
    }
}
