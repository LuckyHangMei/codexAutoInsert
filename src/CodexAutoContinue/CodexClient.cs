using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexAutoContinue;

internal sealed class CodexClient : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly string? _socketPath;
    private ClientWebSocket? _socket;
    private HttpMessageInvoker? _http;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _readerTask;
    private Task? _errorTask;
    private Task? _stopTask;
    private int _nextId;
    private volatile bool _stopping;
    private volatile bool _connectionLost;
    private bool _started;

    public CodexClient(ProcessStartInfo startInfo) => _process = new() { StartInfo = startInfo };
    internal CodexClient(string socketPath) => _socketPath = Path.GetFullPath(socketPath);
    public bool IsShared => _socketPath is not null;

    public static CodexClient Shared() => new(Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "app-server-control", "app-server-control.sock"));

    public event Action<JsonElement>? Message;
    public event Action<string>? Diagnostic;
    public event Action<string>? Disconnected;
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<string> StartAsync(string workingDirectory)
    {
        if (IsShared) throw new InvalidOperationException("共享连接不能创建新会话。");
        await ConnectAsync().ConfigureAwait(false);
        var result = await RequestAsync("thread/start", new { cwd = workingDirectory }).ConfigureAwait(false);
        return result.Part("thread").Text("id") ?? throw new InvalidDataException("Codex 未返回会话标识。");
    }

    public async Task ConnectAsync()
    {
        if (_started) throw new InvalidOperationException("连接已启动。");
        if (_process is not null)
        {
            if (!_process.Start()) throw new IOException("无法启动 Codex。");
            _started = true;
            _errorTask = ReadErrorsAsync();
        }
        else
        {
            _started = true;
            // The native daemon creates this per-user private socket directory. No TCP listener or proxy process.
            _http = new HttpMessageInvoker(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath!), token).ConfigureAwait(false); }
                    catch { socket.Dispose(); throw; }
                    return new NetworkStream(socket, ownsSocket: true);
                }
            });
            _socket = new ClientWebSocket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { await _socket.ConnectAsync(new Uri("ws://localhost/rpc"), _http, timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !_stopping)
            {
                throw new IOException("无法连接 Codex 共享后台。请先启动共享后台，并将目标会话接入；不会自动重启或接管内嵌会话。", error);
            }
        }
        _readerTask = ReadAsync();
        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "codex_auto_continue", title = "Codex 自动续发", version = "1.1.2" },
            capabilities = new { experimentalApi = true, requestAttestation = false }
        }).ConfigureAwait(false);
        await WriteAsync(new { method = "initialized", @params = new { } }).ConfigureAwait(false);
    }

    public async Task<List<CodexSession>> ListSessionsAsync()
    {
        var sessions = new List<CodexSession>();
        string? cursor = null;
        do
        {
            var page = await RequestAsync("thread/loaded/list", new { cursor, limit = 100 }).ConfigureAwait(false);
            foreach (var id in page.Part("data").EnumerateArray())
            {
                try
                {
                    var thread = (await RequestAsync("thread/read", new { threadId = id.GetString(), includeTurns = false }).ConfigureAwait(false)).Part("thread");
                    if (thread.Part("canAcceptDirectInput").ValueKind != JsonValueKind.True
                        || thread.Part("parentThreadId").ValueKind == JsonValueKind.String
                        || thread.Part("status").Text("type") == "notLoaded") continue;
                    sessions.Add(new(thread.Text("id")!, thread.Text("name") ?? thread.Text("preview") ?? "未命名会话", thread.Text("cwd")!));
                }
                catch (IOException error) { Diagnostic?.Invoke("会话列表更新：" + error.Message); }
            }
            cursor = page.Text("nextCursor");
        } while (cursor is not null);
        return sessions;
    }

    public async Task<JsonElement> AttachAsync(string threadId)
    {
        var thread = (await RequestAsync("thread/read", new { threadId, includeTurns = false }).ConfigureAwait(false)).Part("thread");
        if (thread.Part("status").Text("type") == "notLoaded" || thread.Part("canAcceptDirectInput").ValueKind != JsonValueKind.True)
            throw new IOException("目标会话不在共享后台运行，请刷新列表。不会从历史记录另起会话。");
        return await RequestAsync("thread/resume", new { threadId, excludeTurns = true,
            initialTurnsPage = new { limit = 1, sortDirection = "desc", itemsView = "summary" } }).ConfigureAwait(false);
    }

    public async Task<string> SendAsync(string threadId, string text)
    {
        if (IsShared)
        {
            var thread = (await RequestAsync("thread/read", new { threadId, includeTurns = false }).ConfigureAwait(false)).Part("thread");
            if (thread.Part("status").Text("type") is not ("idle" or "systemError")
                || thread.Part("canAcceptDirectInput").ValueKind != JsonValueKind.True)
                throw new IOException("目标会话正在执行、等待审批或已卸载，本次未发送。");
            // ponytail: bound sessions use this window for all manual input. turn/start may steer an active turn;
            // the read above is a final guard, not an atomic lock against input from another client.
            // Multi-writer support requires a server-side compare-and-start API.
        }
        var result = await RequestAsync("turn/start", new { threadId, input = new[] { new { type = "text", text } } }).ConfigureAwait(false);
        return result.Part("turn").Text("id") ?? throw new InvalidDataException("Codex 未确认消息所属回合。");
    }

    public Task<JsonElement> InterruptAsync(string threadId, string turnId) =>
        RequestAsync("turn/interrupt", new { threadId, turnId });

    public Task RespondAsync(JsonElement id, object result) => WriteAsync(new { id, result });
    public Task RejectUnsupportedAsync(JsonElement id) =>
        WriteAsync(new { id, error = new { code = -32601, message = "This desktop client does not support this request." } });

    internal async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        if (_stopping || _connectionLost) throw new IOException("Codex 连接已关闭。");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters }).ConfigureAwait(false);
            return await completion.Task.WaitAsync(RequestTimeout, _lifetime.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Codex 未及时确认请求；无法确定是否已执行，请停止或结束会话后再操作。");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object message)
    {
        var token = _lifetime.Token;
        await _writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_socket is not null)
                await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            else
            {
                await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            }
        }
        finally { _writer.Release(); }
    }

    private async Task ReadAsync()
    {
        string reason = "Codex 连接已关闭。";
        try
        {
            while (await ReadMessageAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.Part("id") is { ValueKind: JsonValueKind.Number } id
                    && message.Text("method") is null && id.TryGetInt32(out var number)
                    && _pending.TryGetValue(number, out var completion))
                {
                    if (message.Part("error").ValueKind == JsonValueKind.Object)
                        completion.TrySetException(new IOException(message.Part("error").Text("message") ?? "Codex 拒绝了请求。"));
                    else completion.TrySetResult(message.Part("result").Clone());
                }
                else if (message.Text("method") is not null) Message?.Invoke(message.Clone());
            }
        }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception error) { reason = "Codex 通信失败：" + error.Message; }
        finally
        {
            _connectionLost = true;
            foreach (var item in _pending.Values) item.TrySetException(new IOException(reason));
            if (!_stopping) Disconnected?.Invoke(reason);
        }
    }

    private async Task<string?> ReadMessageAsync()
    {
        if (_socket is null) return await _process!.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var part = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 16 * 1024 * 1024)
                throw new InvalidDataException("共享后台消息类型或长度无效。");
            message.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process!.StandardError.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
                if (!_stopping && !string.IsNullOrWhiteSpace(line)) Diagnostic?.Invoke(line);
        }
        catch (OperationCanceledException) when (_stopping) { }
        catch (IOException) { }
    }

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _stopping = true;
        _lifetime.Cancel();
        foreach (var item in _pending.Values) item.TrySetException(new IOException("会话已结束。"));
        _socket?.Abort(); // Detach only. Never shut down or interrupt the shared daemon/thread.
        if (_started && _process is not null)
        {
            try { _process.StandardInput.Close(); } catch (IOException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (_process.HasExited) { }
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        if (_readerTask is not null) await _readerTask.ConfigureAwait(false);
        if (_errorTask is not null) await _errorTask.ConfigureAwait(false);
        _socket?.Dispose();
        _http?.Dispose();
        _process?.Dispose();
        _lifetime.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}

internal sealed record CodexSession(string Id, string Name, string Directory)
{
    public override string ToString() => $"{(string.IsNullOrWhiteSpace(Name) ? "未命名会话" : Name.Replace('\n', ' ').Replace('\r', ' '))} · {Directory} · {Id}";
    public bool Matches(string query) => new[] { Id, Name, Directory }.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
}

internal static class CodexLaunch
{
    public static ProcessStartInfo ForDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("所选工作目录不存在。");
        var (executable, packageRoot) = FindExecutable();
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("app-server");
        if (packageRoot is not null)
        {
            info.Environment["CODEX_MANAGED_PACKAGE_ROOT"] = packageRoot;
            info.Environment["CODEX_MANAGED_BY_NPM"] = "1";
        }
        return info;
    }

    private static (string executable, string? packageRoot) FindExecutable()
    {
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var triple = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            // Do not resolve executables from a relative entry in an untrusted project directory.
            if (!Path.IsPathFullyQualified(directory)) continue;
            var direct = Path.Combine(directory, "codex.exe");
            if (File.Exists(direct)) return (direct, null);
            var package = Path.Combine(directory, "node_modules", "@openai", "codex");
            if (!File.Exists(Path.Combine(package, "bin", "codex.js"))) continue;
            string[] vendorRoots = [
                Path.Combine(package, "node_modules", "@openai", "codex-win32-" + architecture, "vendor"),
                Path.Combine(directory, "node_modules", "@openai", "codex-win32-" + architecture, "vendor"),
                Path.Combine(package, "vendor")
            ];
            foreach (var root in vendorRoots)
            {
                var candidate = Path.Combine(root, triple, "bin", "codex.exe");
                if (File.Exists(candidate)) return (candidate, package);
            }
        }
        throw new FileNotFoundException("未在 PATH 中找到 Codex CLI。请确认已安装 Codex，并重新打开本工具。");
    }
}
