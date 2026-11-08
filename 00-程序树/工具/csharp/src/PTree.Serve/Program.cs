using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PTree.Core;

namespace PTree.Serve;

/// <summary>
/// 本地 HTTP 服务：给可视化界面提供数据与写操作，AI 与人共用同一套内核。
/// 只用 BCL，不引 NuGet；不依赖 URL ACL（TcpListener 手写 HTTP/1.1）。
/// 路由：
///   GET  /                     静态：viewer/index.html
///   GET  /{file}               静态：viewer 目录下的文件（只允许白名单后缀，禁止路径穿越）
///   GET  /api/health           服务与根目录信息
///   GET  /api/status?format=full|card  tree_status（默认 full，给 viewer 的 JSON 兼容）
///   GET  /api/check            tree_check
///   GET  /api/node/{id}?depth=2&format=full  tree_read_node（默认 full，给 viewer 的 JSON 兼容）
///   GET  /api/find?q=...       tree_find
///   POST /api/node             tree_add_node（body 即工具参数）
///   POST /api/link             tree_add_link
///   POST /api/node/update      tree_update_node
///   POST /api/node/retire      tree_retire_node
/// </summary>
public static class Program
{
    private static string _root = Directory.GetCurrentDirectory();
    private static string _viewer = string.Empty;
    private static int _port = 7777;
    private static readonly string[] StaticAllowed = { ".html", ".js", ".css", ".json", ".txt", ".png", ".jpg", ".svg", ".woff2" };

    public static int Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Length: _root = Path.GetFullPath(args[++i]); break;
                case "--port" when i + 1 < args.Length: _port = int.Parse(args[++i]); break;
                case "--viewer" when i + 1 < args.Length: _viewer = Path.GetFullPath(args[++i]); break;
                case "--help":
                case "-h":
                    Console.WriteLine("用法：ptree-serve [--root <项目根>] [--port 7777] [--viewer <视图目录>]");
                    return 0;
            }
        }
        _viewer = string.IsNullOrEmpty(_viewer) ? Path.Combine(_root, "00-程序树", "工具", "viewer") : _viewer;
        if (!Directory.Exists(_viewer))
        {
            Console.Error.WriteLine("视图目录不存在：" + _viewer);
            return 2;
        }
        var listener = new TcpListener(IPAddress.Loopback, _port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine("端口被占用或不可用：" + _port + " — " + exception.Message);
            return 3;
        }
        Console.WriteLine("ptree-serve 监听 http://127.0.0.1:" + _port + "/");
        Console.WriteLine("项目根：" + _root);
        Console.WriteLine("视图目录：" + _viewer);
        while (true)
        {
            var client = listener.AcceptTcpClient();
            _ = Task.Run(() => Handle(client));
        }
    }

    private static void Handle(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            stream.ReadTimeout = 15000;
            var buffer = new List<byte>(8192);
            var chunk = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var got = stream.Read(chunk, 0, chunk.Length);
                if (got <= 0) return;
                buffer.AddRange(chunk.Take(got));
                headerEnd = IndexOf(buffer, "\r\n\r\n");
                if (buffer.Count > 1024 * 1024) return;
            }
            var headerBytes = buffer.Take(headerEnd).ToArray();
            var headText = Encoding.UTF8.GetString(headerBytes);
            var lines = headText.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0].ToUpperInvariant();
            var target = parts[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            var contentLength = headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var parsedLength)
                ? Math.Max(0, parsedLength)
                : 0;
            var bodyStart = headerEnd + 4;
            while (buffer.Count - bodyStart < contentLength)
            {
                var got = stream.Read(chunk, 0, chunk.Length);
                if (got <= 0) break;
                buffer.AddRange(chunk.Take(got));
            }
            var bodyBytes = buffer.Skip(bodyStart).Take(Math.Max(0, Math.Min(contentLength, buffer.Count - bodyStart))).ToArray();
            var body = Encoding.UTF8.GetString(bodyBytes);
            var (status, contentType, payload) = Route(method, target, body, headers);
            var head = "HTTP/1.1 " + status + " " + Reason(status) + "\r\n"
                + "Content-Type: " + contentType + "; charset=utf-8\r\n"
                + "Content-Length: " + payload.Length + "\r\n"
                + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            var headBytes = Encoding.UTF8.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("请求失败：" + exception.Message);
        }
        finally
        {
            client.Close();
        }
    }

    private static int IndexOf(List<byte> buffer, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        for (var i = 0; i + bytes.Length <= buffer.Count; i++)
        {
            var hit = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                if (buffer[i + j] != bytes[j]) { hit = false; break; }
            }
            if (hit) return i;
        }
        return -1;
    }

    private static (int, string, byte[]) Route(string method, string target, string body, Dictionary<string, string> headers)
    {
        var path = target;
        var query = string.Empty;
        var mark = target.IndexOf('?');
        if (mark >= 0)
        {
            path = target[..mark];
            query = target[(mark + 1)..];
        }
        path = Uri.UnescapeDataString(path);
        try
        {
            if (method == "POST" && !SameOrigin(headers))
            {
                return Text(403, "{\"ok\":false,\"error\":\"拒绝跨站写操作：请求来源不可信\"}");
            }
            if (method == "GET" && path == "/api/health")
            {
                return Json(200, new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["root"] = _root,
                    ["viewer"] = _viewer,
                    ["kernel"] = KernelVersion(),
                });
            }
            if (method == "GET" && path == "/api/status")
                return Text(200, NodeTools.Status(_root, Args(new { format = QueryValue(query, "format") ?? "full" })));
            if (method == "GET" && path == "/api/check") return Text(200, NodeTools.Check(_root));
            if (method == "GET" && path.StartsWith("/api/node/", StringComparison.Ordinal))
            {
                var id = path["/api/node/".Length..];
                var depth = QueryInt(query, "depth", 1);
                var format = QueryValue(query, "format") ?? "full";
                return Text(200, NodeTools.ReadNode(_root, Args(new { id, depth, format })));
            }
            if (method == "GET" && path == "/api/find")
            {
                var q = QueryValue(query, "q") ?? string.Empty;
                return Text(200, NodeTools.Find(_root, Args(new { query = q })));
            }
            if (method == "POST" && path == "/api/node") return Text(200, NodeTools.AddNode(_root, ArgsFromBody(body)));
            if (method == "POST" && path == "/api/link") return Text(200, NodeTools.AddLink(_root, ArgsFromBody(body)));
            if (method == "POST" && path == "/api/node/update") return Text(200, NodeTools.UpdateNode(_root, ArgsFromBody(body)));
            if (method == "POST" && path == "/api/node/retire") return Text(200, NodeTools.RetireNode(_root, ArgsFromBody(body)));
            if (method == "GET" || method == "HEAD")
            {
                var file = path == "/" ? "index.html" : path.TrimStart('/');
                var full = Path.GetFullPath(Path.Combine(_viewer, file));
                if (!IsUnderViewer(full) || !File.Exists(full))
                {
                    return Text(404, "{\"ok\":false,\"error\":\"文件不存在\"}");
                }
                if (Array.IndexOf(StaticAllowed, Path.GetExtension(full).ToLowerInvariant()) < 0)
                {
                    return Text(403, "{\"ok\":false,\"error\":\"后缀不允许\"}");
                }
                return (200, Mime(full), File.ReadAllBytes(full));
            }
            return Text(404, "{\"ok\":false,\"error\":\"路由不存在\"}");
        }
        catch (PTreeException exception)
        {
            return Text(400, JsonSerializer.Serialize(new { ok = false, error = exception.Message },
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception exception)
        {
            return Text(500, JsonSerializer.Serialize(new { ok = false, error = exception.Message },
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }

    /// <summary>静态文件必须在 viewer 目录内：按分隔符边界判断，堵住 /../viewer-x/ 这类同前缀兄弟目录。</summary>
    private static bool IsUnderViewer(string full)
    {
        var prefix = _viewer.EndsWith(Path.DirectorySeparatorChar) ? _viewer : _viewer + Path.DirectorySeparatorChar;
        return string.Equals(full, _viewer, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>写接口只接受本机服务同源请求：浏览器跨站 POST 必带 Origin，非本机来源一律拒绝；命令行不带 Origin 时放行。</summary>
    private static bool SameOrigin(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Origin", out var origin) || string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
        var loopback = parsed.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
        return loopback && parsed.Port == _port;
    }

    private static JsonElement Args(object value)
    {
        var json = JsonSerializer.Serialize(value);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement ArgsFromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Args(new { });
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static int QueryInt(string query, string key, int fallback)
    {
        var value = QueryValue(query, key);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            if (split.Length == 2 && split[0] == key) return Uri.UnescapeDataString(split[1]);
        }
        return null;
    }

    private static string KernelVersion()
    {
        try
        {
            return typeof(NodeTools).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".js" => "application/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        _ => "text/plain",
    };

    private static (int, string, byte[]) Text(int status, string text) => (status, "application/json", Encoding.UTF8.GetBytes(text));

    private static (int, string, byte[]) Json(int status, object value) => Text(status, JsonSerializer.Serialize(value,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        _ => "Internal Server Error",
    };
}
