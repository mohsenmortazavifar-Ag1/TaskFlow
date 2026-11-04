using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Handlers;

public interface IJobHandler
{
    string JobType { get; }
    Task HandleAsync(ClaimedJob job, CancellationToken cancellationToken);
}

public sealed class JobHandlerRegistry(IEnumerable<IJobHandler> handlers)
{
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers =
        handlers.ToDictionary(x => x.JobType, StringComparer.OrdinalIgnoreCase);

    public IJobHandler Resolve(string jobType) =>
        _handlers.TryGetValue(jobType, out var handler)
            ? handler
            : throw new InvalidOperationException($"No handler registered for JobType '{jobType}'.");
}
