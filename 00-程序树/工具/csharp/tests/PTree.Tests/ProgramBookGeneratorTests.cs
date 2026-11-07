using System.Net;
using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class ProgramBookGeneratorTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _content;
        public int Calls { get; private set; }

        public StubHandler(string content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = _content } } },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static PTreeRepository Repository() => PTreeRepository.Load(RepoLocator.Root);

    [Fact]
    public void PromptContainsNodeFacts()
    {
        var repository = Repository();
        var node = repository.Nodes.First(item => item.Id is { Length: > 0 });
        var prompt = ProgramBookGenerator.BuildPrompt(repository, node.Id);
        Assert.Contains("节点 id：" + node.Id, prompt);
        Assert.Contains("名称：" + (node.Name ?? ""), prompt);
        Assert.Contains("职责：" + (node.Responsibility ?? ""), prompt);
        if (node.Files.Count > 0) Assert.Contains(node.Files[0], prompt);
        Assert.Contains("800-1200", prompt);
        Assert.Contains("路径白名单", prompt);
    }

    [Fact]
    public void OutOfWhitelistPathIsRejected()
    {
        var repository = Repository();
        var whitelist = ProgramBookGenerator.BuildWhitelist(repository);
        var invalid = ProgramBookGenerator.OutOfWhitelistPaths(
            "参考 00-程序树/不存在.txt 和 src/Evil.cs，以及合法路径 00-程序树/README.txt。", whitelist);
        Assert.Contains("00-程序树/不存在.txt", invalid);
        Assert.Contains("src/Evil.cs", invalid);
        Assert.DoesNotContain("00-程序树/README.txt", invalid);
        Assert.False(ProgramBookGenerator.ValidateOutput("见 00-程序树/不存在.txt", whitelist));
    }

    [Fact]
    public void MissingKeyFallsBackToTemplateWithoutCallingHttp()
    {
        var repository = Repository();
        var handler = new StubHandler("不应被调用");
        using var client = new DeepSeekClient(handler, new DeepSeekConfig());
        var result = ProgramBookGenerator.GenerateAsync(repository, client, null).GetAwaiter().GetResult();

        Assert.False(result.Llm);
        Assert.True(result.Fallback);
        Assert.Contains("未配置 DeepSeek API Key", result.Reason);
        Assert.Contains("程序书", result.Content);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void OutOfWhitelistOutputRetriesThenFallsBack()
    {
        var repository = Repository();
        var handler = new StubHandler("参考 00-程序树/不存在.txt");
        using var client = new DeepSeekClient(handler, new DeepSeekConfig
        {
            ApiKey = "test-key-not-real",
            BaseUrl = "https://fake.deepseek.test",
        });
        var result = ProgramBookGenerator.GenerateAsync(repository, client, null).GetAwaiter().GetResult();

        Assert.False(result.Llm);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("白名单外路径", result.Reason);
        Assert.Equal(2, handler.Calls);
    }
}
