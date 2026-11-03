namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nealytics.Engine.Infrastructure.Configuration;

public static class ProjectScopeMiddleware
{
    public static void UseProjectSchemas(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            ProjectCatalog catalog = context.RequestServices.GetRequiredService<ProjectCatalog>();
            if (!catalog.Enabled || !context.Request.Path.StartsWithSegments("/api/v1"))
            {
                await next(context);
                return;
            }
            bool writing = context.Request.Path.StartsWithSegments("/api/v1/telemetry")
                || context.Request.Path.StartsWithSegments("/api/v1/tenants");
            string? project = null;
            if (writing)
            {
                string key = context.Request.Path.StartsWithSegments("/api/v1/telemetry/beacon")
                    ? string.Empty : context.Request.Headers["X-Project-Key"].ToString();
                if (key.Length == 0)
                {
                    key = context.Request.Query["k"].ToString();
                }
                ApiKeyValidator validator = context.RequestServices.GetRequiredService<ApiKeyValidator>();
                if (validator.TryResolve(key, out IngestionKeyPolicy? policy))
                {
                    project = policy.PinnedProject;
                }
            }
            else if (context.User.Identity?.IsAuthenticated == true)
            {
                project = context.User.FindFirst("project_id")?.Value;
            }
            if (project is null)
            {
                await next(context);
                return;
            }
            ProjectContext? selected = string.IsNullOrWhiteSpace(project) || project.Length > 256
                ? null : catalog.Resolve(project);
            if (selected is null)
            {
                await Results.Forbid().ExecuteAsync(context);
                return;
            }
            context.Items[ProjectRequestContext.ItemKey] = selected;
            await next(context);
        });
    }
}
