using DynamicIsland.Models;

namespace DynamicIsland.Services;

public interface ICodexStatusService : IDisposable
{
    event EventHandler<CodexTask>? TaskUpdated;

    CodexTask? CurrentTask { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default);
}
