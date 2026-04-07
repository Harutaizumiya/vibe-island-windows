using DynamicIsland.Models;

namespace DynamicIsland.Services;

public interface IClaudecodeService : IDisposable
{
    event EventHandler<ClaudecodeTask>? TaskUpdated;

    ClaudecodeTask? CurrentTask { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task ApproveAsync(CancellationToken cancellationToken = default);

    Task RejectAsync(CancellationToken cancellationToken = default);

    Task SnoozeAsync(CancellationToken cancellationToken = default);
}
