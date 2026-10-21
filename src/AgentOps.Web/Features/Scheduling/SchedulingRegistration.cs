using System.ComponentModel.DataAnnotations;
using Cronos;
using Hangfire;
using Microsoft.Extensions.Options;

namespace AgentOps.Web.Features.Scheduling;

/// <summary>Validated recurring UTC execution settings, reapplied on each startup.</summary>
public sealed class SchedulingOptions
{
    public const string Section = "Scheduling";
    public bool Enabled { get; set; } = true;
    [Required] public string RecurringJobId { get; set; } = "insights-agent";
    [Required] public string Cron { get; set; } = "*/15 * * * *";
}

public static class SchedulingRegistration
{
    public static IServiceCollection AddAgentScheduling(this IServiceCollection services)
    {
        services.AddOptions<SchedulingOptions>().BindConfiguration(SchedulingOptions.Section).ValidateDataAnnotations()
            .Validate(o => CronExpression.TryParse(o.Cron, out var cron) &&
                cron.GetNextOccurrence(DateTime.UtcNow) is not null,
                "Scheduling:Cron must be a valid five-field UTC expression with a future occurrence.")
            .ValidateOnStart();
        services.AddScoped<AgentJob>();
        services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseInMemoryStorage());
        services.AddHostedService<RecurringScheduleRegistration>();
        services.AddHangfireServer(options => options.WorkerCount = 1);
        return services;
    }
}

/// <summary>Registration failures fail host startup instead of silently disabling execution.</summary>
public sealed class RecurringScheduleRegistration(
    IRecurringJobManager manager,
    IOptions<SchedulingOptions> options,
    ILogger<RecurringScheduleRegistration> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        if (settings.Enabled)
        {
            manager.AddOrUpdate<AgentJob>(settings.RecurringJobId,
                job => job.RunAsync(CancellationToken.None), settings.Cron,
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            logger.LogInformation("Registered Hangfire recurring job {RecurringJobId} with UTC cron {Cron}; scheduling storage is volatile",
                settings.RecurringJobId, settings.Cron);
        }
        else
        {
            manager.RemoveIfExists(settings.RecurringJobId);
            logger.LogInformation("Hangfire recurring job {RecurringJobId} is disabled", settings.RecurringJobId);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
