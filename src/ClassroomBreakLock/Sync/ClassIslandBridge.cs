using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassroomBreakLock.Logging;

namespace ClassroomBreakLock.Sync;

/// <summary>ClassIsland 推过来的一次事件。</summary>
public sealed class ClassIslandEvent
{
    /// <summary>true = 上课，false = 下课。</summary>
    public bool IsClassStart { get; set; }

    /// <summary>科目名（ClassIsland 的 自动化 可以把课程名带过来）。</summary>
    public string Subject { get; set; } = "";

    /// <summary>原始请求内容，便于排查。</summary>
    public string Raw { get; set; } = "";

    public string Describe() => IsClassStart
        ? $"上课{(string.IsNullOrWhiteSpace(Subject) ? "" : "（" + Subject + "）")}"
        : "下课";
}

/// <summary>
/// ClassIsland 状态同步接收端。
///
/// 为什么要走本地 HTTP 而不是直接写 ClassIsland 插件：
///   ClassIsland 的插件要引用它自己的 ClassIsland.Core 程序集，而那套东西的版本、
///   签名和运行时要跟着它一起走；一旦它升级，插件就得重编。反过来，让课间锁开一个
///   本地端口、ClassIsland 用它自带的「自动化 → 运行程序」推事件过来，互不绑定版本，
///   坏了也好查（浏览器直接 GET 一下 /state 就知道通没通）。
///
/// 接口（默认 8731，仅监听 localhost，不暴露到局域网）：
///   GET  /state          查看当前状态与最近一次事件
///   POST /class/start    上课（可用 ?subject=数学 带上科目）
///   POST /class/end      下课
///   GET  /class/start    同上（方便直接用浏览器或 curl 试）
/// 需要在设置里填了令牌时，请带上 ?token=xxx 或 X-Auth-Token 头。
/// </summary>
public sealed class ClassIslandBridge : IDisposable
{
    private readonly int _port;
    private readonly string _token;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ClassIslandBridge(int port, string token)
    {
        _port = port;
        _token = token ?? "";
    }

    /// <summary>收到上课/下课事件时触发。</summary>
    public event Action<ClassIslandEvent>? EventReceived;

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public DateTime? LastEventAt { get; private set; }

    public string LastEventText { get; private set; } = "";

    public int EventCount { get; private set; }

    /// <summary>实际监听的前缀，便于界面显示/排查。</summary>
    public string Prefix { get; private set; } = "";

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        // 只绑 localhost：不暴露局域网，也不需要管理员改防火墙。
        // 两种写法都加上——只用 "localhost" 时，客户端若发往 127.0.0.1 可能连不上。
        string[] candidates =
        {
            $"http://localhost:{_port}/",
            $"http://127.0.0.1:{_port}/"
        };

        // 先试「一个监听器挂两个前缀」（最稳）
        try
        {
            var listener = new HttpListener();
            foreach (string prefix in candidates)
            {
                listener.Prefixes.Add(prefix);
            }

            listener.Start();
            _listener = listener;
            Prefix = string.Join(" 、 ", candidates);
            IsRunning = true;
            LastError = null;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cts.Token));
            Log.Info($"ClassIsland 同步已监听：{Prefix}");
            return;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn($"ClassIsland 同步多前缀监听失败，改用单前缀重试：{ex.Message}");
        }

        // 退回逐个前缀尝试
        foreach (string prefix in candidates)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();

                _listener = listener;
                Prefix = prefix;
                IsRunning = true;
                LastError = null;

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => LoopAsync(_cts.Token));

                Log.Info($"ClassIsland 同步已监听：{prefix}");
                return;
            }
            catch (Exception ex)
            {
                LastError = $"{prefix} → {ex.Message}";
                Log.Warn($"ClassIsland 同步监听失败：{LastError}");
            }
        }

        IsRunning = false;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // 停的时候出错无所谓
        }
        finally
        {
            _listener = null;
            _cts = null;
            IsRunning = false;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;   // 监听被停掉了
            }

            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                Log.Warn($"处理 ClassIsland 事件时出错：{ex.Message}");
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/').ToLowerInvariant() ?? "";
        string query = ctx.Request.Url?.Query ?? "";
        string subject = ctx.Request.QueryString["subject"]
                         ?? ctx.Request.QueryString["name"]
                         ?? "";

        bool authorized = string.IsNullOrEmpty(_token)
                          || string.Equals(ctx.Request.QueryString["token"], _token, StringComparison.Ordinal)
                          || string.Equals(ctx.Request.Headers["X-Auth-Token"], _token, StringComparison.Ordinal);

        if (!authorized)
        {
            Write(ctx, 401, new { ok = false, error = "token 不正确" });
            return;
        }

        // 支持把参数放在 body 里：{"action":"start","subject":"数学"}
        string body = ReadBody(ctx);

        switch (path)
        {
            case "/state":
                Write(ctx, 200, new
                {
                    ok = true,
                    running = IsRunning,
                    eventCount = EventCount,
                    lastEventAt = LastEventAt,
                    lastEvent = LastEventText,
                    prefix = Prefix
                });
                return;

            case "/class/start":
            case "/class/begin":
            case "/start":
                Raise(true, subject, query + " " + body);
                Write(ctx, 200, new { ok = true, action = "start", subject });
                return;

            case "/class/end":
            case "/class/finish":
            case "/end":
                Raise(false, subject, query + " " + body);
                Write(ctx, 200, new { ok = true, action = "end", subject });
                return;

            default:
                Write(ctx, 404, new
                {
                    ok = false,
                    error = "未知路径",
                    tryThese = new[] { "/state", "/class/start", "/class/end" }
                });
                return;
        }
    }

    private void Raise(bool isStart, string subject, string raw)
    {
        var evt = new ClassIslandEvent { IsClassStart = isStart, Subject = subject, Raw = raw };
        EventCount++;
        LastEventAt = DateTime.Now;
        LastEventText = evt.Describe();

        Log.Info($"收到 ClassIsland 事件：{evt.Describe()}");
        EventReceived?.Invoke(evt);
    }

    private static string ReadBody(HttpListenerContext ctx)
    {
        try
        {
            if (!ctx.Request.HasEntityBody)
            {
                return "";
            }

            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            string text = reader.ReadToEnd();
            return text.Length > 4000 ? text.Substring(0, 4000) : text;
        }
        catch
        {
            return "";
        }
    }

    private static void Write(HttpListenerContext ctx, int status, object payload)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // 写不回去就算了
        }
        finally
        {
            try
            {
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
