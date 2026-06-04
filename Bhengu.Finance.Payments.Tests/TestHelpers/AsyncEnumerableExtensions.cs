// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Tests.TestHelpers;

/// <summary>
/// Test-only helpers for materialising <see cref="IAsyncEnumerable{T}"/> into a list so existing
/// assertion patterns (<c>Assert.Equal(2, list.Count)</c>) keep working after providers moved
/// from <c>Task&lt;IReadOnlyList&gt;</c> to streamed enumerables.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            list.Add(item);
        return list;
    }
}
