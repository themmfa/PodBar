using Windows.Media.Control;
using Windows.Storage.Streams;

namespace PodBar;

public sealed record TrackInfo(string Title, string Artist, bool Playing);

public enum MediaCommand { Previous, PlayPause, Next }

/// <summary>Reads what Windows says is currently playing (any app that shows up in the media flyout) and controls it.</summary>
public sealed class NowPlaying : IDisposable
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    string? _artKey;

    /// <summary>Album art for the last track returned by <see cref="GetAsync"/>, when the app provides one.</summary>
    public Image? Art { get; private set; }

    async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return _manager.GetCurrentSession();
    }

    public async Task<TrackInfo?> GetAsync()
    {
        try
        {
            var session = await SessionAsync();
            if (session is null) return null;

            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null || string.IsNullOrWhiteSpace(props.Title)) return null;

            // Apps often publish the thumbnail a moment after the title, so keep trying while we have none.
            string key = props.Title + "\n" + props.Artist;
            if (key != _artKey || Art is null)
            {
                var art = await LoadArtAsync(props.Thumbnail);
                if (key != _artKey || art is not null)
                {
                    var old = Art;
                    Art = art;
                    old?.Dispose();
                }
                _artKey = key;
            }

            bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return new TrackInfo(props.Title, props.Artist ?? "", playing);
        }
        catch (Exception ex)
        {
            Log.Write($"Now playing lookup failed: {ex.Message}");
            return null;
        }
    }

    public async Task SendAsync(MediaCommand command)
    {
        try
        {
            var session = await SessionAsync();
            if (session is null) return;
            _ = command switch
            {
                MediaCommand.Previous => await session.TrySkipPreviousAsync(),
                MediaCommand.Next => await session.TrySkipNextAsync(),
                _ => await session.TryTogglePlayPauseAsync(),
            };
        }
        catch (Exception ex)
        {
            Log.Write($"Media command {command} failed: {ex.Message}");
        }
    }

    static async Task<Image?> LoadArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var winrtStream = await thumbnail.OpenReadAsync();
            using var stream = winrtStream.AsStreamForRead();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;
            using var decoded = Image.FromStream(memory);
            return new Bitmap(decoded); // detach from the stream so it can be disposed
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => Art?.Dispose();
}
