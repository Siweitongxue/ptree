using PTree.Core;
using Xunit;

namespace PTree.Tests;

public sealed class TreeStoreConfigTests
{
    [Fact]
    public void ConfiguredNonStrictTreeLoads()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptree-treeconfig-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "00-程序树", "树实例"));
            File.WriteAllText(Path.Combine(root, "00-程序树", "program-tree.yaml"),
                "schema_version: 1\ntree_version: 0.1.0\nupdated: '2026-11-06'\nroot: pilot\n" +
                "trees:\n- kind: 音效树\n  file: 音效树\n  id_pattern: ^S[0-9]{2}$\n  strict: false\n",
                new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "00-程序树", "树实例", "音效树.txt"),
                "音效树\n版本 1 ｜ 2026-11-06\n每行字段顺序：编号 ｜ 名称 ｜ 说明\nS01 ｜ 确认音 ｜ 完成\nS02 ｜ 拒绝音 ｜ 拒绝\n",
                new System.Text.UTF8Encoding(false));
            var store = TreeStore.Load(root);
            var document = store.FindDocument("音效树");
            Assert.NotNull(document);
            Assert.False(document!.Strict);
            Assert.Equal(2, document.Entries.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
