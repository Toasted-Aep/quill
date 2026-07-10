using Windows.Media.SpeechRecognition;

namespace Quill.Services;

/// <summary>
/// Continuous speech-to-text using Windows' built-in dictation engine
/// (Windows.Media.SpeechRecognition, the user's Windows display language).
/// Finalised segments are raised via <see cref="TextRecognized"/>; callers
/// marshal to the UI thread themselves.
/// </summary>
public sealed class DictationService : IDisposable
{
    private SpeechRecognizer? _rec;

    public bool IsRunning { get; private set; }
    public event Action<string>? TextRecognized;
    public event Action? Stopped;

    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;
        try
        {
            _rec = new SpeechRecognizer();
            _rec.Constraints.Add(new SpeechRecognitionTopicConstraint(
                SpeechRecognitionScenario.Dictation, "dictation"));
            var compile = await _rec.CompileConstraintsAsync();
            if (compile.Status != SpeechRecognitionResultStatus.Success) { Cleanup(); return false; }

            _rec.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
            {
                if (e.Result.Status == SpeechRecognitionResultStatus.Success &&
                    !string.IsNullOrWhiteSpace(e.Result.Text))
                    TextRecognized?.Invoke(e.Result.Text);
            };
            _rec.ContinuousRecognitionSession.Completed += (_, _) =>
            {
                IsRunning = false;
                Stopped?.Invoke();
            };
            await _rec.ContinuousRecognitionSession.StartAsync();
            IsRunning = true;
            return true;
        }
        catch
        {
            // no mic, mic denied, or "online speech recognition" disabled in
            // Windows privacy settings
            Cleanup();
            return false;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            if (IsRunning && _rec != null)
                await _rec.ContinuousRecognitionSession.StopAsync();
        }
        catch { }
        Cleanup();
    }

    private void Cleanup()
    {
        IsRunning = false;
        try { _rec?.Dispose(); } catch { }
        _rec = null;
    }

    public void Dispose() => Cleanup();
}
