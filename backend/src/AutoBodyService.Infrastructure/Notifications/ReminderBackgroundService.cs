using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoBodyService.Infrastructure.Notifications;

/// <summary>Runs without an open browser and never replays overdue reminders after startup.</summary>
public sealed class ReminderBackgroundService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReminderBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startedAt = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<ReminderProcessor>();
                while (await processor.ProcessDueAsync(startedAt, stoppingToken) == 100) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Do not log SQL values, account data, or notification content.
                logger.LogWarning("Inspection reminder processing failed; it will be retried.");
            }
            try { await Task.Delay(ScanInterval, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
