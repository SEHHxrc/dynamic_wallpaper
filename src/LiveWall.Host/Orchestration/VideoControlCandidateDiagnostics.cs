using System.Diagnostics;
using LiveWall.Domain.Playback;
using LiveWall.Host.CommandLoop;

namespace LiveWall.Host.Orchestration;

public enum VideoControlCandidateStage { Playing, Paused, ReattachedPaused, Resumed }
public sealed record VideoControlCandidateStageResult(VideoControlCandidateStage Stage, long Generation,
    TimeSpan Started, TimeSpan Held);
public sealed record VideoControlCandidateResult(bool SameRenderer, bool FreshSurface,
    DesktopRecoveryResult Recovery, IReadOnlyList<VideoControlCandidateStageResult> Stages);

// Diagnostic sequence using the product actor and current-generation protocol replies.
// Reattachment is synthetic: no Explorer restart or Shell generation claim.
internal static class VideoControlCandidateDiagnostics
{
    internal static async Task<VideoControlCandidateResult> RunAsync(HostCommandLoop host,
        TimeSpan hold, Action<VideoControlCandidateStage>? observer, CancellationToken token)
    {
        PreparedSession initial = host.GetPreparedSessionsForDiagnostics().Single();
        RendererProtocolSession renderer = initial.Renderer as RendererProtocolSession ??
            throw new InvalidOperationException("Video control acceptance requires the formal protocol session.");
        Stopwatch clock = Stopwatch.StartNew();
        List<VideoControlCandidateStageResult> stages = [];
        await Feedback(PlaybackState.Playing, initial.Session.Generation, 0);
        await Hold(VideoControlCandidateStage.Playing, initial.Session.Generation);
        long beforePause = LastSequence();
        await host.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Pause), token).ConfigureAwait(false);
        await Feedback(PlaybackState.Paused, initial.Session.Generation, beforePause);
        await Hold(VideoControlCandidateStage.Paused, initial.Session.Generation);
        var existingRecoveries = host.GetRecoveryOperationIdsForDiagnostics().ToHashSet();
        await host.SendAsync(new ExplorerRestartedCommand(), token).ConfigureAwait(false);
        long id = host.GetRecoveryOperationIdsForDiagnostics().Single(value => !existingRecoveries.Contains(value));
        DesktopRecoveryResult recovery = await host.WaitForDesktopRecoveryAsync(id, token).ConfigureAwait(false);
        PreparedSession current = host.GetPreparedSessionsForDiagnostics().Single();
        bool sameRenderer = ReferenceEquals(current.Renderer, renderer) && current.Session.Id == initial.Session.Id;
        bool freshSurface = current.Surfaces.Single().Surface.Id != initial.Surfaces.Single().Surface.Id;
        if (recovery.TerminalState != DesktopRecoveryTerminalState.Succeeded ||
            recovery.CompletionMode != DesktopRecoveryCompletionMode.Reattached || !sameRenderer || !freshSurface)
            throw new InvalidOperationException("Controlled reattachment did not retain the Renderer with a fresh Surface.");
        await Feedback(PlaybackState.Paused, current.Session.Generation, 0);
        await Hold(VideoControlCandidateStage.ReattachedPaused, current.Session.Generation);
        long beforeResume = LastSequence();
        await host.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Play), token).ConfigureAwait(false);
        await Feedback(PlaybackState.Playing, current.Session.Generation, beforeResume);
        await Hold(VideoControlCandidateStage.Resumed, current.Session.Generation);
        return new(sameRenderer, freshSurface, recovery, stages);

        long LastSequence()
        {
            var entries = renderer.GetPlaybackEvidenceForDiagnostics();
            return entries.Count == 0 ? 0 : entries[^1].Sequence;
        }
        async Task Feedback(PlaybackState state, long generation, long after)
        {
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            while (!renderer.GetPlaybackEvidenceForDiagnostics().Any(e => e.Sequence > after &&
                e.Kind == RendererPlaybackEvidenceKind.FeedbackReceived && e.Generation == generation && e.State == state))
            {
                if (renderer.ActiveFailure.IsCompleted) throw new InvalidOperationException("Renderer failed while awaiting playback feedback.");
                await Task.Delay(10, budget.Token).ConfigureAwait(false);
            }
        }
        async Task Hold(VideoControlCandidateStage stage, long generation)
        {
            observer?.Invoke(stage);
            TimeSpan started = clock.Elapsed;
            await Task.Delay(hold, token).ConfigureAwait(false);
            stages.Add(new(stage, generation, started, clock.Elapsed - started));
        }
    }
}
