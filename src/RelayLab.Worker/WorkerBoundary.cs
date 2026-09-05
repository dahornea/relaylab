using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RelayLab.Tests")]

namespace RelayLab.Worker;

// Internal synchronization seam for deterministic interruption experiments. No runtime fault configuration.
public sealed class WorkerBoundary
{
    internal Func<string, Guid, CancellationToken, Task>? Reached { get; set; }
    internal Task HitAsync(string name, Guid workId, CancellationToken ct) => Reached?.Invoke(name, workId, ct) ?? Task.CompletedTask;
}
