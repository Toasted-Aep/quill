using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Quill.Services;

/// <summary>
/// Minimal multi-provider AI client: Claude, OpenAI, Gemini, or any local
/// OpenAI-compatible server (Ollama / LM Studio). API keys live in the Windows
/// Credential Locker, never in library.json.
/// </summary>
public static class AiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private const string VaultResource = "Quill.AI";

    public static string? GetKey(string provider)
    {
        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            var cred = vault.Retrieve(VaultResource, provider);
            cred.RetrievePassword();
            return cred.Password;
        }
        catch { return null; }
    }

    public static void SetKey(string provider, string key)
    {
        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            try { vault.Remove(vault.Retrieve(VaultResource, provider)); } catch { }
            if (!string.IsNullOrWhiteSpace(key))
                vault.Add(new Windows.Security.Credentials.PasswordCredential(VaultResource, provider, key));
        }
        catch { }
    }

    public static string DefaultModel(string provider) => provider switch
    {
        "Claude" => "claude-sonnet-5",
        "OpenAI" => "gpt-4o-mini",
        "Gemini" => "gemini-2.0-flash",
        "Local" => "llama3",
        _ => ""
    };

    public static async Task<string> CompleteAsync(
        string provider, string? model, string? endpoint, string? apiKey, string system, string user)
    {
        model = string.IsNullOrWhiteSpace(model) ? DefaultModel(provider) : model.Trim();
        return provider switch
        {
            "Claude" => await ClaudeAsync(model, apiKey!, system, user),
            "Gemini" => await GeminiAsync(model, apiKey!, system, user),
            "OpenAI" => await OpenAiCompatAsync("https://api.openai.com/v1", model, apiKey, system, user),
            "Local" => await OpenAiCompatAsync(
                string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434/v1" : endpoint.TrimEnd('/'),
                model, apiKey, system, user),
            _ => throw new InvalidOperationException("No AI provider selected.")
        };
    }

    private static async Task<string> ClaudeAsync(string model, string apiKey, string system, string user)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 1500,
            system,
            messages = new[] { new { role = "user", content = user } }
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(ShortError(json, resp.StatusCode.ToString()));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> GeminiAsync(string model, string apiKey, string system, string user)
    {
        var body = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { parts = new[] { new { text = user } } } }
        });
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        using var resp = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(ShortError(json, resp.StatusCode.ToString()));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> OpenAiCompatAsync(string baseUrl, string model, string? apiKey, string system, string user)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(ShortError(json, resp.StatusCode.ToString()));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string ShortError(string json, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? fallback;
                return err.ToString();
            }
        }
        catch { }
        return fallback;
    }
}
