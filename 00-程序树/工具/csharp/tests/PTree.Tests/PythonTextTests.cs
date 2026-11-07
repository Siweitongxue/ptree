using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>R08 依赖的 Python 文本语义：lower/归一化、码点二元组、%.2f 舍入。</summary>
public sealed class PythonTextTests
{
    [Theory]
    [InlineData("İ", "i\u0307")]
    [InlineData("ΟΣ", "ος")]
    [InlineData("ΣΣ", "σς")]
    [InlineData("ΟΣ'", "ος'")]
    [InlineData("ẞ", "ß")]
    [InlineData("STRASSE", "strasse")]
    [InlineData("straße", "straße")]
    public void LowerMatchesPythonSpecialCases(string input, string expected)
    {
        Assert.Equal(expected, PythonText.Lower(input));
    }

    [Theory]
    [InlineData("A—B，C。D🙂", "abcd")]
    [InlineData("𠀀 𠀁", "𠀀𠀁")]
    [InlineData("ẞ İ", "ßi\u0307")]
    [InlineData("ΟΣ.", "ος")]
    [InlineData("ab\tcd\u001Cef", "abcdef")]
    public void NormalizeMatchesPythonRules(string input, string expected)
    {
        Assert.Equal(expected, PythonText.Normalize(input));
    }

    [Fact]
    public void NormalizeMatchesPythonForCuratedCorpus()
    {
        var corpus = new[]
        {
            "负责程序职责", "A—B，C。D🙂", "𠀀 𠀁", "İstanbul", "ẞtraße", "ΟΣ.", "ΣΣ",
            "a\u0307b", "ＡＢＣ１２３", "ab\tcd\u001Cef", "emoji🙂🙃", "繁體擴展𠀀𠀁",
        };
        var script = "import json,sys,unicodedata\n" +
                     "def norm(t):\n" +
                     "    out=[]\n" +
                     "    for ch in t.lower():\n" +
                     "        cat=unicodedata.category(ch)\n" +
                     "        if ch.isspace() or cat.startswith(('P','S','Z')):\n" +
                     "            continue\n" +
                     "        out.append(ch)\n" +
                     "    return ''.join(out)\n" +
                     "print(json.dumps([norm(t) for t in json.loads(sys.argv[1])], ensure_ascii=False))\n";
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(JsonSerializer.Serialize(corpus));
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 python");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        using var document = JsonDocument.Parse(stdout);
        var expected = document.RootElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();
        var actual = corpus.Select(PythonText.Normalize).ToList();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0.9, "0.90")]
    [InlineData(8.0 / 9.0, "0.89")]
    [InlineData(0.125, "0.12")]
    [InlineData(0.135, "0.14")]
    [InlineData(2.675, "2.67")]
    [InlineData(0.005, "0.01")]
    [InlineData(0.994, "0.99")]
    [InlineData(1.0, "1.00")]
    [InlineData(0.0, "0.00")]
    public void Format2MatchesPython(double value, string expected)
    {
        Assert.Equal(expected, PythonText.Format2(value));
    }

    [Fact]
    public void JaccardUsesCodePointBigrams()
    {
        Assert.Equal(0.9, PythonText.Jaccard("abcdefghijk", "abcdefghij"), 12);
        Assert.Equal(8.0 / 9.0, PythonText.Jaccard("abcdefghij", "abcdefghi"), 12);
        Assert.Equal(1.0, PythonText.Jaccard("𠀀𠀁𠀂", "𠀀𠀁𠀂"), 12);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "a")]
    [InlineData("a\n", "a")]
    [InlineData("a\n\n", "a\u0001")]
    [InlineData("\n", "")]
    [InlineData("\n\n", "\u0001")]
    [InlineData("a\r\nb\rc\nd\ve\ff", "a\u0001b\u0001c\u0001d\u0001e\u0001f")]
    [InlineData("a\u001cb\u001dc\u001ed", "a\u0001b\u0001c\u0001d")]
    [InlineData("a\u0085b\u2028c\u2029d", "a\u0001b\u0001c\u0001d")]
    [InlineData("a\r\nb\u0085c\u2028d", "a\u0001b\u0001c\u0001d")]
    public void SplitLinesMatchesPython(string input, string expectedJoined)
    {
        var actual = string.Join("\u0001", PythonText.SplitLines(input));
        Assert.Equal(expectedJoined, actual);
    }
}
