using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexAutoContinue;

internal static partial class NativeCheck
{
    internal static async Task RunSharedAsync(string scratch, Action<bool, string> assert, bool showWindow)
    {
        // Windows AF_UNIX paths must fit in 108 bytes; keep the isolated home short.
        var home = Path.GetFullPath(Path.Combine(scratch, "../s" + Guid.NewGuid().ToString("N")[..6]));
        Directory.CreateDirectory(home);
        using var cancel = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), $$"""
            model = "probe-model"
            model_provider = "probe"
            approval_policy = "on-request"
            sandbox_mode = "read-only"
            [model_providers.probe]
            name = "Local shared check"
            base_url = "http://127.0.0.1:{{port}}/v1"
            wire_api = "responses"
            requires_openai_auth = false
            request_max_retries = 0
            stream_max_retries = 1
            """);
        var requests = 0;
        var mock = ServeAsync(listener, cancel.Token, () => ++requests, number => number == 4 ? ApprovalResponse() : null);
        var launch = CodexLaunch.ForDirectory(home);
        launch.Environment["CODEX_HOME"] = home;
        foreach (var name in new[] { "OPENAI_API_KEY", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) launch.Environment.Remove(name);
        launch.ArgumentList.Add("--listen"); launch.ArgumentList.Add("unix://");
        using var server = Process.Start(launch)!;
        var stderr = server.StandardError.ReadToEndAsync();
        var stdout = server.StandardOutput.ReadToEndAsync();
        var socketPath = Path.Combine(home, "app-server-control", "app-server-control.sock");
        await using var owner = new CodexClient(socketPath);
        var ownerEvents = new List<JsonElement>();
        var dispatcher = Dispatcher.CurrentDispatcher;
        owner.Message += message => dispatcher.BeginInvoke(() => ownerEvents.Add(message));
        MainWindow? window = null;
        var closed = false;
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            await Program.UntilAsync(() => File.Exists(socketPath) || server.HasExited, 10);
            if (server.HasExited) throw new IOException(await stderr);
            await owner.ConnectAsync();
            var start = await owner.RequestAsync("thread/start", new { cwd = home });
            var thread = start.Part("thread").Text("id")!;
            await owner.RequestAsync("thread/name/set", new { threadId = thread, name = "绑定检查 中文" });
            await owner.SendAsync(thread, "Local probe. No tools.");
            await Program.UntilAsync(() => Finals(ownerEvents).Count == 1, 30);
            assert(Finals(ownerEvents)[0].Part("turn").Text("status") == "failed", "shared owner reaches final high demand before attachment");

            var otherDirectory = Path.Combine(scratch, "other-project");
            Directory.CreateDirectory(otherDirectory);
            var otherThread = (await owner.RequestAsync("thread/start", new { cwd = otherDirectory })).Part("thread").Text("id")!;
            await owner.RequestAsync("thread/name/set", new { threadId = otherThread, name = "另一个会话（不绑定）" });

            Environment.SetEnvironmentVariable("CODEX_HOME", home);
            var store = new SettingsStore(Path.Combine(home, "window-settings.json"));
            store.Save(new Settings { WorkingDirectory = home, AutoText = "共享会话：继续。", FixedSeconds = 1 });
            window = new MainWindow(store) { ShowActivated = false };
            window.Closed += (_, _) => closed = true;
            if (showWindow) window.Show();
            Click(window, "ConnectExistingButton");
            await Program.UntilAsync(() => ((Button)window.FindName("AttachButton")).IsEnabled, 15);
            var sessions = (ListBox)window.FindName("SessionsBox");
            var filter = (TextBox)window.FindName("SessionFilter");
            assert(sessions.Items.Count == 2, "picker lists both loaded sessions");
            sessions.SelectedItem = sessions.Items.Cast<CodexSession>().Single(s => s.Id == otherThread);
            Click(window, "RefreshSessionsButton");
            await Program.UntilAsync(() => ((Button)window.FindName("RefreshSessionsButton")).IsEnabled, 15);
            assert(((CodexSession)sessions.SelectedItem).Id == otherThread, "refresh preserves the explicitly selected session");
            filter.Text = "no-such-session-" + Guid.NewGuid().ToString("N");
            assert(sessions.Items.Count == 0 && !((Button)window.FindName("AttachButton")).IsEnabled, "no matching session cannot bind a stale selection");
            foreach (var query in new[] { "中文", home, thread })
            {
                filter.Text = query;
                assert(sessions.Items.Count == 1 && ((CodexSession)sessions.SelectedItem).Id == thread, "live session selector: " + query);
            }
            filter.Clear();
            sessions.SelectedItem = sessions.Items.Cast<CodexSession>().Single(s => s.Id == thread);
            assert(sessions.Items.Count == 2 && ((CodexSession)sessions.SelectedItem).Id == thread, "target can be chosen explicitly from multiple sessions");
            Program.Render((FrameworkElement)window.Content, 1232, 806, Path.Combine(scratch, "shared-picker.png"));
            Click(window, "AttachButton");
            var auto = (CheckBox)window.FindName("AutoToggle");
            await Program.UntilAsync(() => auto.IsEnabled, 15);
            var output = (TextBox)window.FindName("OutputBox");
            assert(auto.IsChecked == false && !Application.Current.Windows.OfType<CompletionNotice>().Any(), "attachment starts disabled and does not notify old completion");
            await Program.UntilAsync(() => output.Text.Replace('\u2019', '\'').Contains(RetryController.TargetMessage));
            assert(((TextBox)window.FindName("DirectoryBox")).Text == home, "bound directory comes from original session");
            Program.Render((FrameworkElement)window.Content, 952, 650, Path.Combine(scratch, "shared-bound-small.png"));
            nint foreground = 0;
            uint clipboard = 0;
            if (showWindow)
            {
                window.WindowState = WindowState.Minimized;
                await SettleForegroundAsync();
                foreground = GetForegroundWindow(); clipboard = GetClipboardSequenceNumber();
                Console.WriteLine($"Shared baseline: {ForegroundDescription(foreground)}, clipboard={clipboard}");
            }
            auto.IsChecked = true;
            await Program.UntilAsync(() => Finals(ownerEvents).Count == 2 && ((Button)window.FindName("SendButton")).IsEnabled, 30);
            assert(requests == 3 && Finals(ownerEvents)[1].Part("turn").Text("status") == "completed", "bound final failure triggers exactly one continuation after internal retry");
            assert(Finals(ownerEvents).All(p => p.Text("threadId") == thread), "attachment and continuation retain original thread ID");
            var untouched = (await owner.RequestAsync("thread/read", new { threadId = otherThread, includeTurns = true })).Part("thread");
            assert(untouched.Part("turns").GetArrayLength() == 0, "unselected session never receives a message");
            await Program.UntilAsync(() => output.Text.Contains("自动发送：") && output.Text.Contains("本轮完成。"));
            assert(auto.IsChecked == true, "own acknowledged turn does not look like external input");
            if (showWindow)
            {
                await Program.UntilAsync(() => Application.Current.Windows.OfType<CompletionNotice>().Any());
                Console.WriteLine($"Shared completed: {ForegroundDescription(GetForegroundWindow())}, clipboard={GetClipboardSequenceNumber()}, minimized={window.WindowState == WindowState.Minimized}");
                assert(GetForegroundWindow() == foreground && GetClipboardSequenceNumber() == clipboard, "shared recovery and popup preserve focus and clipboard");
                assert(Application.Current.Windows.OfType<CompletionNotice>().Single().IsActive == false, "shared completion popup is non-activating");
            }

            // Deliberate out-of-band input must pause automation. The model then asks for approval;
            // closing the observer must not decline it, interrupt it, or close the server.
            await owner.SendAsync(thread, "Local approval probe. Request elevated shell command.");
            await Program.UntilAsync(() => ownerEvents.Any(e => e.Text("method") == "item/commandExecution/requestApproval"), 20);
            await Program.UntilAsync(() => ((TextBlock)window.FindName("SessionStatus")).Text == "等待确认");
            assert(auto.IsChecked == false && !((Button)window.FindName("SendButton")).IsEnabled, "external turn pauses automation and original approval blocks input");
            var approval = ownerEvents.Last(e => e.Text("method") == "item/commandExecution/requestApproval");
            assert(!ownerEvents.Any(e => e.Text("method") == "serverRequest/resolved"), "observer does not answer or reject original approval");
            var blocked = false;
            try { await owner.SendAsync(thread, "must not steer"); }
            catch (IOException) { blocked = true; }
            assert(blocked && requests == 4, "shared pre-send guard refuses active approval turn");
            await File.WriteAllTextAsync(Path.Combine(home, "window-output.txt"), output.Text);
            window.Close();
            await Program.UntilAsync(() => closed, 10);
            assert(!server.HasExited && Finals(ownerEvents).Count == 2, "closing bound window leaves original active turn and daemon alive");
            await owner.RespondAsync(approval.Part("id"), new { decision = "decline" });
            await Program.UntilAsync(() => Finals(ownerEvents).Count == 3, 20);
            assert(Finals(ownerEvents)[2].Part("turn").Text("status") == "completed", "original owner can still answer approval and complete after detach");
            assert(!Application.Current.Windows.OfType<CompletionNotice>().Any(), "detached window leaves no completion popup");
            Console.WriteLine("Shared native: select / attach / same-thread recovery / original approval / detach OK");
        }
        finally
        {
            if (window is not null && !closed) { window.Close(); await Program.UntilAsync(() => closed, 10); }
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            await owner.StopAsync();
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(home, "server.log"), await stderr + await stdout);
            await File.WriteAllTextAsync(Path.Combine(home, "events.json"), JsonSerializer.Serialize(ownerEvents, SettingsStore.JsonOptions));
            cancel.Cancel(); listener.Stop();
            try { await mock; } catch (OperationCanceledException) { } catch (SocketException) when (cancel.IsCancellationRequested) { }
        }
    }

    private static void Click(MainWindow window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static List<JsonElement> Finals(List<JsonElement> events) => events.Where(e => e.Text("method") == "turn/completed").Select(e => e.Part("params")).ToList();
    private static string ApprovalResponse()
    {
        var item = new { type = "function_call", id = "fc_probe", call_id = "call_probe", name = "exec_command",
            arguments = JsonSerializer.Serialize(new { cmd = "Write-Output 'must be declined'", sandbox_permissions = "require_escalated", justification = "Local approval-routing check; decline this request." }) };
        return Event("response.created", new { type = "response.created", response = new { id = "r_approval" } })
            + Event("response.output_item.done", new { type = "response.output_item.done", output_index = 0, item })
            + Event("response.completed", new { type = "response.completed", response = new { id = "r_approval", output = new[] { item }, usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } } });
    }
}
