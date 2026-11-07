using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>YamlDotNet 与 PyYAML 的解析语义对照：只比解析，不掺规则。</summary>
public sealed class YamlOracleTests
{
    private static readonly string[] Corpus =
    {
        "yes",
        "no",
        "on",
        "off",
        "value: yes\nother: no",
        "date: 2026-10-30",
        "timestamp: 2026-10-30T12:34:56+08:00",
        "timestamp2: 2026-10-30 12:34:56",
        "hex: 0x1A",
        "octal: 0o17",
        "legacy_octal: 017",
        "underscore: 1_000",
        "sexagesimal: 1:20",
        "inf: .inf",
        "neg_inf: -.Inf",
        "nan: .nan",
        "a: 1\na: 2",
        "base: &b {x: 1, y: 2}\nchild: {<<: *b, y: 3}",
        "\uFEFFa: 1\n",
        "a:\nb: 2\n",
        "node:\n  id: c\n  layer: 2\n",
        "",
    };

    [Fact]
    public void RawYamlMatchesPyYamlForCorpus()
    {
        var python = RunPython("""
import json, sys, yaml, datetime
def canon(v):
    if v is None: return {"t": "null"}
    if isinstance(v, bool): return {"t": "bool", "v": v}
    if isinstance(v, int): return {"t": "int", "v": str(v)}
    if isinstance(v, float): return {"t": "float", "v": repr(v)}
    if isinstance(v, str): return {"t": "str", "v": v}
    if isinstance(v, datetime.datetime): return {"t": "datetime", "v": v.isoformat()}
    if isinstance(v, datetime.date): return {"t": "date", "v": v.isoformat()}
    if isinstance(v, list): return {"t": "list", "v": [canon(x) for x in v]}
    if isinstance(v, dict): return {"t": "dict", "v": [[str(k), canon(val)] for k, val in v.items()]}
    raise TypeError(type(v).__name__)
cases = json.loads(sys.argv[1])
print(json.dumps([canon(yaml.safe_load(case)) for case in cases], ensure_ascii=False))
""", JsonSerializer.Serialize(Corpus));
        var actual = Corpus.Select(caseText => Canonical(RawYaml.Parse(caseText))).ToList();
        var expected = JsonNode.Parse(python)!.AsArray();
        for (var index = 0; index < Corpus.Length; index++)
        {
            var expectedNode = expected[index];
            var actualJson = JsonSerializer.Serialize(actual[index]);
            var actualNode = JsonNode.Parse(actualJson);
            Assert.True(JsonNode.DeepEquals(expectedNode, actualNode),
                "case " + index + " " + Corpus[index] + "\nexpected=" + expectedNode!.ToJsonString() +
                "\nactual=" + actualJson);
        }
    }

    [Fact]
    public void RecordsRuntimeVersions()
    {
        var output = RunPython("import sys, yaml; print(sys.version.split()[0]); print(yaml.__version__)");
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    private static object? Canonical(object? value)
    {
        var result = new Dictionary<string, object?>();
        switch (value)
        {
            case null:
                result["t"] = "null";
                return result;
            case bool flag:
                result["t"] = "bool";
                result["v"] = flag;
                return result;
            case long number:
                result["t"] = "int";
                result["v"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return result;
            case double number:
                result["t"] = "float";
                result["v"] = PyValue.Repr(number);
                return result;
            case string text:
                result["t"] = "str";
                result["v"] = text;
                return result;
            case PyDate date:
                result["t"] = "date";
                result["v"] = date.Iso;
                return result;
            case PyDateTime dateTime:
                result["t"] = "datetime";
                result["v"] = dateTime.Iso;
                return result;
            case List<object?> list:
                result["t"] = "list";
                result["v"] = list.Select(Canonical).ToList();
                return result;
            case RawMap map:
                result["t"] = "dict";
                result["v"] = map.Select(entry => new object[] { entry.Key, Canonical(entry.Value)! }).ToList();
                return result;
            default:
                result["t"] = "other";
                result["v"] = value.ToString();
                return result;
        }
    }

    private static string RunPython(string script, string argument = "")
    {
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
        if (argument.Length > 0)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 python");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Trim();
    }
}
