namespace Nealytics.Engine.Features.ImportJobs;

using System;

public sealed record ImportJobPage(ImportJobResponse[] Jobs, Guid? NextAfter);
