using System;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.Storage;

namespace Quill.Services;

public class AudioPlayer : IDisposable
{
    private MediaPlayer? _mediaPlayer;
    private System.Threading.Timer? _timer;

    public event Action<TimeSpan>? PositionChanged;
    public event Action? PlaybackEnded;

    public bool IsPlaying => _mediaPlayer?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public TimeSpan Position
    {
        get => _mediaPlayer?.PlaybackSession.Position ?? TimeSpan.Zero;
        set { if (_mediaPlayer != null) _mediaPlayer.PlaybackSession.Position = value; }
    }

    public TimeSpan Duration => _mediaPlayer?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;

    public double PlaybackRate
    {
        get => _mediaPlayer?.PlaybackSession.PlaybackRate ?? 1.0;
        set { if (_mediaPlayer != null) _mediaPlayer.PlaybackSession.PlaybackRate = value; }
    }

    public async Task OpenAsync(string filePath)
    {
        Close();

        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.MediaEnded += OnMediaEnded;
        
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        _mediaPlayer.Source = MediaSource.CreateFromStorageFile(file);

        _timer = new System.Threading.Timer(OnTimerTick, null, 100, 100);
    }

    public void Play()
    {
        _mediaPlayer?.Play();
    }

    public void Pause()
    {
        _mediaPlayer?.Pause();
    }

    public void SeekTo(TimeSpan position)
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.PlaybackSession.Position = position;
            PositionChanged?.Invoke(position);
        }
    }

    private void OnTimerTick(object? state)
    {
        if (IsPlaying)
        {
            PositionChanged?.Invoke(Position);
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        PlaybackEnded?.Invoke();
    }

    public void Close()
    {
        if (_timer != null)
        {
            _timer.Dispose();
            _timer = null;
        }

        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaEnded -= OnMediaEnded;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }
    }

    public static TimeSpan StrokeTicksToAudioPosition(long strokeTicks, long recordingStartTicks)
    {
        long diff = strokeTicks - recordingStartTicks;
        if (diff < 0) diff = 0;
        return TimeSpan.FromTicks(diff);
    }

    public void Dispose()
    {
        Close();
    }
}
