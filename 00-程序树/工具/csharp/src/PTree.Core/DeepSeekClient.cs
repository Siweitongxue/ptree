using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PTree.Core;

/// <summary>DeepSeek 配置；ApiKey 只留在内存，不写入 ToString / 错误文本 / 台账。</summary>
public sealed class DeepSeekConfig
{
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-chat";
    public string Source { get; init; } = "";

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record DeepSeekResult(bool Ok, string Content, string Error, int StatusCode, int Attempts)
{
    public static DeepSeekResult Success(string content, int attempts = 1) => new(true, content, "", 200, attempts);
    public static DeepSeekResult Failure(string error, int statusCode = 0, int attempts = 1) => new(false, "", error, statusCode, attempts);
}

/// <summary>
/// OpenAI 兼容的 DeepSeek chat/completions 客户端：60 秒超时、失败重试一次，
/// 错误只带状态码与通用原因，绝不回显 API Key。
/// </summary>
public sealed class DeepSeekClient : IDisposable
{
    public const string DefaultBaseUrl = "https://api.deepseek.com";
    public const string DefaultModel = "deepseek-chat";
    public const int TimeoutSeconds = 60;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DeepSeekConfig _config;

    public DeepSeekConfig Config => _config;

    public DeepSeekClient(DeepSeekConfig config) : this(new HttpClient(), config, true)
    {
    }

    public DeepSeekClient(HttpMessageHandler handler, DeepSeekConfig config)
        : this(new HttpClient(handler, false), config, true)
    {
    }

    public DeepSeekClient(HttpClient http, DeepSeekConfig config, bool ownsHttp = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _config = config;
        try { _http.Timeout = TimeSpan.FromSeconds(TimeoutSeconds); }
        catch (InvalidOperationException) { }
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ptree-cs");
    }

    /// <summary>密钥优先级：环境变量 -> global.yaml deepseek.api_key -> credentials.enc。</summary>
    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DeepSeekConfig LoadConfig(BackupHome? home = null)
    {
        var resolvedHome = home ?? BackupHome.Resolve();
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")?.Trim() ?? "";
        var source = apiKey.Length > 0 ? "env:DEEPSEEK_API_KEY" : "";
        var baseUrl = DefaultBaseUrl;
        var model = DefaultModel;
        object? raw = null;
        try
        {
            if (File.Exists(resolvedHome.GlobalPath)) raw = YamlLoader.LoadRawDocument(resolvedHome.GlobalPath);
        }
        catch (PTreeException) { }
        if (raw is RawMap root && root.TryGetValue("deepseek", out var section) && section is RawMap deepseek)
        {
            if (apiKey.Length == 0 && deepseek.TryGetValue("api_key", out var yamlKey) && yamlKey is string yamlText && !string.IsNullOrWhiteSpace(yamlText))
            {
                apiKey = yamlText.Trim();
                source = "global.yaml:deepseek.api_key";
            }
            if (deepseek.TryGetValue("base_url", out var baseValue) && baseValue is string baseText && !string.IsNullOrWhiteSpace(baseText))
                baseUrl = baseText.Trim();
            if (deepseek.TryGetValue("model", out var modelValue) && modelValue is string modelText && !string.IsNullOrWhiteSpace(modelText))
                model = modelText.Trim();
        }
        if (apiKey.Length == 0)
        {
            try
            {
                var global = BackupGlobal.Load(resolvedHome);
                if (global.TryGetKey(out var key) && File.Exists(resolvedHome.CredentialsPath))
                {
                    var plaintext = BackupCrypto.Decrypt(key, File.ReadAllText(resolvedHome.CredentialsPath, Encoding.UTF8));
                    using var document = JsonDocument.Parse(plaintext);
                    var credentials = document.RootElement;
                    foreach (var name in new[] { "deepseek_api_key", "api_key", "token" })
                    {
                        if (credentials.TryGetProperty(name, out var value) &&
                            value.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(value.GetString()))
                        {
                            apiKey = value.GetString()!.Trim();
                            source = "credentials.enc:" + name;
                            break;
                        }
                    }
                }
            }
            catch (Exception) { }
        }
        return new DeepSeekConfig
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            Model = model,
            Source = source,
        };
    }

    public async Task<DeepSeekResult> ChatAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (!_config.Configured)
            return DeepSeekResult.Failure("未配置 DeepSeek API Key");
        var body = JsonSerializer.Serialize(new
        {
            model = _config.Model,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.3,
            max_tokens = 2400,
            stream = false,
        }, RequestOptions);
        var endpoint = _config.BaseUrl.TrimEnd('/') + "/chat/completions";
        var attempts = 0;
        var lastError = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            attempts++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = response.StatusCode == HttpStatusCode.Unauthorized
                        ? "DeepSeek 鉴权失败（HTTP 401）"
                        : "DeepSeek 请求失败（HTTP " + (int)response.StatusCode + "）";
                    continue;
                }
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(content.GetString()))
                    {
                        return DeepSeekResult.Success(content.GetString()!, attempts);
                    }
                    lastError = "DeepSeek 返回缺少 choices[0].message.content";
                }
                catch (JsonException)
                {
                    lastError = "DeepSeek 返回不是合法 JSON";
                }
            }
            catch (TaskCanceledException)
            {
                lastError = "DeepSeek 请求超时（" + TimeoutSeconds + " 秒）";
            }
            catch (HttpRequestException)
            {
                lastError = "DeepSeek 网络请求失败";
            }
            catch (Exception)
            {
                lastError = "DeepSeek 请求失败";
            }
        }
        return DeepSeekResult.Failure(lastError, 0, attempts);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
