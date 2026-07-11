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
    public string? LastError { get; private set; }
    public event Action<string>? TextRecognized;
    public event Action? Stopped;

    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;
        LastError = null;
        try
        {
            // no explicit constraint: the recognizer's built-in default IS the
            // dictation grammar, and the topic constraint was one more thing
            // that could fail offline. Prefer the system speech language.
            try { _rec = new SpeechRecognizer(SpeechRecognizer.SystemSpeechLanguage); }
            catch { _rec = new SpeechRecognizer(); }
            var compile = await _rec.CompileConstraintsAsync();
            if (compile.Status != SpeechRecognitionResultStatus.Success)
            {
                LastError = "the speech engine rejected its grammar (" + compile.Status + "). Is a speech language pack installed for your display language?";
                Cleanup();
                return false;
            }

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
        catch (Exception ex)
        {
            LastError = (uint)ex.HResult == 0x80045509
                ? "Windows' speech consent is off. Turn ON 'Online speech recognition' under Settings > Privacy & security > Speech, then try again."
                : ex.Message + " (check Settings > Privacy & security > Speech and > Microphone)";
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
