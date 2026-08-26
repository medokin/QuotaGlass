using System.Collections.Concurrent;
using System.Collections.Immutable;
using QuotaGlass.Model;
using QuotaGlass.Providers;

namespace QuotaGlass.Tests.Support;

internal sealed class FakeStatusProvider : IStatusProvider
{
    private readonly Func<int, CancellationToken, Task<ProviderSnapshot>> _fetch;
    private int _invocationCount;

    public FakeStatusProvider(
        string id,
        Func<int, CancellationToken, Task<ProviderSnapshot>> fetch,
        string? label = null)
    {
        Id = id;
        Label = label ?? id;
        _fetch = fetch;
    }

    public string Id { get; }

    public string Label { get; }

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static FakeStatusProvider Blocking(string id, string? label = null)
    {
        var completion = new TaskCompletionSource<ProviderSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeStatusProvider(
            id,
            (_, cancellationToken) => completion.Task.WaitAsync(cancellationToken),
            label);
        provider.Completion = completion;
        return provider;
    }

    public static FakeStatusProvider Returning(
        string id,
        ProviderSnapshot snapshot,
        string? label = null) =>
        new(id, (_, _) => Task.FromResult(snapshot), label);

    public static FakeStatusProvider Sequence(
        string id,
        IEnumerable<Func<CancellationToken, Task<ProviderSnapshot>>> fetches,
        string? label = null)
    {
        var queue = new ConcurrentQueue<Func<CancellationToken, Task<ProviderSnapshot>>>(fetches);
        return new FakeStatusProvider(
            id,
            (_, cancellationToken) => queue.TryDequeue(out Func<CancellationToken, Task<ProviderSnapshot>>? fetch)
                ? fetch(cancellationToken)
                : throw new InvalidOperationException("No fake fetch result remains."),
            label);
    }

    private TaskCompletionSource<ProviderSnapshot>? Completion { get; set; }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        int invocation = Interlocked.Increment(ref _invocationCount);
        Started.TrySetResult();
        return await _fetch(invocation, cancellationToken);
    }

    public void CompleteOk(DateTimeOffset? fetchedAt = null) =>
        Completion?.TrySetResult(Snapshot(Id, Label, fetchedAt: fetchedAt));

    public static ProviderSnapshot Snapshot(
        string id,
        string? label = null,
        HealthState health = HealthState.Ok,
        string? planLabel = null,
        ImmutableArray<UsageWindow>? windows = null,
        ImmutableArray<InfoLine>? info = null,
        string? error = null,
        DateTimeOffset? fetchedAt = null,
        int consecutiveFailures = 0) =>
        new(
            id,
            label ?? id,
            health,
            planLabel,
            windows ?? ImmutableArray<UsageWindow>.Empty,
            info ?? ImmutableArray<InfoLine>.Empty,
            error,
            fetchedAt ?? DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            consecutiveFailures);
}
