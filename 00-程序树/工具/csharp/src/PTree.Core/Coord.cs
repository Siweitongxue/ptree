using System.Text.Json;

namespace PTree.Core;

/// <summary>多智能体协作：文件占用锁 + 留言信箱。只依赖本地协作目录，不碰树清单。</summary>
public static class Coord
{
    private const string LockFileName = "locks.json";
    private const string MailFileName = "mail.jsonl";
    private const string GuardFileName = ".coord.lock";
    private const int DefaultTtlMinutes = 30;
    private const int MaxTextLength = 500;

    public static string Dir(string root) => Path.Combine(root, "00-程序树", "协作");

    public static string Claim(string root, JsonElement? args)
    {
        var path = Normalize(StringOf(args, "path"));
        if (path.Length == 0) return Error("claim 需要 path（要改的文件路径）");
        var agent = StringOf(args, "agent");
        if (agent.Length == 0) return Error("claim 需要 agent（占用者名字）");
        var note = StringOf(args, "note");
        var ttl = IntOf(args, "ttl_minutes") ?? DefaultTtlMinutes;
        if (ttl < 1 || ttl > 1440) ttl = DefaultTtlMinutes;

        return WithLock(root, () =>
        {
            var now = DateTimeOffset.Now;
            var locks = LoadLocks(root, now);
            var existing = locks.FirstOrDefault(x => x.Path == path);
            if (existing is not null && existing.ExpiresAt > now && !string.Equals(existing.By, agent, StringComparison.OrdinalIgnoreCase))
            {
                return Err("文件被占用", new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["holder"] = new Dictionary<string, object?>
                    {
                        ["by"] = existing.By,
                        ["at"] = existing.At.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["expires_at"] = existing.ExpiresAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["remaining_seconds"] = (int)Math.Max(0, (existing.ExpiresAt - now).TotalSeconds),
                        ["note"] = existing.Note,
                    },
                });
            }

            var renewed = existing is not null;
            locks.RemoveAll(x => x.Path == path);
            var entry = new LockEntry
            {
                Path = path,
                By = agent,
                At = now,
                ExpiresAt = now.AddMinutes(ttl),
                Note = note,
            };
            locks.Add(entry);
            SaveLocks(root, locks);

            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["path"] = path,
                ["by"] = agent,
                ["renewed"] = renewed,
                ["at"] = entry.At.ToString("yyyy-MM-dd HH:mm:ss"),
                ["expires_at"] = entry.ExpiresAt.ToString("yyyy-MM-dd HH:mm:ss"),
                ["ttl_minutes"] = ttl,
                ["hint"] = "改完立刻执行 coord release " + path,
            });
        });
    }

    public static string Release(string root, JsonElement? args)
    {
        var path = Normalize(StringOf(args, "path"));
        if (path.Length == 0) return Error("release 需要 path");
        var agent = StringOf(args, "agent");
        var force = BoolOf(args, "force");

        return WithLock(root, () =>
        {
            var now = DateTimeOffset.Now;
            var locks = LoadLocks(root, now);
            var existing = locks.FirstOrDefault(x => x.Path == path);
            if (existing is null)
            {
                return JsonOutput.Serialize(new Dictionary<string, object?> { ["ok"] = true, ["path"] = path, ["released"] = false, ["note"] = "没有该占用" });
            }
            if (!force && agent.Length > 0 && !string.Equals(existing.By, agent, StringComparison.OrdinalIgnoreCase))
            {
                return Err("占用者不是 " + agent + "，要强拆加 force", new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["holder_by"] = existing.By,
                });
            }
            locks.RemoveAll(x => x.Path == path);
            SaveLocks(root, locks);
            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["path"] = path,
                ["released"] = true,
                ["freed_by"] = agent.Length > 0 ? agent : "force",
                ["remaining"] = locks.Count,
            });
        });
    }

    public static string List(string root)
    {
        var now = DateTimeOffset.Now;
        var locks = LoadLocks(root, now);
        var rows = locks
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => new Dictionary<string, object?>
            {
                ["path"] = x.Path,
                ["by"] = x.By,
                ["at"] = x.At.ToString("yyyy-MM-dd HH:mm:ss"),
                ["expires_at"] = x.ExpiresAt.ToString("yyyy-MM-dd HH:mm:ss"),
                ["expired"] = x.ExpiresAt <= now,
                ["remaining_seconds"] = (int)Math.Max(0, (x.ExpiresAt - now).TotalSeconds),
                ["note"] = x.Note,
            })
            .ToList();
        return JsonOutput.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["now"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["count"] = rows.Count,
            ["locks"] = rows,
        });
    }

    public static string Sweep(string root)
    {
        return WithLock(root, () =>
        {
            var now = DateTimeOffset.Now;
            var locks = LoadLocks(root, now);
            var before = locks.Count;
            locks.RemoveAll(x => x.ExpiresAt <= now);
            SaveLocks(root, locks);
            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["removed"] = before - locks.Count,
                ["remaining"] = locks.Count,
            });
        });
    }

    public static string Say(string root, JsonElement? args)
    {
        var to = StringOf(args, "to");
        var from = StringOf(args, "from");
        var text = StringOf(args, "text").Trim();
        if (to.Length == 0) return Error("say 需要 to（收件人）");
        if (text.Length == 0) return Error("say 需要 text（一句话）");
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];

        return WithLock(root, () =>
        {
            var now = DateTimeOffset.Now;
            var id = "msg-" + now.ToString("yyyyMMddHHmmssfff");
            var line = JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["at"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["from"] = from.Length > 0 ? from : "unknown",
                ["to"] = to,
                ["text"] = text,
                ["read_at"] = null,
            }).Replace("\r\n", " ").Replace("\n", " ");
            var file = Path.Combine(Dir(root), MailFileName);
            Directory.CreateDirectory(Dir(root));
            File.AppendAllText(file, line + "\n");
            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["id"] = id,
                ["to"] = to,
                ["at"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        });
    }

    public static string Inbox(string root, JsonElement? args)
    {
        var agent = StringOf(args, "agent");
        if (agent.Length == 0) return Error("inbox 需要 agent（收件人）");
        var keep = BoolOf(args, "keep");

        return WithLock(root, () =>
        {
            var now = DateTimeOffset.Now;
            var file = Path.Combine(Dir(root), MailFileName);
            var messages = new List<Dictionary<string, object?>>();
            if (File.Exists(file))
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.Trim().Length == 0) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch (JsonException) { continue; }
                    using (doc)
                    {
                        var to = doc.RootElement.TryGetProperty("to", out var toEl) ? toEl.GetString() ?? "" : "";
                        if (!string.Equals(to, agent, StringComparison.OrdinalIgnoreCase)) continue;
                        var alreadyRead = doc.RootElement.TryGetProperty("read_at", out var readEl) && readEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(readEl.GetString());
                        if (alreadyRead) continue;
                        messages.Add(new Dictionary<string, object?>
                        {
                            ["id"] = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                            ["at"] = doc.RootElement.TryGetProperty("at", out var atEl) ? atEl.GetString() : null,
                            ["from"] = doc.RootElement.TryGetProperty("from", out var fromEl) ? fromEl.GetString() : null,
                            ["text"] = doc.RootElement.TryGetProperty("text", out var textEl) ? textEl.GetString() : null,
                        });
                    }
                }
            }

            if (!keep && messages.Count > 0)
            {
                MarkRead(file, agent, now);
            }

            return JsonOutput.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["agent"] = agent,
                ["count"] = messages.Count,
                ["marked_read"] = !keep && messages.Count > 0,
                ["messages"] = messages,
            });
        });
    }

    private sealed class LockEntry
    {
        public string Path { get; set; } = "";
        public string By { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string Note { get; set; } = "";
    }

    private static List<LockEntry> LoadLocks(string root, DateTimeOffset now)
    {
        var file = System.IO.Path.Combine(Dir(root), LockFileName);
        var list = new List<LockEntry>();
        if (!File.Exists(file)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("locks", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray())
            {
                list.Add(new LockEntry
                {
                    Path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    By = item.TryGetProperty("by", out var b) ? b.GetString() ?? "" : "",
                    At = item.TryGetProperty("at", out var a) && DateTimeOffset.TryParse(a.GetString(), out var at) ? at : now,
                    ExpiresAt = item.TryGetProperty("expires_at", out var e) && DateTimeOffset.TryParse(e.GetString(), out var exp) ? exp : now,
                    Note = item.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "",
                });
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException) { }
        return list;
    }

    private static void SaveLocks(string root, List<LockEntry> locks)
    {
        Directory.CreateDirectory(Dir(root));
        var payload = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["locks"] = locks.Select(x => new Dictionary<string, object?>
            {
                ["path"] = x.Path,
                ["by"] = x.By,
                ["at"] = x.At.ToString("yyyy-MM-dd HH:mm:ss"),
                ["expires_at"] = x.ExpiresAt.ToString("yyyy-MM-dd HH:mm:ss"),
                ["note"] = x.Note,
            }).ToList(),
        };
        var file = System.IO.Path.Combine(Dir(root), LockFileName);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonOutput.Serialize(payload));
        File.Move(temp, file, overwrite: true);
    }

    private static void MarkRead(string file, string agent, DateTimeOffset now)
    {
        var lines = File.ReadAllLines(file);
        var output = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var to = doc.RootElement.TryGetProperty("to", out var toEl) ? toEl.GetString() ?? "" : "";
                var readAt = doc.RootElement.TryGetProperty("read_at", out var r) ? r.GetString() : null;
                if (string.Equals(to, agent, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(readAt))
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in doc.RootElement.EnumerateObject()) dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
                    dict["read_at"] = now.ToString("yyyy-MM-dd HH:mm:ss");
                    output.Add(JsonOutput.Serialize(dict).Replace("\r\n", " ").Replace("\n", " "));
                }
                else
                {
                    output.Add(line);
                }
            }
            catch (JsonException)
            {
                output.Add(line);
            }
        }
        File.WriteAllLines(file, output);
    }

    private static string WithLock(string root, Func<string> action)
    {
        Directory.CreateDirectory(Dir(root));
        var guard = System.IO.Path.Combine(Dir(root), GuardFileName);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var stream = new FileStream(guard, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return action();
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
        return Error("协作目录被其他进程占用，请稍后重试");
    }


    /// <summary>写盘前的守门检查：返回 null 表示可以写，否则返回一句人能看懂的原因。</summary>
    public static string? Guard(string root, IEnumerable<string> relPaths, string agent)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PTREE_COORD"), "off", StringComparison.OrdinalIgnoreCase)) return null;
        // 只管 AI：没带身份的调用（人、脚本、单智能体）一律放行，不挡正常干活
        if (string.IsNullOrWhiteSpace(agent)) return null;
        var policy = LoadPolicy(root);
        if (!policy.Enforce) return null;

        var now = DateTimeOffset.Now;
        var locks = LoadLocks(root, now);
        var blocked = new List<string>();
        var unclaimed = new List<string>();
        foreach (var raw in relPaths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            var path = Normalize(raw);
            if (path.Length == 0) continue;
            var entry = locks.FirstOrDefault(x => x.Path == path);
            if (entry is not null && entry.ExpiresAt > now)
            {
                // 自己持有就是正常路径：改完记得 release
                if (agent.Length > 0 && string.Equals(entry.By, agent, StringComparison.OrdinalIgnoreCase)) continue;
                blocked.Add(path + " 正被 " + entry.By + " 占用（到 " + entry.ExpiresAt.ToString("yyyy-MM-dd HH:mm") + "），先跟它协调或等它释放");
                continue;
            }
            if (policy.RequireClaim && agent.Length > 0) unclaimed.Add(path);
        }

        if (blocked.Count == 0 && unclaimed.Count == 0) return null;
        var parts = new List<string>(blocked);
        parts.AddRange(unclaimed.Select(p => p + " 未登记占用，先执行 coord claim " + p + " --by " + agent));
        return string.Join("；", parts);
    }

    /// <summary>从工具参数里取调用者身份：agent 字段优先，其次环境变量 PTREE_AGENT。</summary>
    public static string AgentOf(JsonElement? args)
    {
        var agent = StringOf(args, "agent").Trim();
        if (agent.Length > 0) return agent;
        return (Environment.GetEnvironmentVariable("PTREE_AGENT") ?? "").Trim();
    }

    private sealed class Policy
    {
        public bool Enforce { get; set; } = true;
        public bool RequireClaim { get; set; }
    }

    private static Policy LoadPolicy(string root)
    {
        var policy = new Policy();
        var file = System.IO.Path.Combine(root, "00-程序树", "协作策略.yaml");
        if (!File.Exists(file)) return policy;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().ToLowerInvariant();
            var flag = value is "true" or "yes" or "on" or "1";
            if (key == "enforce") policy.Enforce = flag;
            else if (key == "require_claim") policy.RequireClaim = flag;
        }
        return policy;
    }

    private static string StringOf(JsonElement? args, string name)
    {
        if (args is null || args.Value.ValueKind != JsonValueKind.Object) return "";
        if (!args.Value.TryGetProperty(name, out var property)) return "";
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    }

    private static int? IntOf(JsonElement? args, string name)
    {
        if (args is null || args.Value.ValueKind != JsonValueKind.Object) return null;
        if (!args.Value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool BoolOf(JsonElement? args, string name)
    {
        if (args is null || args.Value.ValueKind != JsonValueKind.Object) return false;
        if (!args.Value.TryGetProperty(name, out var property)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private static string Normalize(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value;
    }

    private static string Error(string message) => Err(message, null);

    private static string Err(string message, Dictionary<string, object?>? extra)
    {
        var payload = new Dictionary<string, object?> { ["ok"] = false, ["error"] = message };
        if (extra is not null)
        {
            foreach (var pair in extra) payload[pair.Key] = pair.Value;
        }
        return JsonOutput.Serialize(payload);
    }
}
