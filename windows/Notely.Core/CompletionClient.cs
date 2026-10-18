using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Notely.Core;

public sealed record CompletionRequest(string Model, string System, string Context, byte[]? Image = null, bool Summary = false);

public interface ICompletionClient
{
    IAsyncEnumerable<string> StreamAsync(CompletionRequest request, CancellationToken cancellationToken);
}

public sealed class OpenRouterClient(HttpClient http, string key) : ICompletionClient
{
    public static object CreateBody(CompletionRequest request)
    {
        var parts = new List<object> { new { type = "text", text = request.Context } };
        if (request.Image is { Length: > 0 })
            parts.Add(new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(request.Image) } });
        return new
        {
            model = request.Model, stream = true, max_tokens = request.Summary ? 1200 : 1000,
            temperature = request.Summary ? 0.1 : 0.3,
            messages = new object[] { new { role = "system", content = request.System }, new { role = "user", content = parts } }
        };
    }

    public async IAsyncEnumerable<string> StreamAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Summary ? 90 : 60));
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(CreateBody(request));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"OpenRouter HTTP {(int)response.StatusCode}: " + (response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "проверьте ключ",
                System.Net.HttpStatusCode.PaymentRequired => "недостаточно средств",
                System.Net.HttpStatusCode.TooManyRequests => "лимит запросов, повторите позже",
                _ => "запрос не выполнен"
            }));
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var ended = false;
        await foreach (var payload in ServerEvents.ReadAsync(reader, timeout.Token))
        {
            if (payload == "[DONE]") { ended = true; break; }
            JsonDocument document;
            try { document = JsonDocument.Parse(payload); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("error", out _)) throw new IOException("OpenRouter: провайдер прервал ответ");
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (choice.ValueKind != JsonValueKind.Object) continue;
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    if (finish.GetString() == "error") throw new IOException("OpenRouter: ошибка генерации");
                    ended = true;
                }
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } value)
                    yield return value;
            }
        }
        if (!ended) throw new IOException("OpenRouter: соединение оборвалось до конца ответа");
    }
}

public static class ServerEvents
{
    public static async IAsyncEnumerable<string> ReadAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 2_000_000) throw new IOException("Слишком большой ответ сервера");
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                if (data.Length > 2_000_000) throw new IOException("Слишком большой ответ сервера");
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }
}
