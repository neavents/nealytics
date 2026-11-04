using Nealytics.Engine.Features.S3Import;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

internal static class S3TestSupport
{
    internal static S3SourceOptions Options() => new()
    {
        Name = "source", ProjectId = "p", Endpoint = "https://s3.amazonaws.com", Bucket = "examplebucket",
        Region = "us-east-1", AccessKey = "AKIAIOSFODNN7EXAMPLE", SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
    };

    internal static S3Sources Catalog(S3SourceOptions? source = null, Action<TelemetryEngineOptions>? configure = null)
    {
        var options = new TelemetryEngineOptions
        {
            EnableNdjsonImport = true, EnableImportJobs = true, S3Sources = [source ?? Options()]
        };
        configure?.Invoke(options);
        var projects = new ProjectCatalog(options, ProjectContext.Compile("baseline", options));
        return new(options, projects);
    }

    internal static S3Source Source(S3SourceOptions? source = null) => Catalog(source).Find("source", "p")!;
}
