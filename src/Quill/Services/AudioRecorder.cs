using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Quill.Services;

public class AudioRecorder : IDisposable
{
    private MediaCapture? _mediaCapture;
    private LowLagMediaRecording? _mediaRecording;
    private bool _isRecording;
    private System.Threading.Timer? _timer;
    private DateTime _startTime;

    public bool IsRecording => _isRecording;
    public long RecordingStartTicks { get; private set; }
    public TimeSpan ElapsedTime => _isRecording ? DateTime.UtcNow - _startTime : TimeSpan.Zero;

    public event Action<TimeSpan>? ElapsedChanged;

    /// <summary>True when <paramref name="path"/> already holds a recording that
    /// must not be written over. Errs towards "occupied": if the file cannot be
    /// inspected we refuse to touch it rather than risk destroying a take.</summary>
    public static bool HasAudioContent(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return true; }
    }

    /// <summary>Picks the file for a NEW take on a page: the plain per-page name
    /// while it is free, then numbered takes. Never returns a path that already
    /// holds audio, so starting a recording cannot overwrite an earlier one.</summary>
    public static string NextTakePath(string dir, Guid pageId)
    {
        var first = Path.Combine(dir, $"{pageId}.m4a");
        if (!HasAudioContent(first)) return first;
        for (int i = 2; i < 1000; i++)
        {
            var p = Path.Combine(dir, $"{pageId}-take{i}.m4a");
            if (!HasAudioContent(p)) return p;
        }
        return Path.Combine(dir, $"{pageId}-take{DateTime.Now:yyyyMMdd-HHmmss-fff}.m4a");
    }

    public async Task StartRecordingAsync(string filePath)
    {
        if (_isRecording) return;

        // Ensure directory exists
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // MediaCapture needs the target file to already exist, but creating it
        // must never destroy a take that is already there: the old code wrote an
        // empty file unconditionally, so tapping record a second time truncated
        // the previous recording to zero bytes BEFORE the microphone was even
        // opened — unrecoverable even if the user stopped immediately.
        // Only ever create a missing (or already-empty) file; anything with
        // content in it is refused, and the caller records to a new path.
        if (File.Exists(filePath))
        {
            long existing;
            try { existing = new FileInfo(filePath).Length; }
            catch { existing = -1; }   // cannot tell: assume occupied
            if (existing != 0)
                throw new InvalidOperationException(
                    "A recording already exists at this path. Record to a new file instead.");
        }
        else
        {
            File.WriteAllBytes(filePath, Array.Empty<byte>());
        }

        _mediaCapture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Audio,
            MediaCategory = MediaCategory.Speech
        };

        try
        {
            await _mediaCapture.InitializeAsync(settings);
        }
        catch (Exception ex)
        {
            _mediaCapture.Dispose();
            _mediaCapture = null;
            throw new InvalidOperationException("Failed to initialize audio capture device. Ensure microphone access is enabled.", ex);
        }

        // Windows has no MP3 *encoder* for MediaCapture — recording to MP3
        // throws at runtime on most machines. AAC in an .m4a container is the
        // supported, efficient choice (#55).
        var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
        var file = await StorageFile.GetFileFromPathAsync(filePath);

        try
        {
            _mediaRecording = await _mediaCapture.PrepareLowLagRecordToStorageFileAsync(profile, file);
            await _mediaRecording.StartAsync();
            
            _isRecording = true;
            _startTime = DateTime.UtcNow;
            RecordingStartTicks = DateTime.UtcNow.Ticks;

            _timer = new System.Threading.Timer(OnTimerTick, null, 1000, 1000);
        }
        catch (Exception)
        {
            Cleanup();
            throw;
        }
    }

    private void OnTimerTick(object? state)
    {
        ElapsedChanged?.Invoke(ElapsedTime);
    }

    public async Task<TimeSpan> StopRecordingAsync()
    {
        if (!_isRecording) return TimeSpan.Zero;

        var duration = ElapsedTime;

        if (_mediaRecording != null)
        {
            try
            {
                await _mediaRecording.StopAsync();
                await _mediaRecording.FinishAsync();
            }
            catch { }
        }

        Cleanup();
        return duration;
    }

    private void Cleanup()
    {
        _isRecording = false;
        
        if (_timer != null)
        {
            _timer.Dispose();
            _timer = null;
        }

        if (_mediaRecording != null)
        {
            _mediaRecording = null;
        }

        if (_mediaCapture != null)
        {
            _mediaCapture.Dispose();
            _mediaCapture = null;
        }
    }

    public void Dispose()
    {
        Cleanup();
    }
}
