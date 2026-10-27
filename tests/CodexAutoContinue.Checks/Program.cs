using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAutoContinue;

internal static class Program
{
    private static int _checks;
    private static string _scratch = "";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fake-server")
        {
            FakeServer.RunAsync(args.ElementAtOrDefault(1) ?? "normal").GetAwaiter().GetResult();
            return 0;
        }
        _scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.work/checks"));
        Directory.CreateDirectory(_scratch);
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var frame = new DispatcherFrame();
        var work = RunAsync(args);
        _ = work.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try
        {
            work.GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {_checks} assertions. Artifacts: {_scratch}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { app.Shutdown(); }
    }

    private static async Task RunAsync(string[] args)
    {
        if (args.Contains("--tray")) { await TrayCheck.RunAsync(_scratch, Assert); return; }
        if (args.Contains("--shared")) { await NativeCheck.RunSharedAsync(_scratch, Assert, args.Contains("--window")); return; }
        CheckSettings();
        await CheckSchedulerAsync();
        await CheckTransportAsync();
        await CheckCardsAndRenderAsync();
        await IconCheck.RunAsync(_scratch, Assert);
        if (args.Contains("--window")) await TrayCheck.RunAsync(_scratch, Assert);
        if (args.Contains("--native") || args.Contains("--window"))
        {
            await NativeCheck.RunAsync(_scratch, Assert, args.Contains("--window"));
            await NativeCheck.RunSharedAsync(_scratch, Assert, args.Contains("--window"));
        }
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        _checks++;
    }

    private static void CheckSettings()
    {
        var settings = new Settings();
        Assert(settings.MaximumAttempts is null && !settings.JitterEnabled && settings.TrayIconPath == "", "safe defaults");
        Assert(settings.DelayFor(long.MaxValue).TotalSeconds == 30, "fixed unlimited streak");
        var exponential = settings with { Strategy = WaitStrategy.Exponential };
        Assert(new long[] { 1, 2, 3, 4, 5, long.MaxValue }.Select(n => exponential.DelayFor(n).TotalSeconds)
            .SequenceEqual(new double[] { 30, 60, 120, 240, 300, 300 }), "exponential cap and overflow");
        var sequence = settings with { Strategy = WaitStrategy.Sequence, Sequence = "30，60; 120" };
        Assert(sequence.DelayFor(2).TotalSeconds == 60 && sequence.DelayFor(long.MaxValue).TotalSeconds == 120, "sequence repeats tail");
        var jitter = exponential with { JitterEnabled = true };
        Assert(jitter.DelayFor(99, new EdgeRandom(false)).TotalSeconds == 300, "jitter lower endpoint");
        Assert(jitter.DelayFor(99, new EdgeRandom(true)).TotalSeconds == 310, "jitter upper endpoint outside base cap");
        var largest = settings with { FixedSeconds = int.MaxValue, JitterEnabled = true, JitterMaximumSeconds = int.MaxValue };
        Assert(largest.DelayFor(1, new EdgeRandom(true)).TotalSeconds == 2L * int.MaxValue, "long waits do not overflow");
        foreach (var invalid in new[]
        {
            settings with { FixedSeconds = 0 }, settings with { InitialSeconds = 301 },
            settings with { Sequence = "30,no" }, settings with { JitterMinimumSeconds = 11 },
            settings with { MaximumAttempts = 0 }, settings with { Strategy = (WaitStrategy)9 }, settings with { TrayIconPath = null! }
        }) Throws<ArgumentException>(() => invalid.Validate(), "invalid setting rejected");
        var store = new SettingsStore(Path.Combine(_scratch, "settings.json"));
        var saved = settings with { WorkingDirectory = _scratch, AutoText = "继续\n引号 \" and \\ 路径", TrayIconPath = Path.Combine(_scratch, "saved-icon.ico") };
        store.Save(saved);
        Assert(store.Load() == saved, "settings round trip Unicode and newlines");
        Assert(!File.ReadAllText(store.FilePath).Contains("Enabled\": true"), "automation is not persisted");
        store.Save(saved with { FixedSeconds = 12 });
        Assert(File.Exists(store.FilePath + ".bak"), "atomic save backup");
        File.WriteAllText(store.FilePath, "{\"AutoText\":\"legacy\"}");
        Assert(store.Load().TrayIconPath == "" && store.Load().AutoText == "legacy", "old settings default to the built-in tray icon");
        File.WriteAllText(store.FilePath, "{broken");
        Throws<JsonException>(() => store.Load(), "corrupt settings reported");
        Assert(File.ReadAllText(store.FilePath) == "{broken", "corrupt source not overwritten");
        Console.WriteLine("Settings OK");
    }

    private static async Task CheckSchedulerAsync()
    {
        var sent = new List<string>();
        var retry = new RetryController((_, text) => { sent.Add(text); return Task.FromResult("auto-" + sent.Count); });
        var successes = 0;
        retry.TurnSucceeded += () => successes++;
        retry.Apply(new Settings { AutoText = "old", FixedSeconds = 1 });
        retry.Bind("thread");
        retry.SetEnabled(true);
        Start(retry, "first");
        retry.Handle("item/agentMessage/delta", Json(new { threadId = "thread", delta = RetryController.TargetMessage }));
        retry.Handle("error", Json(new { threadId = "thread", turnId = "first", willRetry = false,
            error = new { message = RetryController.TargetMessage } }));
        Assert(!retry.Waiting && retry.Busy, "only terminal failure schedules");
        Complete(retry, "first", "failed");
        Assert(retry.Waiting, "target final failure schedules");
        Assert(successes == 0, "retry and final failure do not signal success");
        await Task.Delay(150);
        var remaining = retry.Remaining;
        Complete(retry, "first", "failed");
        Assert(retry.Remaining <= remaining, "duplicate completion does not restart countdown");
        retry.Apply(new Settings { AutoText = "new", FixedSeconds = 1, MaximumAttempts = 1 });
        Assert(retry.Remaining > remaining, "save resets countdown");
        await UntilAsync(() => sent.Count == 1);
        Assert(sent.SequenceEqual(new[] { "new" }) && retry.Attempts == 1, "only new saved text sent and accepted counted");
        Complete(retry, "auto-1", "failed");
        Assert(!retry.Waiting && !retry.Enabled && retry.Attempts == 1, "limit pauses automation");
        await retry.SendManualAsync("manual");
        Assert(retry.Attempts == 0, "manual send resets count");
        Complete(retry, "auto-2", "completed");
        Complete(retry, "auto-2", "completed");
        Assert(successes == 1, "manual success signals once despite duplicate completion");
        Assert(!retry.Busy && retry.CanSend, "same session usable after success");

        // Cancel several real timers together; no cancelled task may leak a send.
        var cancelledSends = 0;
        var controllers = Enumerable.Range(0, 7).Select(_ =>
        {
            var c = new RetryController((_, _) => { cancelledSends++; return Task.FromResult("unexpected"); });
            c.Apply(new Settings { AutoText = "continue", FixedSeconds = 1 }); c.Bind("thread"); c.SetEnabled(true);
            Start(c, "failed"); Complete(c, "failed", "failed"); return c;
        }).ToArray();
        controllers[0].SetEnabled(false);
        controllers[1].StopPending();
        controllers[2].Disconnect();
        controllers[3].Bind("new-thread");
        controllers[4].Pause("connection lost");
        controllers[5].SetPendingRequests(1);
        controllers[6].Apply(new Settings { AutoText = "later", FixedSeconds = 60 });
        await Task.Delay(1100);
        Assert(cancelledSends == 0, "disable/stop/disconnect/rebind/pause/approval/save cancel stale waits");
        controllers[0].SetEnabled(true);
        Assert(controllers[0].Waiting, "enable handles current unhandled failure");
        controllers[5].SetPendingRequests(0);
        Assert(controllers[5].Waiting, "pending approval defers automatic send");
        controllers[1].SetEnabled(true);
        Assert(!controllers[1].Waiting, "stop consumes this failure");
        foreach (var c in controllers) c.Disconnect();

        var manual = new RetryController((_, _) => Task.FromResult("manual-turn"));
        var stoppedSuccesses = 0;
        manual.TurnSucceeded += () => stoppedSuccesses++;
        manual.Apply(new Settings { AutoText = "continue", FixedSeconds = 1 }); manual.Bind("thread"); manual.SetEnabled(true);
        Start(manual, "failure"); Complete(manual, "failure", "failed");
        await manual.SendManualAsync("manual");
        Assert(!manual.Waiting && manual.TurnId == "manual-turn", "manual cancels wait");
        manual.StopPending(); Complete(manual, "manual-turn", "failed");
        Assert(!manual.Waiting, "stop suppresses late failure from stopping turn");
        Start(manual, "stopping"); manual.StopPending(); Complete(manual, "stopping", "completed");
        Assert(stoppedSuccesses == 0, "stop suppresses a racing success notification");
        manual.Disconnect();
        var nonTarget = new RetryController((_, _) => Task.FromResult("unused"));
        nonTarget.Apply(new Settings { AutoText = "continue" }); nonTarget.Bind("thread"); nonTarget.SetEnabled(true);
        Start(nonTarget, "other"); Complete(nonTarget, "other", "failed", "another internalServerError");
        Assert(!nonTarget.Enabled, "non-target error pauses");
        nonTarget.Disconnect();

        foreach (var status in new[] { "completed", "interrupted", "failed" })
        {
            RetryController fast = null!;
            var called = false;
            var fastSuccesses = 0;
            fast = new RetryController(async (_, _) =>
            {
                Start(fast, "fast"); Complete(fast, "fast", status, status == "failed" ? "different error" : null);
                await Task.Delay(30); called = true; return "fast";
            });
            fast.TurnSucceeded += () => fastSuccesses++;
            fast.Apply(new Settings { AutoText = "continue", FixedSeconds = 1 }); fast.Bind("thread"); fast.SetEnabled(true);
            Start(fast, "initial"); Complete(fast, "initial", "failed");
            await UntilAsync(() => called && !fast.Sending);
            Assert(fast.Attempts == (status == "completed" ? 0 : 1), "early " + status + " / late acknowledgment count");
            Assert(!fast.Waiting && !fast.Busy, "early final event leaves no running turn");
            Assert(fastSuccesses == (status == "completed" ? 1 : 0), "automatic success notification before acknowledgment: " + status);
            fast.Disconnect();
        }
        var unknown = new RetryController((_, _) => throw new TimeoutException("unknown send outcome"));
        unknown.Bind("thread");
        await ThrowsAsync<TimeoutException>(() => unknown.SendManualAsync("test"), "send outcome unknown");
        Assert(!unknown.Enabled && unknown.Busy && !unknown.CanSend, "unknown send outcome blocks blind resend");
        unknown.Disconnect();
        var restored = new RetryController((_, _) => Task.FromResult("unused"));
        var restoredSuccesses = 0;
        restored.TurnSucceeded += () => restoredSuccesses++;
        restored.Apply(new Settings { AutoText = "continue" });
        restored.Bind("thread", Json(new { id = "old-success", status = "completed" }));
        Assert(restored.CanSend && restoredSuccesses == 0 && !restored.Enabled, "binding history never reports old success or enables automation");
        restored.Bind("thread", Json(new { id = "old-failure", status = "failed", error = new { message = RetryController.TargetMessage } }));
        restored.SetEnabled(true);
        Assert(restored.Waiting, "binding latest target failure can schedule after explicit enable");
        restored.Disconnect();

        var typographySends = 0;
        var typography = new RetryController((_, _) => Task.FromResult("typography-" + ++typographySends));
        typography.Apply(new Settings { AutoText = "continue", FixedSeconds = 1 });
        typography.Bind("thread"); typography.SetEnabled(true);
        await typography.SendManualAsync("manual");
        Assert(typography.Enabled, "manual send preserves enabled automation");
        const string curlyError = "We’re currently experiencing high demand, which may cause temporary errors.";
        typography.Handle("error", Json(new { threadId = "thread", turnId = "typography-1", willRetry = true,
            error = new { message = curlyError } }));
        Assert(typography.Enabled && typography.Busy && !typography.Waiting, "curly-apostrophe internal retry does not send");
        Complete(typography, "typography-1", "failed", curlyError);
        Assert(typography.Enabled && typography.Waiting, "curly-apostrophe target failure keeps automation enabled and schedules");
        await UntilAsync(() => typographySends == 2 && !typography.Sending);
        Assert(typography.Enabled && typography.Attempts == 1 && typography.TurnId == "typography-2", "curly-apostrophe failure automatically continues");
        typography.Disconnect();

        const string overloadError = "stream disconnected before completion: anyrouter: 当前模型 gpt-6-astra 负载已经达到上限，请稍后重试 (request id: 20261027104114305878459HaCJeLBK) (500: you can retry your request)";
        Assert(RetryController.IsTargetFailure(Json(new { message = "本轮失败：" + overloadError })), "model overload tolerates the displayed failure prefix");
        Assert(RetryController.IsTargetFailure(Json(new { message = "anyrouter: 当前模型 another-model 负载已经达到上限，请稍后重试 (request id: another-request)" })),
            "model overload does not depend on model name, request ID or stream wrapper");
        foreach (var message in new[] { "", "stream disconnected before completion: network error",
            "stream disconnected before completion: anyrouter: internal server error (500: you can retry your request)" })
            Assert(!RetryController.IsTargetFailure(Json(new { message })), "unrelated errors remain non-target: " + message);
        Assert(!RetryController.IsTargetFailure(default) && !RetryController.IsTargetFailure(Json(new { message = 500 })),
            "missing and non-text error messages remain non-target");

        var overloadSent = new List<(string Thread, string Text)>();
        var overload = new RetryController((thread, text) =>
        {
            overloadSent.Add((thread, text));
            return Task.FromResult("overload-auto-" + overloadSent.Count);
        });
        overload.Apply(new Settings { AutoText = "continue", FixedSeconds = 1 });
        overload.Bind("thread"); overload.SetEnabled(true);
        Start(overload, "overload-failure");
        foreach (var method in new[] { "item/agentMessage/delta", "item/commandExecution/outputDelta" })
            overload.Handle(method, Json(new { threadId = "thread", delta = overloadError }));
        foreach (var willRetry in new[] { true, false })
            overload.Handle("error", Json(new { threadId = "thread", turnId = "overload-failure", willRetry,
                error = new { message = overloadError } }));
        Assert(overload.Enabled && overload.Busy && !overload.Waiting && overloadSent.Count == 0,
            "overload output and error notifications do not send before final failure");
        Complete(overload, "overload-failure", "failed", overloadError);
        Assert(overload.Enabled && overload.Waiting, "final model overload schedules automatic continuation");
        Complete(overload, "overload-failure", "failed", overloadError);
        await UntilAsync(() => overloadSent.Count == 1 && !overload.Sending);
        Complete(overload, "overload-failure", "failed", overloadError);
        Assert(overloadSent.SequenceEqual(new[] { ("thread", "continue") }) && overload.Attempts == 1 && !overload.Waiting,
            "duplicate model overload completion sends saved text to the same thread only once");
        overload.Disconnect();
        overload.Bind("thread", Json(new { id = "bound-overload", status = "failed", error = new { message = overloadError } }));
        Assert(!overload.Enabled && !overload.Waiting && overload.CanSend, "binding a model overload does not enable automation");
        overload.SetEnabled(true);
        Assert(overload.Waiting, "enabling after binding a model overload schedules continuation");
        await UntilAsync(() => overloadSent.Count == 2 && !overload.Sending);
        Assert(overloadSent.All(message => message == ("thread", "continue")) && overload.Attempts == 1,
            "bound model overload automatically continues the same thread");
        overload.Disconnect();

        var session = new CodexSession("id-123", "Test Name", @"D:\project");
        Assert(session.Matches("NAME") && session.Matches("project") && session.Matches("123") && !session.Matches("missing"), "session filter matches name, directory and ID");
        Console.WriteLine("Scheduler OK");
    }

    private static async Task CheckTransportAsync()
    {
        await using (var client = new CodexClient(FakeServer.Launch()) { RequestTimeout = TimeSpan.FromMilliseconds(500) })
        {
            var text = new TaskCompletionSource<string>();
            var approval = new TaskCompletionSource<JsonElement>();
            var response = new TaskCompletionSource<JsonElement>();
            client.Message += message =>
            {
                if (message.Text("method") == "item/agentMessage/delta") text.TrySetResult(message.Part("params").Text("delta")!);
                if (message.Text("method") == "item/commandExecution/requestApproval") approval.TrySetResult(message.Part("id"));
                if (message.Text("method") == "checks/replied") response.TrySetResult(message.Part("params"));
            };
            var thread = await client.StartAsync(_scratch);
            Assert(thread == "thread-1", "handshake and thread start");
            const string unicode = "继续\n中文、引号 \" \\ and emoji 🧪";
            Assert(await client.SendAsync(thread, unicode) == "turn-1", "turn accepted");
            Assert(await text.Task.WaitAsync(TimeSpan.FromSeconds(2)) == unicode, "UTF-8 JSONL exact round trip");
            await client.RequestAsync("checks/approval", new { });
            await client.RespondAsync(await approval.Task, new { decision = "decline" });
            var replied = await response.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(replied.Text("id") == "approval-中文" && replied.Part("result").Text("decision") == "decline", "string server request IDs round trip");
            await ThrowsAsync<TimeoutException>(() => client.RequestAsync("checks/hang", new { }), "RPC timeout");
            await ThrowsAsync<IOException>(() => client.RequestAsync("checks/close", new { }), "EOF rejects pending requests");
        }
        await using (var bad = new CodexClient(FakeServer.Launch()))
        {
            await bad.StartAsync(_scratch);
            await ThrowsAsync<IOException>(() => bad.RequestAsync("checks/malformed", new { }), "invalid JSON disconnect");
        }
        using var sentinel = Process.Start(FakeServer.Launch("linger"))!;
        try
        {
            await using var stuck = new CodexClient(FakeServer.Launch("linger"));
            await stuck.StartAsync(_scratch);
            await stuck.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert(!sentinel.HasExited, "shutdown kills only its owned process");
        }
        finally { if (!sentinel.HasExited) sentinel.Kill(entireProcessTree: true); await sentinel.WaitForExitAsync(); }
        Console.WriteLine("Transport OK");
    }

    private static async Task CheckCardsAndRenderAsync()
    {
        var result = new TaskCompletionSource<JsonElement>();
        var card = new RequestCard(Json(new { id = "approval", method = "item/permissions/requestApproval", @params = new
        { threadId = "thread", turnId = "turn", reason = "验证权限不会自动同意", permissions = new { network = new { enabled = true }, fileSystem = (object?)null } } }),
            default, value => { result.TrySetResult(Json(value)); return Task.CompletedTask; });
        Assert(!result.Task.IsCompleted, "no implicit approval");
        Descendants<Button>(card).Single(b => Equals(b.Content, "拒绝")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert((await result.Task).Part("permissions").EnumerateObject().Count() == 0, "permission decline grants nothing");
        Assert(Json("approval-中文").IdKey() == JsonDocument.Parse("\"approval-\\u4e2d\\u6587\"").RootElement.IdKey(), "escaped ID equivalence");

        var store = new SettingsStore(Path.Combine(_scratch, "ui-settings.json"));
        store.Save(new Settings { WorkingDirectory = _scratch, AutoText = "请继续刚才的任务。", Strategy = WaitStrategy.Exponential });
        var window = new MainWindow(store);
        Assert(!((CheckBox)window.FindName("AutoToggle")).IsChecked!.Value, "reopened window defaults off");
        var panel = (Panel)window.FindName("RequestsPanel");
        panel.Children.Add(new RequestCard(Json(new { id = "command", method = "item/commandExecution/requestApproval", @params = new
        { threadId = "thread", turnId = "turn", command = "dotnet build", cwd = _scratch, reason = "需要运行项目的构建检查。" } }), default, _ => Task.CompletedTask));
        ((ScrollViewer)window.FindName("RequestScroller")).Visibility = Visibility.Visible;
        ((TextBox)window.FindName("OutputBox")).Text = "[12:00:00] 新会话已启动。\n\n你：\n请完成当前项目。\n\nCodex：\n正在检查项目，保留当前会话和手动输入能力。\n\n[12:00:02] 等待人工确认。";
        var content = (FrameworkElement)window.Content;
        Render(content, 1232, 806, Path.Combine(_scratch, "window.png"));
        Render(content, 952, 586, Path.Combine(_scratch, "window-small.png"));
        Assert(content.ActualWidth == 952 - content.Margin.Left - content.Margin.Right, "minimum window layout");
        foreach (var name in new[] { "SendButton", "StopButton", "ApplyButton", "CountdownText" })
        {
            var control = (FrameworkElement)window.FindName(name);
            var bottom = control.TransformToAncestor(content).Transform(new Point(0, control.ActualHeight));
            Assert(bottom.Y <= content.ActualHeight, name + " remains reachable at minimum size");
        }
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(_scratch, "missing-" + Guid.NewGuid().ToString("N")[..6]));
            var connect = (Button)window.FindName("ConnectExistingButton");
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => connect.IsEnabled, 15);
            Assert(((FrameworkElement)window.FindName("SharedPicker")).Visibility == Visibility.Visible,
                "failed shared connection keeps the session picker visible");
            Assert(((TextBlock)window.FindName("FooterText")).Text.Contains("无法连接 Codex 共享后台")
                && !((Button)window.FindName("AttachButton")).IsEnabled, "connection error explains why no session can be selected");
            Render(content, 952, 586, Path.Combine(_scratch, "shared-connect-failed.png"));
            Descendants<Button>(window).Single(b => Equals(b.Content, "取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => connect.IsEnabled);
            Assert(((FrameworkElement)window.FindName("SharedPicker")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)window.FindName("ConversationWorkspace")).Visibility == Visibility.Visible,
                "cancel failed selection returns to the conversation workspace");
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => connect.IsEnabled, 15);
            Assert(((FrameworkElement)window.FindName("SharedPicker")).Visibility == Visibility.Visible
                && ((ListBox)window.FindName("SessionsBox")).Items.Count == 0, "reopening failed connection has no stale sessions");
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", previousHome); }
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        await Task.Delay(30);
        Assert(closed, "closing an idle window is not reentrant");
        var notice = new CompletionNotice();
        Render((FrameworkElement)notice.Content, 360, 172, Path.Combine(_scratch, "completion-notice.png"));
        Assert(!notice.ShowActivated && !notice.ShowInTaskbar, "completion notice does not request activation or taskbar entry");
        notice.Close();
        Console.WriteLine("Request cards and WPF render OK");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    internal static void Render(FrameworkElement content, int width, int height, string path)
    {
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(243, 246, 248)), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static void Start(RetryController retry, string turn) => retry.Handle("turn/started", Json(new { threadId = "thread", turn = new { id = turn } }));
    private static void Complete(RetryController retry, string turn, string status, string? message = null) =>
        retry.Handle("turn/completed", Json(new { threadId = "thread", turn = new { id = turn, status,
            error = new { message = message ?? RetryController.TargetMessage + ".", codexErrorInfo = "internalServerError" } } }));
    internal static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    internal static async Task UntilAsync(Func<bool> condition, int seconds = 5)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start).TotalSeconds > seconds) throw new TimeoutException("check timed out");
            await Task.Delay(15);
        }
    }
    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); } catch (T) { Assert(true, label); return; }
        throw new Exception("FAIL: " + label);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
    {
        try { await action(); } catch (T) { Assert(true, label); return; }
        throw new Exception("FAIL: " + label);
    }
    private sealed class EdgeRandom(bool upper) : Random
    {
        public override long NextInt64(long minValue, long maxValue) => upper ? maxValue - 1 : minValue;
    }
}
