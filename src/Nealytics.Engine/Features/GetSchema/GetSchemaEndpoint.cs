namespace Nealytics.Engine.Features.GetSchema;

using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;

public static class GetSchemaEndpoint
{
    public static void MapGetSchema(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/schema", async (
            HttpContext context,
            BreakdownColumns columns,
            DimensionRegistry dimensions,
            MeasureRegistry measures,
            RollupRegistry rollups,
            Microsoft.Extensions.Options.IOptions<TelemetryEngineOptions> engineOptions,
            GetEventTypesQuery eventTypes,
            GetColumnPopulationQuery population,
            System.Threading.CancellationToken cancellationToken) =>
        {
            string? projectId = context.User.FindFirst("project_id")?.Value;
            string? tenantId = context.User.FindFirst("tenant_id")?.Value;

            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
            {
                return Results.Forbid();
            }

            IReadOnlyDictionary<string, long> counts = new Dictionary<string, long>();
            bool populationAvailable = false;

            try
            {
                counts = await population.ExecuteAsync(
                    projectId, tenantId, System.DateTime.UtcNow, cancellationToken);
                populationAvailable = true;
            }
            catch (System.Exception)
            {
                populationAvailable = false;
            }

            long CountFor(string name) =>
                counts.TryGetValue(name, out long value) ? value : 0;

            HashSet<string> declared = new(
                dimensions.Active.Select(dimension => dimension.Name), System.StringComparer.Ordinal);

            List<SchemaDimension> schemaDimensions = new(dimensions.Active.Count);

            foreach (Dimension dimension in dimensions.Active)
            {
                long nonEmpty = CountFor(dimension.Name);

                schemaDimensions.Add(new SchemaDimension
                {
                    Name = dimension.Name,
                    Type = dimension.ConfiguredType,
                    Groupable = columns.Allowed.Contains(dimension.Name),
                    Filterable = columns.Allowed.Contains(dimension.Name),
                    NonEmptyCount = nonEmpty,
                    Populated = populationAvailable && nonEmpty > 0,
                });
            }

            List<SchemaMeasure> schemaMeasures = new(measures.Active.Count);
            List<string> metrics = ["events", "sessions", "users"];

            foreach (Measure measure in measures.Active)
            {
                IReadOnlyList<string> aggregations =
                    [.. measure.Aggregations.Order(System.StringComparer.Ordinal)];

                long nonEmpty = CountFor(measure.Name);

                schemaMeasures.Add(new SchemaMeasure
                {
                    Name = measure.Name,
                    Type = measure.ConfiguredType,
                    Aggregations = aggregations,
                    Minimum = measure.Minimum,
                    Maximum = measure.Maximum,
                    NonEmptyCount = nonEmpty,
                    Populated = populationAvailable && nonEmpty > 0,
                });

                foreach (string aggregation in aggregations)
                {
                    metrics.Add($"{aggregation}({measure.Name})");
                }
            }

            List<SchemaRollup> schemaRollups = new(rollups.Declared.Count);

            foreach (Rollup rollup in rollups.Declared)
            {
                schemaRollups.Add(SchemaRollup.From(rollup));
            }

            IReadOnlyList<SchemaEventType> observed = [];
            bool observedAvailable = false;

            try
            {
                observed = await eventTypes.ExecuteAsync(
                    projectId, tenantId, System.DateTime.UtcNow, cancellationToken);
                observedAvailable = true;
            }
            catch (System.Exception)
            {
                observedAvailable = false;
            }

            return Results.Ok(new SchemaResponse
            {
                CoreColumns = [.. columns.Allowed.Where(name => !declared.Contains(name))],
                Dimensions = schemaDimensions,
                Measures = schemaMeasures,
                Metrics = metrics,
                Grains = ["event", "session", "user"],
                Rollups = schemaRollups,
                RetentionDays = engineOptions.Value.RetentionDays,
                EventTypes = observed,
                EventTypesAvailable = observedAvailable,
                PopulationAvailable = populationAvailable,
            });
        })
        .WithName("GetSchema")
        .Produces<SchemaResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}
