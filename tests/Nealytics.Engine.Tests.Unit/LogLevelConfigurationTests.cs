using System.Text.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// The declared log levels are the levels that apply.
///
/// <b>What this is for.</b> <c>appsettings.json</c> has declared
/// <c>"Microsoft.AspNetCore": "Warning"</c> for as long as the file has existed, and it did nothing:
/// Serilog owns the pipeline outright and never reads <c>Logging:LogLevel</c>, so the setting read
/// as a control that was in force while ASP.NET Core emitted four Information lines per request.
///
/// That is a hot-path cost, not a tidiness one. A benchmark at 20k req/s produced roughly 80,000
/// JSON log lines a second and a <b>7.7 GB</b> file in minutes — every line formatted and written
/// while the ingest path was trying to work, and, wherever logs are exported, also shipped to the
/// collector over OTLP.
///
/// Dead configuration is the shape this codebase keeps paying for: something that reads, in review,
/// like a setting somebody chose.
/// </summary>
public class LogLevelConfigurationTests
{
    private static string AppSettings()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "src", "Nealytics.Engine", "appsettings.json");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("src/Nealytics.Engine/appsettings.json not found.");
    }

    private static string ProgramSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Nealytics.Engine", "Program.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("src/Nealytics.Engine/Program.cs not found.");
    }

    [Fact]
    public void AppSettingsStillDeclaresTheFrameworkLevel()
    {
        using JsonDocument document = JsonDocument.Parse(AppSettings());

        string? level = document.RootElement
            .GetProperty("Logging").GetProperty("LogLevel").GetProperty("Microsoft.AspNetCore")
            .GetString();

        level.Should().Be(
            "Warning",
            "per-request framework logging is the single largest source of log volume this service "
            + "produces, and at ingest rates it is measured in gigabytes per minute");
    }

    [Fact]
    public void TheLoggerReadsThatDeclarationRatherThanHardCodingALevel()
    {
        string program = ProgramSource();

        // The specific regression: `.MinimumLevel.Information()` with nothing else meant every
        // framework category logged at Information no matter what the file said.
        program.Should().Contain(
            "Logging:LogLevel:Microsoft.AspNetCore",
            "the declared framework level has to reach Serilog, or appsettings.json is decoration");

        program.Should().Contain(
            "MinimumLevel.Override(\"Microsoft.AspNetCore\"",
            "Serilog needs a category override; a global minimum cannot express 'quieter for one "
            + "source and not the others'");

        program.Should().Contain(
            "Logging:LogLevel:Default",
            "the default level must be configurable too, or turning on debug logging means a "
            + "rebuild");
    }

    [Theory]
    [InlineData("Trace")]
    [InlineData("Debug")]
    [InlineData("Information")]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Critical")]
    public void EveryLevelNameDotnetUsesIsMapped(string level)
    {
        // Serilog's names are not the ones .NET writes in appsettings — Trace is Verbose and
        // Critical is Fatal. An unmapped name would silently fall back to the default, which is the
        // quiet kind of wrong: the file says Error and the service logs everything.
        ProgramSource().Should().Contain(
            $"\"{level}\" =>",
            "an unmapped level name falls back silently, so the configuration would lie again");
    }
}
