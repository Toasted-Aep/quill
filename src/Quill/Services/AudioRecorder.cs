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

    public async Task StartRecordingAsync(string filePath)
    {
        if (_isRecording) return;

        // Ensure directory exists
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Ensure empty file exists so GetFileFromPathAsync succeeds
        File.WriteAllBytes(filePath, Array.Empty<byte>());

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
