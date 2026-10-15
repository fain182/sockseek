using Sockseek.Api;
using Sockseek.Cli;

namespace Tests.Cli;

internal static class BackendTestWait
{
    public static async Task AlbumChoicesReadyAsync(ICliBackend backend, int count)
    {
        using var signal = new TestSignal().Subscribe<ServerEventEnvelopeDto>(
            handler => backend.EventReceived += handler, handler => backend.EventReceived -= handler);
        await signal.WaitUntilAsync(async () =>
        {
            var jobs = await backend.GetJobsAsync(new JobQuery(null, null, null, null, IncludeAll: true));
            return jobs.Count(job => job.Kind == ServerJobKind.Album
                && job.LifecycleState == ServerJobLifecycleState.AwaitingSelection) >= count;
        }, $"Expected {count} albums ready for selection.");
    }
}
