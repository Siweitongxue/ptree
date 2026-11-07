using System.Net;
using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class DeepSeekClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        public int Calls { get; private set; }
        public string? Body { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(_response(request));
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new TaskCanceledException("simulated timeout");
        }
    }

    private static DeepSeekConfig Config() => new()
    {
        ApiKey = "test-key-not-real",
        BaseUrl = "https://fake.deepseek.test",
        Model = "deepseek-chat",
        Source = "test",
    };

    [Fact]
    public void SuccessParsesContentAndSendsOpenAiCompatibleBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = "生成正文" } } },
            }), Encoding.UTF8, "application/json"),
        });
        using var client = new DeepSeekClient(handler, Config());
        var result = client.ChatAsync("测试 prompt").GetAwaiter().GetResult();

        Assert.True(result.Ok);
        Assert.Equal("生成正文", result.Content);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("\"model\":\"deepseek-chat\"", handler.Body);
        Assert.Contains("\"role\":\"user\"", handler.Body);
        Assert.Contains("测试 prompt", handler.Body);
    }

    [Fact]
    public void UnauthorizedReturnsStructuredErrorWithoutKey()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new DeepSeekClient(handler, Config());
        var result = client.ChatAsync("prompt").GetAwaiter().GetResult();

        Assert.False(result.Ok);
        Assert.Contains("401", result.Error);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain("test-key-not-real", result.Error);
    }

    [Fact]
    public void TimeoutRetriesOnceAndReturnsFallbackError()
    {
        var handler = new TimeoutHandler();
        using var client = new DeepSeekClient(handler, Config());
        var result = client.ChatAsync("prompt").GetAwaiter().GetResult();

        Assert.False(result.Ok);
        Assert.Contains("超时", result.Error);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, handler.Calls);
    }
}
