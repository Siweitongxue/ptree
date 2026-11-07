using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PTree.Core;
using Xunit;

namespace PTree.Tests;

/// <summary>
/// 自动备份子系统测试：全部用临时项目 + 临时全局目录（PTreeBackupHome），不碰真实仓库、不写真实全局目录。
/// 覆盖：默认关闭 / 未配置拒绝启用 / 加密落盘与脱敏 / 备份点产物与 commit / retention 修剪 / bundle verify / 环境变量 / health W / R43。
/// </summary>
public sealed class BackupTests
{
    private const string Token = "ghp_SECRET_TOKEN_0123456789";

    // ------------------------------------------------------------------
    // 工厂
    // ------------------------------------------------------------------

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public string Project { get; }
        public string Home { get; }
        public string Repo { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ptree-backup-" + Guid.NewGuid().ToString("N"));
            Project = Path.Combine(Root, "project");
            Home = Path.Combine(Root, "global-home");
            Repo = Path.Combine(Root, "repo");
            Directory.CreateDirectory(Path.Combine(Project, "00-程序树", "运维"));
            Directory.CreateDirectory(Home);
            WriteManifest("1.0.0");
            Directory.CreateDirectory(Repo);
            Git(Repo, "init");
            Git(Repo, "config", "user.email", "backup-test@example.com");
            Git(Repo, "config", "user.name", "Backup Test");
            File.WriteAllText(Path.Combine(Repo, "a.txt"), "a", new UTF8Encoding(false));
            Git(Repo, "add", "-A");
            Git(Repo, "commit", "-m", "init");
        }

        public string BackupConfigPath => Path.Combine(Project, "00-程序树", "运维", "备份.yaml");
        public string JournalPath => Path.Combine(Project, "00-程序树", "运维", "备份.jsonl");
        public string CredentialsPath => Path.Combine(Home, "credentials.enc");

        public void WriteManifest(string treeVersion)
        {
            var manifest = "schema_version: 1\n" +
                "tree_version: " + treeVersion + "\n" +
                "updated: '2026-11-07'\n" +
                "root: a\n" +
                "phases:\n" +
                "  - {id: P0, name: 地基, goal: g, exit_criteria: [x]}\n" +
                "nodes:\n" +
                "  - id: a\n" +
                "    name: A\n" +
                "    kind: root\n" +
                "    parent: null\n" +
                "    phase: P0\n" +
                "    status: 未完成\n" +
                "    responsibility: 根节点职责\n" +
                "    not_responsible: []\n" +
                "    inputs: []\n" +
                "    outputs: []\n" +
                "    depends_on: []\n" +
                "    files: []\n" +
                "    acceptance: []\n" +
                "    spec: x\n";
            File.WriteAllText(Path.Combine(Project, "00-程序树", "program-tree.yaml"), manifest, new UTF8Encoding(false));
        }

        public string Head() => Git(Repo, "rev-parse", "HEAD").Trim();

        public int BundleFileCount()
        {
            var root = Path.Combine(Project, "00-程序树", "运维", "备份");
            return Directory.Exists(root) ? Directory.GetFiles(root, "*.bundle", SearchOption.AllDirectories).Length : 0;
        }

        public void Dispose()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                // git 的对象文件是只读的，Windows 上必须先清只读属性才能递归删临时目录
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(directory);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "git " + string.Join(" ", arguments) + " 失败：" + stderr);
        return stdout;
    }

    private static JsonElement Args(object payload) => JsonSerializer.SerializeToElement(payload);

    private static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static void Enable(Sandbox sandbox, int retention = 10)
    {
        var home = BackupHome.Resolve(sandbox.Home);
        var setup = Parse(BackupService.Setup(sandbox.Project, Args(new { username = "alice", token = Token }), home));
        Assert.True(setup.GetProperty("ok").GetBoolean(), setup.GetRawText());
        var configured = Parse(BackupService.Config(sandbox.Project,
            Args(new { add_targets = new[] { sandbox.Repo }, mode = "git-bundle", retention }), home));
        Assert.True(configured.GetProperty("ok").GetBoolean(), configured.GetRawText());
        var enabled = Parse(BackupService.Config(sandbox.Project, Args(new { enabled = true }), home));
        Assert.True(enabled.GetProperty("ok").GetBoolean(), enabled.GetRawText());
    }

    private static int CountJournal(Sandbox sandbox, string kind)
    {
        if (!File.Exists(sandbox.JournalPath)) return 0;
        var count = 0;
        foreach (var line in File.ReadLines(sandbox.JournalPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var k = document.RootElement.GetProperty("kind").GetString();
            if (string.Equals(k, kind, StringComparison.Ordinal)) count++;
        }
        return count;
    }

    // ------------------------------------------------------------------
    // 1. 默认关闭 + 未配置拒绝启用
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigDefaultsToDisabledAndRefusesEnableWithoutCredentials()
    {
        using var sandbox = new Sandbox();
        var home = BackupHome.Resolve(sandbox.Home);
        var status = Parse(BackupService.Status(sandbox.Project, null, home));
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("未配置", status.GetProperty("credentials").GetProperty("status").GetString());

        var add = Parse(BackupService.Config(sandbox.Project, Args(new { add_targets = new[] { sandbox.Repo } }), home));
        Assert.True(add.GetProperty("ok").GetBoolean());
        Assert.False(add.GetProperty("enabled").GetBoolean());

        var enable = Parse(BackupService.Config(sandbox.Project, Args(new { enabled = true }), home));
        Assert.False(enable.GetProperty("ok").GetBoolean());
        Assert.Contains("tree_backup_setup", enable.GetProperty("error").GetString());
        Assert.True(enable.GetProperty("need_setup").GetBoolean());

        Assert.False(BackupSettings.Load(sandbox.Project).Enabled);
        Assert.False(File.Exists(sandbox.CredentialsPath));
    }

    // ------------------------------------------------------------------
    // 2. setup：加密落盘、明文搜不到、status 脱敏
    // ------------------------------------------------------------------

    [Fact]
    public void SetupEncryptsCredentialsAndStatusMasksToken()
    {
        using var sandbox = new Sandbox();
        var home = BackupHome.Resolve(sandbox.Home);
        var setup = Parse(BackupService.Setup(sandbox.Project, Args(new { username = "alice", token = Token }), home));
        Assert.True(setup.GetProperty("ok").GetBoolean());
        Assert.True(File.Exists(sandbox.CredentialsPath));

        var cipherText = File.ReadAllText(sandbox.CredentialsPath, Encoding.UTF8);
        Assert.DoesNotContain(Token, cipherText);
        Assert.DoesNotContain("alice", cipherText);
        Assert.True(setup.GetProperty("key_created").GetBoolean());

        var status = Parse(BackupService.Status(sandbox.Project, null, home));
        Assert.True(status.GetProperty("credentials").GetProperty("configured").GetBoolean());
        Assert.Equal("已配置", status.GetProperty("credentials").GetProperty("status").GetString());
        Assert.Equal("alice", status.GetProperty("credentials").GetProperty("username").GetString());
        Assert.Equal(Token[..4] + "****" + Token[^2..], status.GetProperty("credentials").GetProperty("token_masked").GetString());
        Assert.DoesNotContain(Token, status.GetRawText());

        // token 传空 = 只改用户名，token 保留
        var renamed = Parse(BackupService.Setup(sandbox.Project, Args(new { username = "alice2", token = "" }), home));
        Assert.True(renamed.GetProperty("ok").GetBoolean());
        var after = Parse(BackupService.Status(sandbox.Project, null, home));
        Assert.Equal("alice2", after.GetProperty("credentials").GetProperty("username").GetString());
        Assert.Equal(Token[..4] + "****" + Token[^2..], after.GetProperty("credentials").GetProperty("token_masked").GetString());
        Assert.DoesNotContain(Token, File.ReadAllText(sandbox.CredentialsPath, Encoding.UTF8));
    }

    // ------------------------------------------------------------------
    // 3. backup now：bundle 产物 + jsonl 一行 + commit 与 rev-parse HEAD 一致
    // ------------------------------------------------------------------

    [Fact]
    public void BackupNowWritesBundleAndJournalWithHeadCommit()
    {
        using var sandbox = new Sandbox();
        var home = BackupHome.Resolve(sandbox.Home);
        Enable(sandbox);

        var now = Parse(BackupService.Now(sandbox.Project, null, home));
        Assert.True(now.GetProperty("ok").GetBoolean(), now.GetRawText());
        Assert.Equal("1.0.0", now.GetProperty("tree_version").GetString());
        Assert.Equal(1, now.GetProperty("points").GetInt32());
        var first = now.GetProperty("results")[0];
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.Equal("bundle", first.GetProperty("kind").GetString());
        Assert.Equal(sandbox.Head(), first.GetProperty("commit").GetString());
        Assert.True(first.GetProperty("bytes").GetInt64() > 0);

        var bundle = first.GetProperty("bundle").GetString()!;
        Assert.True(File.Exists(Path.Combine(sandbox.Project, bundle)), "bundle 不存在：" + bundle);

        var lines = File.ReadAllLines(sandbox.JournalPath, Encoding.UTF8).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        Assert.Single(lines);
        using var record = JsonDocument.Parse(lines[0]);
        Assert.Equal("1.0.0", record.RootElement.GetProperty("tree_version").GetString());
        Assert.Equal("bundle", record.RootElement.GetProperty("kind").GetString());
        Assert.Equal(sandbox.Repo, record.RootElement.GetProperty("target").GetString());
        Assert.Equal(sandbox.Head(), record.RootElement.GetProperty("commit").GetString());
        Assert.Equal(first.GetProperty("bytes").GetInt64(), record.RootElement.GetProperty("bytes").GetInt64());

        var status = Parse(BackupService.Status(sandbox.Project, null, home));
        Assert.Equal(1, status.GetProperty("backup_points").GetInt32());
        Assert.NotNull(status.GetProperty("last_run").GetString());
    }

    [Fact]
    public void BackupNowRefusesWhenSecretDetected()
    {
        using var sandbox = new Sandbox();
        var home = BackupHome.Resolve(sandbox.Home);
        Enable(sandbox);
        File.WriteAllText(Path.Combine(sandbox.Repo, "secret.txt"),
            "api_key = \"0123456789abcdef\"\n", new UTF8Encoding(false));

        var now = Parse(BackupService.Now(sandbox.Project, null, home));
        Assert.False(now.GetProperty("ok").GetBoolean(), now.GetRawText());
        Assert.True(now.GetProperty("findings").GetArrayLength() > 0);
        Assert.Contains("secret_scan", now.GetProperty("hint").GetString());
        Assert.Contains("拒绝生成备份", now.GetProperty("error").GetString());
        Assert.Equal(0, sandbox.BundleFileCount());
        Assert.Equal(0, CountJournal(sandbox, "bundle"));
    }

    // ------------------------------------------------------------------
    // 4. retention：造 12 个备份点，留 10，删 2，jsonl 有 trimmed 记录
    // ------------------------------------------------------------------

    [Fact]
    public void RetentionKeepsLatestTenAndAppendsTrimmedRecords()
    {
        using var sandbox = new Sandbox();
        var home = BackupHome.Resolve(sandbox.Home);
        Enable(sandbox, retention: 10);
        for (var index = 1; index <= 12; index++)
        {
            sandbox.WriteManifest("1.0." + index);
            var now = Parse(BackupService.Now(sandbox.Project, null, home));
            Assert.True(now.GetProperty("ok").GetBoolean(), now.GetRawText());
        }
        // 台账 append-only：12 行备份点仍在，另加 2 行 trimmed 墓碑；有效备份点应为 10
        Assert.Equal(12, CountJournal(sandbox, "bundle"));
        Assert.Equal(2, CountJournal(sandbox, "trimmed"));
        Assert.Equal(10, sandbox.BundleFileCount());
        var listed = Parse(BackupService.List(sandbox.Project, Args(new { limit = 50 }), home));
        Assert.Equal(10, listed.GetProperty("count").GetInt32());
        var status = Parse(BackupService.Status(sandbox.Project, null, home));
        Assert.Equal(10, status.GetProperty("backup_points").GetInt32());
        var all = Parse(BackupService.List(sandbox.Project, Args(new { limit = 50, include_trimmed = true }), home));
        Assert.Equal(14, all.GetProperty("count").GetInt32());
    }

    // ------------------------------------------------------------------
    // 5. verify：bundle 可校验；坏 bundle 报失败
    // ------------------------------------------------------------------

    [Fact]
    public void VerifyAcceptsGoodBundleAndRejectsBrokenOne()
    {
        using var sandbox = new Sandbox();
        var home = BackupHome.Resolve(sandbox.Home);
        Enable(sandbox);
        Assert.True(Parse(BackupService.Now(sandbox.Project, null, home)).GetProperty("ok").GetBoolean());

        var good = Parse(BackupService.Verify(sandbox.Project, null, home));
        Assert.True(good.GetProperty("ok").GetBoolean(), good.GetRawText());
        Assert.True(good.GetProperty("entries")[0].GetProperty("ok").GetBoolean());

        var bundle = Path.Combine(sandbox.Project, good.GetProperty("entries")[0].GetProperty("bundle").GetString()!);
        File.WriteAllText(bundle, "not a bundle", new UTF8Encoding(false));
        var broken = Parse(BackupService.Verify(sandbox.Project, null, home));
        Assert.False(broken.GetProperty("ok").GetBoolean());
        Assert.False(broken.GetProperty("entries")[0].GetProperty("ok").GetBoolean());
    }

    // ------------------------------------------------------------------
    // 6. PTreeBackupHome 环境变量生效
    // ------------------------------------------------------------------

    [Fact]
    public void EnvVarPTreeBackupHomeRedirectsGlobalDirectory()
    {
        using var sandbox = new Sandbox();
        var previous = Environment.GetEnvironmentVariable(BackupLayout.HomeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BackupLayout.HomeEnvVar, sandbox.Home);
            var resolved = BackupHome.Resolve();
            Assert.Equal(Path.GetFullPath(sandbox.Home), resolved.Directory);
            Assert.Equal("env:" + BackupLayout.HomeEnvVar, resolved.Source);
            var setup = Parse(BackupService.Setup(sandbox.Project, Args(new { username = "bob", token = Token })));
            Assert.True(setup.GetProperty("ok").GetBoolean(), setup.GetRawText());
            Assert.True(File.Exists(sandbox.CredentialsPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupLayout.HomeEnvVar, previous);
        }
    }

    // ------------------------------------------------------------------
    // 7. enabled=true 但未配置 → health 一条 W
    // ------------------------------------------------------------------

    [Fact]
    public void EnabledButUnconfiguredProducesHealthWarning()
    {
        using var sandbox = new Sandbox();
        var previous = Environment.GetEnvironmentVariable(BackupLayout.HomeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BackupLayout.HomeEnvVar, sandbox.Home);
            File.WriteAllText(sandbox.BackupConfigPath,
                "enabled: true\nmode: git-bundle\nretention: 10\ntargets:\n  - " + sandbox.Repo.Replace('\\', '/') + "\n",
                new UTF8Encoding(false));
            var repository = PTreeRepository.Load(sandbox.Project);
            var report = HealthCheck.Run(repository);
            var issue = Assert.Single(report.Issues.Where(item => item.Kind == "backup_unconfigured"));
            Assert.Equal("W", issue.Level);
            Assert.Contains("tree_backup_setup", issue.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupLayout.HomeEnvVar, previous);
        }
    }

    // ------------------------------------------------------------------
    // 8. R43：备份配置结构校验
    // ------------------------------------------------------------------

    [Fact]
    public void R43ReportsBadModeAndRetention()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.BackupConfigPath,
            "enabled: false\nmode: zip\nretention: 0\ntargets: []\n",
            new UTF8Encoding(false));
        var repository = PTreeRepository.Load(sandbox.Project);
        var validation = Validator.Validate(repository);
        Assert.Contains(validation.Errors, issue => issue.Code == "R43");
        Assert.Contains(validation.Errors, issue => issue.Code == "R43" && issue.Message.Contains("mode"));
        Assert.Contains(validation.Errors, issue => issue.Code == "R43" && issue.Message.Contains("retention"));
    }

    // ------------------------------------------------------------------
    // 9. 树改动钩子：AfterTreeChange 在启用时把 tree_version 映射成备份点
    // ------------------------------------------------------------------

    [Fact]
    public void AfterTreeChangeProducesBackupPointWhenEnabled()
    {
        using var sandbox = new Sandbox();
        var previous = Environment.GetEnvironmentVariable(BackupLayout.HomeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BackupLayout.HomeEnvVar, sandbox.Home);
            Assert.True(Parse(BackupService.Setup(sandbox.Project, Args(new { username = "alice", token = Token })))
                .GetProperty("ok").GetBoolean());
            Assert.True(Parse(BackupService.Config(sandbox.Project,
                    Args(new { add_targets = new[] { sandbox.Repo } })))
                .GetProperty("ok").GetBoolean());
            Assert.True(Parse(BackupService.Config(sandbox.Project, Args(new { enabled = true })))
                .GetProperty("ok").GetBoolean());

            var repository = PTreeRepository.Load(sandbox.Project);
            var payload = BackupService.AfterTreeChange(repository, "1.0.9");
            Assert.NotNull(payload);
            Assert.True((bool)payload!["ok"]!);
            Assert.Equal("1.0.9", payload["tree_version"]);
            foreach (var line in File.ReadLines(sandbox.JournalPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var record = JsonDocument.Parse(line);
                Assert.Equal("1.0.9", record.RootElement.GetProperty("tree_version").GetString());
                Assert.Equal(sandbox.Head(), record.RootElement.GetProperty("commit").GetString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackupLayout.HomeEnvVar, previous);
        }
    }

    [Fact]
    public void MaskTokenKeepsOnlyFirstFourAndLastTwo()
    {
        Assert.Equal("abcd****gh", BackupCrypto.MaskToken("abcdefgh"));
        Assert.Equal("****", BackupCrypto.MaskToken("abc"));
        Assert.Equal("", BackupCrypto.MaskToken(null));
    }
}
