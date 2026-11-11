using Windows.Media.Control;

namespace PodBar;

/// <summary>Pauses whatever is playing when you stop listening and resumes it when you start again.</summary>
public sealed class MediaController
{
    const GlobalSystemMediaTransportControlsSessionPlaybackStatus Playing =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    const GlobalSystemMediaTransportControlsSessionPlaybackStatus Paused =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

    readonly List<string> _pausedApps = []; // apps we paused, so only those are resumed
    readonly SemaphoreSlim _gate = new(1, 1); // one pause/resume at a time, in order
    bool _onlyWhenBothOut;
    (bool Left, bool Right)? _ears;
    bool? _listening;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// false: taking out either pod pauses. true: playback continues with one pod in, and pauses only when both are out.
    /// </summary>
    public bool OnlyWhenBothOut
    {
        get => _onlyWhenBothOut;
        set
        {
            if (_onlyWhenBothOut == value) return;
            _onlyWhenBothOut = value;
            // Re-evaluate the baseline under the new rule, so the next pod change is judged correctly
            // and the mode change itself never pauses or resumes anything.
            if (_ears is var (left, right)) _listening = IsListening(left, right);
        }
    }

    bool IsListening(bool left, bool right) => _onlyWhenBothOut ? left || right : left && right;

    /// <summary>
    /// Call when the AirPods go quiet (closed case, out of range). The next sighting then only sets a new
    /// baseline: music paused before the AirPods went away is not resumed when they come back much later.
    /// </summary>
    public void Reset()
    {
        _ears = null;
        _listening = null;
        _pausedApps.Clear();
    }

    /// <summary>
    /// Call with the in-ear state whenever it changes. The returned task completes once any pause or
    /// resume has been sent, so the caller can wait for a pause before moving the sound to another device.
    /// </summary>
    public async Task OnEarsAsync(bool left, bool right)
    {
        _ears = (left, right);
        bool listening = IsListening(left, right);
        bool? previous = _listening;
        _listening = listening;

        string rule = _onlyWhenBothOut ? "pause when both out" : "pause when either out";
        Log.Write($"Ears: left={left} right={right} -> listening={listening} (was {previous?.ToString() ?? "unknown"}; rule: {rule}; auto pause {(Enabled ? "on" : "off")})");
        if (previous is null || previous == listening || !Enabled) return;

        await _gate.WaitAsync();
        try
        {
            // The state may have flipped again while an earlier pause/resume was in flight; then this one is moot.
            if (_listening != listening) return;

            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();

            if (!listening)
            {
                // Pause every app that is playing, not just the one Windows considers "current".
                _pausedApps.Clear();
                foreach (var session in sessions)
                {
                    if (session.GetPlaybackInfo().PlaybackStatus != Playing) continue;
                    bool ok = await session.TryPauseAsync();
                    Log.Write($"Pause {session.SourceAppUserModelId}: {ok}");
                    if (ok) _pausedApps.Add(session.SourceAppUserModelId);
                }
                if (_pausedApps.Count == 0) Log.Write($"Nothing was playing ({sessions.Count} media session(s) found)");
            }
            else if (_pausedApps.Count > 0)
            {
                foreach (var session in sessions)
                {
                    if (!_pausedApps.Contains(session.SourceAppUserModelId)) continue;
                    // Only resume what is still paused; if the user stopped or restarted it meanwhile, leave it.
                    if (session.GetPlaybackInfo().PlaybackStatus != Paused) continue;
                    Log.Write($"Resume {session.SourceAppUserModelId}: {await session.TryPlayAsync()}");
                }
                _pausedApps.Clear();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Media control failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes the media sessions Windows exposes to the log (read-only diagnostic).</summary>
    public static async void LogSessions()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            Log.Write($"Media sessions: {sessions.Count}");
            foreach (var session in sessions)
                Log.Write($"  {session.SourceAppUserModelId}: {session.GetPlaybackInfo().PlaybackStatus}");
        }
        catch (Exception ex)
        {
            Log.Write($"Listing media sessions failed: {ex.Message}");
        }
    }
}
