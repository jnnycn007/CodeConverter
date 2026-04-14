using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;

namespace ICSharpCode.CodeConverter.Common;

[DebuggerStepThrough]
public static class AsyncEnumerableTaskExtensions
{
    public static async Task<TResult[]> SelectManyAsync<TArg, TResult>(this IEnumerable<TArg> nodes,
        Func<TArg, Task<IEnumerable<TResult>>> selector)
    {
        var selectAsync = await nodes.SelectAsync(selector);
        return selectAsync.SelectMany(x => x).ToArray();
    }

    /// <summary>High throughput parallel lazy-ish method</summary>
    /// <remarks>
    /// Inspired by https://stackoverflow.com/a/58564740/1128762
    /// </remarks>
    public static async IAsyncEnumerable<TResult> ParallelSelectAwaitAsync<TArg, TResult>(this IEnumerable<TArg> source,
        Func<TArg, Task<TResult>> selector, int maxDop, [EnumeratorCancellation] CancellationToken token = default)
    {
        var processor = new TransformBlock<TArg, TResult>(selector, new ExecutionDataflowBlockOptions {
            MaxDegreeOfParallelism = maxDop,
            BoundedCapacity = (maxDop * 5) / 4,
            CancellationToken = token,
            SingleProducerConstrained = true,
            EnsureOrdered = false
        });

        bool pipelineTerminatedEarly = false;

        foreach (var item in source) {
            while (!processor.Post(item)) {
                var result = await ReceiveAsync();
                if (pipelineTerminatedEarly) break;
                yield return result;
            }
            if (pipelineTerminatedEarly) break;

            if (processor.TryReceive(out var resultIfAvailable)) {
                yield return resultIfAvailable;
            }
        }
        processor.Complete();

        while (await processor.OutputAvailableAsync(token)) {
            var result = ReceiveKnownAvailable();
            if (pipelineTerminatedEarly) break;
            yield return result;
        }

        await processor.Completion;

        if (pipelineTerminatedEarly) {
            throw new InvalidOperationException("Pipeline terminated early missing items, but no exception thrown");
        }

        async Task<TResult> ReceiveAsync()
        {
            await processor.OutputAvailableAsync();
            return ReceiveKnownAvailable();
        }

        TResult ReceiveKnownAvailable()
        {
            token.ThrowIfCancellationRequested();
            if (!processor.TryReceive(out var item)) {
                pipelineTerminatedEarly = true;
                return default;
            }
            return item;
        }
    }

    public static async Task<TResult[]> SelectAsync<TArg, TResult>(this IEnumerable<TArg> nodes,
        Func<TArg, int, Task<TResult>> selector)
    {
        var nodesWithOrders = nodes.Select((input, originalOrder) => (input, originalOrder));
        return await nodesWithOrders.SelectAsync(nwo => selector(nwo.input, nwo.originalOrder));
    }

    public static async Task<TResult[]> SelectAsync<TArg, TResult>(this IEnumerable<TArg> source,
        Func<TArg, Task<TResult>> selector)
    {
        var partitionResults = new List<TResult>();
        foreach (var partitionMember in source) {
            var result = await selector(partitionMember);
            partitionResults.Add(result);
        }

        return partitionResults.ToArray();
    }

    /// <summary>
    /// Hand-rolled to avoid depending on <c>System.Linq.AsyncEnumerable</c>, which only ships as a 10.x
    /// package and so transitively forces Microsoft.Bcl.AsyncInterfaces 10.0.0.0 into the Vsix output —
    /// a version Visual Studio 17.x cannot bind to. See <c>VsixAssemblyCompatibilityTests</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not named <c>ToArrayAsync</c> so it does not clash with
    /// <see cref="System.Linq.AsyncEnumerable"/>.<c>ToArrayAsync</c> on .NET 10+ when this assembly is
    /// referenced by a project whose target framework provides the BCL version.
    /// </remarks>
    public static async Task<TSource[]> ToArraySafeAsync<TSource>(this IAsyncEnumerable<TSource> source,
        CancellationToken cancellationToken = default)
    {
        var list = new List<TSource>();
        await foreach (var item in source.WithCancellation(cancellationToken)) {
            list.Add(item);
        }
        return list.ToArray();
    }

    /// <summary>
    /// Adapts a synchronous sequence to <see cref="IAsyncEnumerable{T}"/>. Hand-rolled for the same
    /// reason as <see cref="ToArraySafeAsync{TSource}"/>, and likewise renamed to avoid clashing with
    /// <see cref="System.Linq.AsyncEnumerable"/>.<c>ToAsyncEnumerable</c> on .NET 10+.
    /// </summary>
#pragma warning disable 1998 // async method without await; required for the iterator to compile to IAsyncEnumerable.
#pragma warning disable VSTHRD200 // The method returns IAsyncEnumerable, not a Task; "Async" suffix would be misleading.
    public static async IAsyncEnumerable<TSource> AsAsyncEnumerable<TSource>(this IEnumerable<TSource> source)
    {
        foreach (var item in source) {
            yield return item;
        }
    }
#pragma warning restore 1998

    /// <summary>
    /// Lazy projection over an <see cref="IAsyncEnumerable{T}"/>. Hand-rolled for the same reason
    /// as <see cref="ToArraySafeAsync{TSource}"/>, and renamed to avoid clashing with
    /// <see cref="System.Linq.AsyncEnumerable"/>.<c>Select</c> on .NET 10+.
    /// </summary>
    public static async IAsyncEnumerable<TResult> SelectSafe<TSource, TResult>(this IAsyncEnumerable<TSource> source,
        Func<TSource, TResult> selector,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in source.WithCancellation(cancellationToken)) {
            yield return selector(item);
        }
    }

    /// <summary>
    /// Lazy projection (with index) over an <see cref="IAsyncEnumerable{T}"/>. Hand-rolled for the same
    /// reason as <see cref="ToArraySafeAsync{TSource}"/>.
    /// </summary>
    public static async IAsyncEnumerable<TResult> SelectSafe<TSource, TResult>(this IAsyncEnumerable<TSource> source,
        Func<TSource, int, TResult> selector,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        await foreach (var item in source.WithCancellation(cancellationToken)) {
            yield return selector(item, index++);
        }
    }
#pragma warning restore VSTHRD200
}