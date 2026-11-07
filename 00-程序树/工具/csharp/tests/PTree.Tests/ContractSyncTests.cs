using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class ContractSyncTests
{
    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool Ok(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
    }

    [Fact]
    public void PresetConversionRegistersContractsAndRemovesOldProducer()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-contract36-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var plan = InitRunner.Plan(root, "契约同步", force: true);
            var init = InitRunner.Write(plan);
            Assert.True(init.Ok);

            var addArgs = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["parent"] = "core",
                ["name"] = "契约槽位",
                ["slug"] = "contract-slot",
                ["kind"] = "program",
                ["responsibility"] = "将来承担契约同步测试职责",
                ["intro"] = new string('说', 220),
                ["preset"] = true,
                ["outputs_none"] = true,
            });
            var add = NodeTools.AddNode(root, addArgs);
            Assert.True(Ok(add), add);

            var convert = NodeTools.UpdateNode(root, Args("""
            {"id":"core.contract-slot","preset":false,"files":["10-程序/模块/contract-slot.txt"],
             "outputs":["app.result"]}
            """));
            Assert.True(Ok(convert), convert);

            var repository = PTreeRepository.Load(root);
            var validation = Validator.Validate(repository);
            Assert.Empty(validation.Errors);
            Assert.Contains("core.contract-slot", repository.Contracts.Contracts.First(c => c.Id == "app.result").Producers);
            Assert.Equal("10-程序/模块/contract-slot.txt", repository.NodeMap["core.contract-slot"].Files.Single());

            var changeOutputs = NodeTools.UpdateNode(root, Args("""
            {"id":"core.contract-slot","outputs":["user.request"]}
            """));
            Assert.True(Ok(changeOutputs), changeOutputs);
            repository = PTreeRepository.Load(root);
            Assert.DoesNotContain("core.contract-slot", repository.Contracts.Contracts.First(c => c.Id == "app.result").Producers);
            Assert.Contains("core.contract-slot", repository.Contracts.Contracts.First(c => c.Id == "user.request").Producers);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
