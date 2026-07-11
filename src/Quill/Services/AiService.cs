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

    // Current-generation defaults (#11-batch3) — override in Settings any time.
    public static string DefaultModel(string provider) => provider switch
    {
        "Claude" => "claude-sonnet-5",
        "OpenAI" => "gpt-5.1",
        "Gemini" => "gemini-3-flash-preview",
        "Local" => "llama3.3",
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

    /// <summary>Multi-turn chat, optionally attaching a PNG of the page to the
    /// last user message so the model sees the actual ink (#22-batch3).</summary>
    public static async Task<string> ChatAsync(
        string provider, string? model, string? endpoint, string? apiKey,
        string system, IReadOnlyList<(string Role, string Text)> messages, byte[]? pagePng)
    {
        model = string.IsNullOrWhiteSpace(model) ? DefaultModel(provider) : model.Trim();
        string? b64 = pagePng != null ? Convert.ToBase64String(pagePng) : null;
        return provider switch
        {
            "Claude" => await ClaudeChatAsync(model, apiKey!, system, messages, b64),
            "Gemini" => await GeminiChatAsync(model, apiKey!, system, messages, b64),
            "OpenAI" => await OpenAiChatAsync("https://api.openai.com/v1", model, apiKey, system, messages, b64),
            "Local" => await OpenAiChatAsync(
                string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434/v1" : endpoint.TrimEnd('/'),
                model, apiKey, system, messages, b64),
            _ => throw new InvalidOperationException("No AI provider selected.")
        };
    }

    private static async Task<string> ClaudeChatAsync(
        string model, string apiKey, string system,
        IReadOnlyList<(string Role, string Text)> messages, string? pngB64)
    {
        var msgs = new List<object>();
        for (int i = 0; i < messages.Count; i++)
        {
            var (role, text) = messages[i];
            bool last = i == messages.Count - 1;
            if (last && role == "user" && pngB64 != null)
                msgs.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object>
                            { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = pngB64 }
                        },
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = text }
                    }
                });
            else
                msgs.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = text });
        }
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = 1500,
            ["system"] = system,
            ["messages"] = msgs
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

    private static async Task<string> OpenAiChatAsync(
        string baseUrl, string model, string? apiKey, string system,
        IReadOnlyList<(string Role, string Text)> messages, string? pngB64)
    {
        var msgs = new List<object> { new Dictionary<string, object> { ["role"] = "system", ["content"] = system } };
        for (int i = 0; i < messages.Count; i++)
        {
            var (role, text) = messages[i];
            bool last = i == messages.Count - 1;
            if (last && role == "user" && pngB64 != null)
                msgs.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object> { ["url"] = "data:image/png;base64," + pngB64 }
                        },
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = text }
                    }
                });
            else
                msgs.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = text });
        }
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["model"] = model, ["messages"] = msgs });
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(ShortError(json, resp.StatusCode.ToString()));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static async Task<string> GeminiChatAsync(
        string model, string apiKey, string system,
        IReadOnlyList<(string Role, string Text)> messages, string? pngB64)
    {
        var contents = new List<object>();
        for (int i = 0; i < messages.Count; i++)
        {
            var (role, text) = messages[i];
            bool last = i == messages.Count - 1;
            var parts = new List<object>();
            if (last && role == "user" && pngB64 != null)
                parts.Add(new Dictionary<string, object>
                {
                    ["inline_data"] = new Dictionary<string, object> { ["mime_type"] = "image/png", ["data"] = pngB64 }
                });
            parts.Add(new Dictionary<string, object> { ["text"] = text });
            contents.Add(new Dictionary<string, object>
            {
                ["role"] = role == "assistant" ? "model" : "user",
                ["parts"] = parts
            });
        }
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["systemInstruction"] = new Dictionary<string, object>
            { ["parts"] = new object[] { new Dictionary<string, object> { ["text"] = system } } },
            ["contents"] = contents
        });
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        using var resp = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(ShortError(json, resp.StatusCode.ToString()));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
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
