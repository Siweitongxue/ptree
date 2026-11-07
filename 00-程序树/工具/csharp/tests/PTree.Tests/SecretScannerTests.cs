using System.Text;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class SecretScannerTests
{
    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "aws-access-key")]
    [InlineData("ghp_0123456789abcdefghijklmnopqrst", "github-token")]
    [InlineData("sk-proj-0123456789abcdefghijklmn", "openai-key")]
    [InlineData("sk-ant-api03-0123456789abcdefghijkl", "anthropic-key")]
    [InlineData("xoxb-1234567890-abcdefghij", "slack-token")]
    [InlineData("AIzaAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "google-api-key")]
    [InlineData("sk_live_0123456789abcdefghij", "stripe-key")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123", "jwt")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private-key")]
    [InlineData("api_key = \"0123456789abcdef\"", "generic-assignment")]
    public void DetectsEachSecretPattern(string text, string kind)
    {
        var findings = SecretScanner.ScanText("sample.txt", text);
        Assert.Contains(findings, finding => finding.Kind == kind);
    }

    [Fact]
    public void OrdinaryTextHasNoFindings()
    {
        var text = "这是一段普通文本。\napi_key = short\npassword: abc\n没有密钥。";
        Assert.Empty(SecretScanner.ScanText("README.txt", text));
    }

    [Fact]
    public void RedactionKeepsOnlyPrefixAndSuffix()
    {
        const string secret = "AKIAIOSFODNN7EXAMPLE";
        var finding = Assert.Single(SecretScanner.ScanText("sample.txt", secret));
        Assert.Equal("AKIAIO****MPLE", finding.Redacted);
        Assert.DoesNotContain(secret, finding.Redacted);
        Assert.Contains("****", finding.Redacted);
    }

    [Fact]
    public void SensitiveFilenamesAreDetectedAndSkippedAsBinary()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var key = Path.Combine(root, "id_rsa");
            File.WriteAllText(key, "ordinary", new UTF8Encoding(false));
            var hidden = Path.Combine(root, ".env");
            File.WriteAllText(hidden, "A=1", new UTF8Encoding(false));
            var findings = SecretScanner.ScanDirectory(root, root);
            Assert.Contains(findings, finding => finding.Kind == SecretScanner.FilenameKind && finding.Redacted == "id_rsa");
            Assert.Contains(findings, finding => finding.Kind == SecretScanner.FilenameKind && finding.Redacted == ".env");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
