using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using Tests.ClientTests;

namespace Tests.Unit;

[TestClass]
public class SearchCompletionTests
{
    [TestMethod]
    public async Task Search_CompletedSearchKeepsConcurrentResponsesWhenLibraryCleanupFails()
    {
        var cleanupFailure = CreateSearchDisposalFailure();
        var client = new MockSoulseekClient([])
        {
            SearchHandler = async (query, callback, options, ct) =>
            {
                await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => callback(Response(i)), ct)));
                SignalState(query, options, SearchStates.Completed | SearchStates.ResponseLimitReached);
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                return null!;
            },
        };
        using var searcher = CreateSearcher(client);
        var job = new SearchJob("Artist Album");
        int completedCount = -1;
        job.Session.Completed += session => completedCount = session.RawSnapshot().Count;

        await searcher.Search(job, new SearchSettings(), new ResponseData(), CancellationToken.None);

        Assert.IsTrue(job.IsComplete);
        Assert.AreEqual(16, job.ResultCount);
        Assert.AreEqual(16, completedCount, "All accepted responses must be available before completion is published.");
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 16).Select(i => $"User{i}").ToArray(),
            job.RawSnapshot().Select(r => r.Username).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Search_CancellationIsPreservedWhenLibraryCleanupFails(bool receiveResponseBeforeCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupFailure = CreateSearchDisposalFailure();
        var responseReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MockSoulseekClient([])
        {
            SearchHandler = async (query, callback, options, ct) =>
            {
                if (receiveResponseBeforeCancellation)
                {
                    callback(Response(1));
                    await responseReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                cancellation.Cancel();
                SignalState(query, options, SearchStates.Completed | SearchStates.Cancelled);
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                return null!;
            },
        };
        using var searcher = CreateSearcher(client);
        var job = new SearchJob("Artist Album");
        job.Session.RawResultAdded += (_, _) => responseReceived.TrySetResult();

        // Cancellation while waiting for channel data may surface as the derived
        // TaskCanceledException. Both paths must retain the caller's cancellation.
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            searcher.Search(job, new SearchSettings(), new ResponseData(), cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsTrue(job.IsComplete);
        Assert.AreEqual(receiveResponseBeforeCancellation ? 1 : 0, job.ResultCount);
    }

    [TestMethod]
    public async Task Search_DoesNotTreatFailedSearchAsSuccessfulWhenCleanupAlsoFails()
    {
        var cleanupFailure = CreateSearchDisposalFailure();
        var client = new MockSoulseekClient([])
        {
            SearchHandler = (query, callback, options, ct) =>
            {
                SignalState(query, options, SearchStates.Completed | SearchStates.Errored);
                return Task.FromException<Search>(cleanupFailure);
            },
        };
        using var searcher = CreateSearcher(client);

        var error = await Assert.ThrowsExactlyAsync<SynchronizationLockException>(() =>
            searcher.Search(new SearchJob("Artist Album"), new SearchSettings(), new ResponseData(), CancellationToken.None));

        Assert.AreSame(cleanupFailure, error);
    }

    [TestMethod]
    public async Task Search_DoesNotSuppressUnrelatedLockErrors()
    {
        var failure = new SynchronizationLockException("An unrelated lock failed.");
        var client = new MockSoulseekClient([])
        {
            SearchHandler = (query, callback, options, ct) =>
            {
                SignalState(query, options, SearchStates.Completed);
                return Task.FromException<Search>(failure);
            },
        };
        using var searcher = CreateSearcher(client);

        var error = await Assert.ThrowsExactlyAsync<SynchronizationLockException>(() =>
            searcher.Search(new SearchJob("Artist Album"), new SearchSettings(), new ResponseData(), CancellationToken.None));

        Assert.AreSame(failure, error);
    }

    [TestMethod]
    public async Task Search_ResponseObserverFailureDoesNotEscapeIntoNetworkCallback()
    {
        Exception? callbackFailure = null;
        var observerFailure = new InvalidOperationException("Observer failed.");
        var client = new MockSoulseekClient([])
        {
            SearchHandler = (query, callback, options, ct) =>
            {
                try { callback(Response(1)); }
                catch (Exception error) { callbackFailure = error; }
                SignalState(query, options, SearchStates.Completed);
                return Task.FromResult(new Search(query, new SearchScope(SearchScopeType.Network), 1, SearchStates.Completed, 1, 1, 0));
            },
        };
        using var searcher = CreateSearcher(client);
        var job = new SearchJob("Artist Album");
        job.Session.RawResultAdded += (_, _) => throw observerFailure;

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            searcher.Search(job, new SearchSettings(), new ResponseData(), CancellationToken.None));

        Assert.AreSame(observerFailure, error);
        Assert.IsNull(callbackFailure, "Application observers must not run inside the library's network callback.");
    }

    [TestMethod]
    public async Task Search_LargeResponseCoalescesDiscoveryWithoutLosingRawResults()
    {
        const int fileCount = 2_000;
        var events = new DownloadEvents();
        var counts = new List<int>();
        events.JobDiscoveryChanged += job => counts.Add(job.Discovery!.RawResultCount);
        var client = new MockSoulseekClient([new SearchResponse("User", 1, true, 1000, 0,
            Enumerable.Range(0, fileCount).Select(i => TestHelpers.CreateSlFile($@"Artist\Album\Track {i}.mp3", length: 180)).ToArray())]);
        using var searcher = CreateSearcher(client, events);
        var job = new SearchJob("Artist Album");
        int rawEvents = 0;
        job.Session.RawResultAdded += (_, _) => rawEvents++;

        await searcher.Search(job, new SearchSettings(), new ResponseData(), CancellationToken.None);

        Assert.AreEqual(fileCount, job.ResultCount);
        Assert.AreEqual(fileCount, job.RawSnapshot().Count);
        Assert.AreEqual(fileCount, rawEvents, "Raw result consumers must still receive every file.");
        Assert.IsTrue(counts.Count is > 0 and <= 10, $"A single response generated {counts.Count} discovery updates.");
        Assert.AreEqual(fileCount, counts[^1], "The final discovery update must be exact.");
    }

    private static Searcher CreateSearcher(MockSoulseekClient client, DownloadEvents? events = null)
        => new(client, TestHelpers.CreateUserSuccessTracker(), events ?? new DownloadEvents(), 10, 10);

    private static SearchResponse Response(int i)
        => new($"User{i}", 1, true, 1000, 0, [TestHelpers.CreateSlFile($@"Artist\Album\Track {i}.mp3", length: 180)]);

    private static void SignalState(SearchQuery query, SearchOptions options, SearchStates state)
        => options.StateChanged?.Invoke((SearchStates.InProgress,
            new Search(query, new SearchScope(SearchScopeType.Network), 1, state, 0, 0, 0)));

    // Exercise the installed dependency's actual disposal error, without racing threads
    // or matching a localized exception message. Reflection is confined to this fixture.
    private static SynchronizationLockException CreateSearchDisposalFailure()
    {
        var type = typeof(SoulseekClient).Assembly.GetType("Soulseek.SearchInternal", throwOnError: true)!;
        var search = Activator.CreateInstance(type, SearchQuery.FromText("test"), new SearchScope(SearchScopeType.Network), 1, new SearchOptions())!;
        var gate = (ReaderWriterLockSlim)type.GetProperty("ReaderWriterLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(search)!;
        gate.EnterReadLock();
        try
        {
            return Assert.ThrowsExactly<SynchronizationLockException>(() => ((IDisposable)search).Dispose());
        }
        finally
        {
            gate.ExitReadLock();
            ((IDisposable)search).Dispose();
        }
    }
}
