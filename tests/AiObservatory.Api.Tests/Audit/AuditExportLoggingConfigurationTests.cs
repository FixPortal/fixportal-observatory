using AiObservatory.Api.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditExportLoggingConfigurationTests
{
    [Fact]
    public void ActualTelemetryProvider_CollectsExportInformationWithoutEnablingSqlInformation()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                ContentRootPath = Path.GetDirectoryName(typeof(Program).Assembly.Location),
                EnvironmentName = "Production",
            }
        );
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] =
            "InstrumentationKey=00000000-0000-0000-0000-000000000001";
        builder.Logging.ClearProviders();
        Program.AddApplicationInsightsIfConfigured(builder);
        using var services = builder.Services.BuildServiceProvider();
        var providers = services.GetServices<ILoggerProvider>().ToArray();
        providers.Select(p => p.GetType().FullName).Should().Contain("OpenTelemetry.Logs.OpenTelemetryLoggerProvider");
        var factory = services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger<AuditExportDiagnostics>().IsEnabled(LogLevel.Information).Should().BeTrue();
        factory
            .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
            .IsEnabled(LogLevel.Information)
            .Should()
            .BeFalse();
        services
            .GetRequiredService<IOptions<LoggerFilterOptions>>()
            .Value.Rules.Should()
            .NotContain(r =>
                r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider"
            );
    }
}
