using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexAutoContinue;

// The real installed CLI talks only to this disposable loopback provider.
internal static partial class NativeCheck
{
    internal static async Task RunAsync(string scratch, Action<bool, string> assert, bool showWindow)
    {
        var directory = Path.Combine(scratch, "native-" + Guid.NewGuid().ToString("N")[..8]);
        var codexHome = Path.Combine(directory, "home");
        var project = Path.Combine(directory, "project");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(project);
        using var cancel = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"), $$"""
            model = "probe-model"
            model_provider = "probe"
            approval_policy = "never"
            sandbox_mode = "read-only"
            [model_providers.probe]
            name = "Local protocol check"
            base_url = "http://127.0.0.1:{{port}}/v1"
            wire_api = "responses"
            requires_openai_auth = false
            request_max_retries = 0
            stream_max_retries = 1
            """);
        var requests = 0;
        var server = ServeAsync(listener, cancel.Token, () => ++requests);
        var launch = CodexLaunch.ForDirectory(project);
        launch.Environment["CODEX_HOME"] = codexHome;
        foreach (var name in new[] { "OPENAI_API_KEY", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) launch.Environment.Remove(name);
        var client = new CodexClient(launch);
        var retry = new RetryController(client.SendAsync);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var events = new List<JsonElement>();
        var finals = 0;
        var internalRetries = 0;
        var automatic = 0;
        var threadIds = new HashSet<string>();
        Exception? failure = null;
        retry.Apply(new Settings { AutoText = "本地校验：继续。", FixedSeconds = 1, MaximumAttempts = 2 });
        retry.MessageAccepted += (_, auto) => { if (auto) automatic++; };
        client.Message += message => dispatcher.BeginInvoke(() =>
        {
            try
            {
                var method = message.Text("method")!;
                var parameters = message.Part("params");
                retry.Handle(method, parameters);
                if (method is "error" or "turn/completed") events.Add(message);
                if (method == "error" && parameters.Part("willRetry").ValueKind == JsonValueKind.True)
                {
                    internalRetries++;
                    assert(!retry.Waiting && automatic == 0, "native internal retry must not schedule");
                }
                if (method == "turn/completed")
                {
                    finals++;
                    threadIds.Add(parameters.Text("threadId")!);
                    var turn = parameters.Part("turn");
                    if (finals == 1)
                    {
                        assert(turn.Text("status") == "failed", "native initial turn finally failed");
                        assert(RetryController.IsTargetFailure(turn.Part("error")), "native final high demand message");
                        assert(turn.Part("error").Text("codexErrorInfo") == "internalServerError", "native verified error mapping");
                    }
                    else assert(turn.Text("status") == "completed", "native continuation succeeds");
                }
            }
            catch (Exception error) { failure = error; }
        });
        client.Disconnected += reason => dispatcher.BeginInvoke(() => failure = new IOException(reason));
        try
        {
            var thread = await client.StartAsync(project);
            retry.Bind(thread);
            retry.SetEnabled(true);
            await retry.SendManualAsync("Local protocol verification. No tools.");
            await Program.UntilAsync(() => failure is not null || finals >= 2 && !retry.Sending, 45);
            if (failure is not null) throw failure;
            assert(internalRetries == 1 && automatic == 1 && requests == 3, "native retry exhaustion then one automatic continuation");
            assert(threadIds.SetEquals(new[] { thread }), "native reuses one thread");
            assert(retry.Attempts == 0 && !retry.Busy && !retry.Waiting, "native success clears attempts and countdown");
            Console.WriteLine("Native Codex: internal retry -> final high demand -> automatic continuation -> success OK");
            await client.StopAsync();
            retry.Disconnect();
            requests = 0;
            await CheckWindowAsync(directory, codexHome, project, assert, showWindow);
            assert(requests == 3, "window uses native internal retry plus exactly one continuation");
        }
        finally
        {
            retry.Disconnect();
            await File.WriteAllTextAsync(Path.Combine(directory, "events.json"), JsonSerializer.Serialize(events, SettingsStore.JsonOptions));
            await client.StopAsync();
            cancel.Cancel();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { } catch (SocketException) when (cancel.IsCancellationRequested) { }
        }
    }

    private static async Task CheckWindowAsync(string directory, string codexHome, string project,
        Action<bool, string> assert, bool showWindow)
    {
        // Only this check process inherits these values; machine/user environment is never modified.
        string[] names = ["CODEX_HOME", "OPENAI_API_KEY", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        MainWindow? window = null;
        var closed = false;
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            var store = new SettingsStore(Path.Combine(directory, "window-settings.json"));
            store.Save(new Settings { WorkingDirectory = project, AutoText = "窗口校验：继续。", FixedSeconds = 1 });
            window = new MainWindow(store) { ShowActivated = false };
            window.Closed += (_, _) => closed = true;
            if (showWindow) window.Show();
            var start = (Button)window.FindName("StartButton");
            var send = (Button)window.FindName("SendButton");
            var manual = (TextBox)window.FindName("ManualBox");
            var output = (TextBox)window.FindName("OutputBox");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.UntilAsync(() => send.IsEnabled, 15);
            ((CheckBox)window.FindName("AutoToggle")).IsChecked = true;
            manual.Text = "Local window integration check. No tools.";
            nint foreground = 0;
            uint clipboard = 0;
            if (showWindow)
            {
                window.WindowState = WindowState.Minimized;
                await SettleForegroundAsync();
                foreground = GetForegroundWindow();
                clipboard = GetClipboardSequenceNumber();
                Console.WriteLine("Automation baseline: " + ForegroundDescription(foreground));
                assert(foreground != 0 && clipboard != 0, "desktop observations available");
            }
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(!send.IsEnabled && manual.IsEnabled, "running UI blocks send but allows draft editing");
            manual.Text = "执行期间编辑的草稿";
            var stayedBackground = true;
            var clipboardUnchanged = true;
            var lastForeground = foreground;
            await Program.UntilAsync(() =>
            {
                if (showWindow)
                {
                    var current = GetForegroundWindow();
                    if (current != lastForeground)
                    {
                        Console.WriteLine("Automation foreground changed: " + ForegroundDescription(current));
                        lastForeground = current;
                    }
                    stayedBackground &= window.WindowState == WindowState.Minimized && current == foreground;
                    clipboardUnchanged &= GetClipboardSequenceNumber() == clipboard;
                }
                return output.Text.Contains("本轮完成。");
            }, 45);
            assert(output.Text.Replace('\u2019', '\'').Contains(RetryController.TargetMessage) && output.Text.Contains("自动发送："), "window renders failure and automatic message");
            assert(((TextBlock)window.FindName("AttemptsText")).Text == "本轮连续发送 0 次", "window displays reset count");
            assert(manual.Text == "执行期间编辑的草稿" && send.IsEnabled, "new draft survives send acknowledgment");
            if (showWindow)
            {
                assert(stayedBackground, "minimized automation never changes foreground window");
                assert(clipboardUnchanged, "minimized automation never changes clipboard sequence");
                var notice = Application.Current.Windows.OfType<CompletionNotice>().Single();
                assert(notice.IsVisible && !notice.IsActive, "normal completion shows one non-activating notice while minimized");
                var previousForeground = foreground;
                Console.WriteLine($"Notice baseline: {ForegroundDescription(foreground)}; mainHWND={new System.Windows.Interop.WindowInteropHelper(window).Handle:X}; noticeHWND={new System.Windows.Interop.WindowInteropHelper(notice).Handle:X}");
                await Program.UntilAsync(() =>
                {
                    var current = GetForegroundWindow();
                    if (current != previousForeground)
                    {
                        Console.WriteLine($"Foreground changed; notice visible={notice.IsVisible}: {ForegroundDescription(current)}");
                        previousForeground = current;
                    }
                    return !notice.IsVisible;
                }, 12);
                assert(GetForegroundWindow() == foreground, "notice expiry does not change foreground window; baseline=" + ForegroundDescription(foreground) + "; final=" + ForegroundDescription(GetForegroundWindow()));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "window-output.txt"), output.Text);
        }
        finally
        {
            if (window is not null)
            {
                window.Close();
                await Program.UntilAsync(() => closed, 15);
                assert(!((TextBlock)window.FindName("FooterText")).Text.Contains("结束会话失败"), "window close stops its Codex backend");
                assert(!Application.Current.Windows.OfType<CompletionNotice>().Any(), "no completion notice remains after closing the app");
            }
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
        Console.WriteLine(showWindow ? "Live minimized window / foreground / clipboard OK" : "Window handlers / native Codex / close OK");
    }

    // Borrowed HWND used only as an identity value; it is never owned or released here.
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getforegroundwindow
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    // DWORD; observes a revision number without reading any clipboard content.
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber
    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    // Borrowed HWND; DWORD thread ID return and optional DWORD process ID output (WinUser.h).
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    private static string ForegroundDescription(nint window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return $"HWND={window:X}, PID={processId}, testPID={Environment.ProcessId}";
    }

    private static async Task SettleForegroundAsync()
    {
        // Minimize itself changes foreground asynchronously. Establish the baseline before sending.
        var previous = GetForegroundWindow();
        var stableSince = System.Diagnostics.Stopwatch.GetTimestamp();
        await Program.UntilAsync(() =>
        {
            var current = GetForegroundWindow();
            if (current != previous) { previous = current; stableSince = System.Diagnostics.Stopwatch.GetTimestamp(); }
            return current != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(stableSince).TotalMilliseconds >= 600;
        });
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken cancellation, Func<int> nextRequest,
        Func<int, string?>? responseOverride = null)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            await using var stream = client.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 65536)
            {
                if (await stream.ReadAsync(one, cancellation) == 0) throw new IOException("Incomplete probe HTTP request");
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var length = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (length is not null && int.TryParse(length.Split(':', 2)[1].Trim(), out var bytes) && bytes > 0)
            {
                if (bytes > 16_000_000) throw new IOException("Probe request too large");
                await stream.ReadExactlyAsync(new byte[bytes], cancellation);
            }
            string body, status, contentType;
            var requestNumber = lines[0].StartsWith("GET ") ? 0 : nextRequest();
            if (lines[0].StartsWith("GET "))
            {
                status = "200 OK"; contentType = "application/json"; body = "{\"object\":\"list\",\"data\":[]}";
            }
            else if (requestNumber <= 2)
            {
                status = "500 Internal Server Error"; contentType = "application/json";
                body = "{\"error\":{\"code\":\"server_error\",\"message\":\"controlled local failure\"}}";
            }
            else
            {
                status = "200 OK"; contentType = "text/event-stream";
                var item = new { id = "probe-message", type = "message", status = "completed", role = "assistant",
                    content = new[] { new { type = "output_text", text = "本地校验完成。", annotations = Array.Empty<object>() } } };
                body = Event("response.created", new { type = "response.created", response = new { id = "probe-response" } })
                    + Event("response.output_item.added", new { type = "response.output_item.added", output_index = 0, item })
                    + Event("response.output_text.delta", new { type = "response.output_text.delta", item_id = item.id, output_index = 0, content_index = 0, delta = "本地校验完成。" })
                    + Event("response.output_item.done", new { type = "response.output_item.done", output_index = 0, item })
                    + Event("response.completed", new { type = "response.completed", response = new { id = "probe-response", status = "completed",
                        output = new[] { item }, usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } } });
                body = responseOverride?.Invoke(requestNumber) ?? body;
            }
            var data = Encoding.UTF8.GetBytes(body);
            var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeader, cancellation);
            await stream.WriteAsync(data, cancellation);
            await stream.FlushAsync(cancellation);
        }
    }

    private static string Event(string name, object data) => $"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n";
}
