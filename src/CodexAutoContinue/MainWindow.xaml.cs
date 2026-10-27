using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CodexAutoContinue;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private readonly RetryController _retry;
    private readonly DispatcherTimer _timer;
    private readonly StringBuilder _output = new();
    private readonly Dictionary<string, RequestCard> _requests = new();
    private readonly Dictionary<string, JsonElement> _items = new();
    private readonly HashSet<string> _streamed = new();
    private Settings _settings = new();
    private CodexClient? _client;
    private List<CodexSession> _sessions = [];
    private List<JsonElement>? _attachBuffer;
    private readonly HashSet<string> _sharedRequests = [];
    private bool _sharedActive, _selectingSession;
    private CompletionNotice? _completionNotice;
    private readonly Container _trayComponents = new();
    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _customTrayIcon;
    private WindowState _restoreState = WindowState.Normal;
    private Task? _endTask;
    private string? _lastOutputItem;
    private bool _starting, _ending, _stoppingTurn, _closing, _closed, _syncing, _loadFailed;

    public MainWindow() : this(new SettingsStore()) { }

    internal MainWindow(SettingsStore store)
    {
        _store = store;
        InitializeComponent();
        _retry = new RetryController((thread, text) =>
            (_client ?? throw new IOException("会话已结束。")).SendAsync(thread, text));
        _retry.Changed += Refresh;
        _retry.Notice += Log;
        _retry.TurnSucceeded += ShowCompletionNotice;
        _retry.MessageSubmitting += (text, automatic) =>
        {
            _completionNotice?.Close();
            Log($"{(automatic ? "自动发送" : "你")}：\n{text}");
        };
        try { _settings = _store.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _loadFailed = true;
            Report("设置未能读取，原文件已保留。点击“保存并应用”可写入当前设置。" + error.Message);
        }
        LoadFields();
        try
        {
            using var icon = LoadTrayIcon(_settings.TrayIconPath);
            ApplyTrayIcon(icon, CreateTrayIconPreview(icon));
        }
        catch (Exception error)
        {
            ApplyTrayIcon(null, CreateTrayIconPreview(null));
            Report("托盘图标无法读取，已使用默认图标：" + error.Message);
        }
        _retry.Apply(_settings);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => { FlushOutput(); RefreshCountdown(); }, Dispatcher);
        Refresh();
    }

    private void LoadFields()
    {
        DirectoryBox.Text = _settings.WorkingDirectory;
        AutoTextBox.Text = _settings.AutoText;
        StrategyBox.SelectedIndex = (int)_settings.Strategy;
        FixedBox.Text = _settings.FixedSeconds.ToString();
        InitialBox.Text = _settings.InitialSeconds.ToString();
        MaximumBaseBox.Text = _settings.MaximumBaseSeconds.ToString();
        SequenceBox.Text = _settings.Sequence;
        JitterToggle.IsChecked = _settings.JitterEnabled;
        JitterMinBox.Text = _settings.JitterMinimumSeconds.ToString();
        JitterMaxBox.Text = _settings.JitterMaximumSeconds.ToString();
        UnlimitedToggle.IsChecked = _settings.MaximumAttempts is null;
        LimitBox.Text = (_settings.MaximumAttempts ?? 3).ToString();
        TrayIconPathBox.Text = string.IsNullOrEmpty(_settings.TrayIconPath) ? "默认图标" : _settings.TrayIconPath;
        RefreshFields();
    }

    private static int ReadNumber(TextBox box, string label, bool zeroAllowed = false) =>
        int.TryParse(box.Text, out var number) && number >= (zeroAllowed ? 0 : 1)
            ? number : throw new ArgumentException($"{label}必须为{(zeroAllowed ? "非负" : "正")}整数。最大值为 {int.MaxValue}。");

    private Settings ReadSettings()
    {
        var directory = Path.GetFullPath(DirectoryBox.Text.Trim());
        if (!Directory.Exists(directory)) throw new ArgumentException("工作目录不存在。");
        var settings = new Settings
        {
            WorkingDirectory = directory,
            AutoText = AutoTextBox.Text,
            TrayIconPath = _settings.TrayIconPath,
            Strategy = (WaitStrategy)StrategyBox.SelectedIndex,
            FixedSeconds = ReadNumber(FixedBox, "固定间隔"),
            InitialSeconds = ReadNumber(InitialBox, "初始间隔"),
            MaximumBaseSeconds = ReadNumber(MaximumBaseBox, "基础等待上限"),
            Sequence = SequenceBox.Text,
            JitterEnabled = JitterToggle.IsChecked == true,
            JitterMinimumSeconds = ReadNumber(JitterMinBox, "随机延迟最小值", true),
            JitterMaximumSeconds = ReadNumber(JitterMaxBox, "随机延迟最大值", true),
            MaximumAttempts = UnlimitedToggle.IsChecked == true ? null : ReadNumber(LimitBox, "连续次数上限")
        };
        settings.Validate(_retry.Enabled);
        return settings;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettings();
            _store.Save(settings);
            _settings = settings;
            _loadFailed = false;
            _retry.Apply(settings);
            FooterText.Text = "设置已保存并应用。";
        }
        catch (Exception error) { Report("未保存：" + error.Message); }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Codex 工作目录", Multiselect = false };
        if (Directory.Exists(DirectoryBox.Text)) dialog.InitialDirectory = DirectoryBox.Text;
        if (dialog.ShowDialog(this) == true) DirectoryBox.Text = dialog.FolderName;
    }

    private void ChooseTrayIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择托盘图标", Filter = "图标文件 (*.ico)|*.ico", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ChangeTrayIcon(dialog.FileName);
    }

    private void ResetTrayIcon_Click(object sender, RoutedEventArgs e) => ChangeTrayIcon("");

    private static Drawing.Icon? LoadTrayIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 .ico 格式的图标文件。");
        return new Drawing.Icon(path);
    }

    private static BitmapSource CreateTrayIconPreview(Drawing.Icon? icon)
    {
        var preview = Imaging.CreateBitmapSourceFromHIcon((icon ?? Drawing.SystemIcons.Application).Handle,
            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        preview.Freeze();
        return preview;
    }

    private void ApplyTrayIcon(Drawing.Icon? icon, BitmapSource preview)
    {
        var replacement = (Drawing.Icon?)icon?.Clone();
        if (_trayIcon is not null) _trayIcon.Icon = replacement ?? Drawing.SystemIcons.Application;
        var previous = _customTrayIcon;
        _customTrayIcon = replacement;
        TrayIconPreview.Source = preview;
        previous?.Dispose();
    }

    private void ChangeTrayIcon(string path)
    {
        try
        {
            if (_loadFailed) throw new InvalidOperationException("请先修复设置文件，或点击“保存并应用”，再修改托盘图标。");
            using var icon = LoadTrayIcon(path);
            var preview = CreateTrayIconPreview(icon);
            var settings = _settings with { TrayIconPath = path };
            _store.Save(settings);
            ApplyTrayIcon(icon, preview);
            _settings = settings;
            TrayIconPathBox.Text = string.IsNullOrEmpty(path) ? "默认图标" : path;
            FooterText.Text = "托盘图标已保存并应用。";
        }
        catch (Exception error) { Report("托盘图标未更改：" + error.Message); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not null || _starting || _ending || _closing) return;
        _selectingSession = false;
        _starting = true;
        Refresh();
        CodexClient? client = null;
        try
        {
            var directory = Path.GetFullPath(DirectoryBox.Text.Trim());
            var launch = CodexLaunch.ForDirectory(directory);
            _settings = _settings with { WorkingDirectory = directory };
            if (!_loadFailed) _store.Save(_settings);
            DirectoryBox.Text = directory;
            client = new CodexClient(launch);
            UseClient(client);
            var thread = await client.StartAsync(directory);
            if (_client != client || _closing) return;
            _retry.Bind(thread);
            _retry.Apply(_settings);
            Log("新会话已启动。");
            FooterText.Text = $"会话 {thread} · 最小化到托盘后继续运行，关闭窗口退出。";
        }
        catch (Exception error)
        {
            if (client is null || _client == client)
            {
                Report("启动失败：" + error.Message);
                await EndSessionAsync();
            }
        }
        finally { _starting = false; Refresh(); }
    }

    private void UseClient(CodexClient client)
    {
        _output.Clear();
        OutputBox.Clear();
        _items.Clear();
        _streamed.Clear();
        _lastOutputItem = null;
        _client = client;
        client.Message += message => Post(client, () =>
        {
            if (_attachBuffer is not null) _attachBuffer.Add(message);
            else OnMessage(message);
        });
        client.Diagnostic += message => Post(client, () => Log("Codex：" + message));
        client.Disconnected += reason => Post(client, () => OnDisconnected(reason));
        Refresh();
    }

    private async void ConnectExisting_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not null || _starting || _ending || _closing) return;
        _selectingSession = true;
        _starting = true;
        FooterText.Text = "正在连接共享后台并读取会话列表…";
        var client = CodexClient.Shared();
        UseClient(client);
        try
        {
            await client.ConnectAsync();
            var sessions = await client.ListSessionsAsync();
            if (_client != client || _closing) return;
            _sessions = sessions;
            FilterSessions();
            FooterText.Text = sessions.Count == 0 ? "共享后台中没有可绑定的会话。请将目标 CLI 会话接入后刷新。" : "请选择一个会话并点击“绑定所选会话”。";
        }
        catch (Exception error)
        {
            if (_client == client) { Report(error.Message); await EndSessionAsync(); }
        }
        finally { _starting = false; Refresh(); }
    }

    private async void RefreshSessions_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { IsShared: true } client || _retry.ThreadId is not null || _starting) return;
        _starting = true;
        Refresh();
        try
        {
            var sessions = await client.ListSessionsAsync();
            if (_client != client || _closing) return;
            _sessions = sessions;
            FilterSessions();
            FooterText.Text = sessions.Count == 0 ? "共享后台中没有可绑定的会话。" : $"已找到 {sessions.Count} 个会话。";
        }
        catch (Exception error) { if (_client == client) Report(error.Message); }
        finally { _starting = false; Refresh(); }
    }

    private void SessionFilter_Changed(object sender, TextChangedEventArgs e) => FilterSessions();
    private void Sessions_Changed(object sender, SelectionChangedEventArgs e) { if (_retry is not null) Refresh(); }
    private void FilterSessions()
    {
        if (SessionsBox is null) return;
        var selected = (SessionsBox.SelectedItem as CodexSession)?.Id;
        var matches = _sessions.Where(s => s.Matches(SessionFilter.Text.Trim())).ToList();
        SessionsBox.ItemsSource = matches;
        SessionsBox.SelectedItem = matches.FirstOrDefault(s => s.Id == selected) ?? matches.FirstOrDefault();
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { IsShared: true } client || SessionsBox.SelectedItem is not CodexSession selected || _starting) return;
        _starting = true;
        _attachBuffer = [];
        Refresh();
        try
        {
            var response = await client.AttachAsync(selected.Id);
            if (_client != client || _closing) return;
            var thread = response.Part("thread");
            if (thread.Text("id") != selected.Id) throw new IOException("返回的会话 ID 与所选目标不一致。");
            DirectoryBox.Text = thread.Text("cwd") ?? selected.Directory;
            _settings = _settings with { WorkingDirectory = DirectoryBox.Text };
            if (!_loadFailed) _store.Save(_settings);
            var page = response.Part("initialTurnsPage").Part("data");
            var latest = page.ValueKind == JsonValueKind.Array ? page.EnumerateArray().FirstOrDefault() : default;
            _retry.Bind(selected.Id, latest);
            _retry.Apply(_settings);
            SetSharedStatus(thread.Part("status"));
            Log($"已绑定：{selected.Name}\n会话 ID：{selected.Id}\n手动发送请在本工具中操作；审批仍在原终端处理。");
            if (latest.Part("items").ValueKind == JsonValueKind.Array)
                foreach (var item in latest.Part("items").EnumerateArray()) RenderItem(item, completed: true);
            if (latest.Text("status") == "failed") Log("当前回合失败：" + latest.Part("error").Text("message"));
            var buffered = _attachBuffer;
            _attachBuffer = null;
            foreach (var message in buffered ?? []) OnMessage(message);
            FooterText.Text = $"已绑定 {selected.Id} · 关闭工具只断开绑定，不停止原会话。";
        }
        catch (Exception error)
        {
            if (_client == client) { Report("绑定失败：" + error.Message); await EndSessionAsync(); }
        }
        finally { _attachBuffer = null; _starting = false; Refresh(); }
    }

    private void SetSharedStatus(JsonElement status)
    {
        _sharedActive = status.Text("type") is not ("idle" or "systemError");
        UpdateSharedRequests();
    }

    private void UpdateSharedRequests() => _retry.SetPendingRequests(_sharedRequests.Count + (_sharedActive ? 1 : 0));

    private void Post(CodexClient client, Action action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_client == client && !_closing) action();
        }, DispatcherPriority.Normal);
    }

    private async void OnDisconnected(string reason)
    {
        _retry.Pause("连接中断，自动发送已暂停");
        Report(reason);
        await EndSessionAsync();
    }

    private async void End_Click(object sender, RoutedEventArgs e)
    {
        _selectingSession = false;
        await EndSessionAsync();
    }

    private Task EndSessionAsync()
    {
        if (_endTask is { IsCompleted: false }) return _endTask;
        return _endTask = EndSessionCoreAsync();
    }

    private async Task EndSessionCoreAsync()
    {
        _ending = true;
        _completionNotice?.Close();
        var client = _client;
        var thread = _retry.ThreadId;
        var turn = _retry.Busy ? _retry.TurnId : null;
        _client = null; // Queued callbacks from this process can no longer alter the window.
        _attachBuffer = null;
        _sharedRequests.Clear();
        _sharedActive = false;
        _sessions.Clear();
        FilterSessions();
        _retry.Disconnect();
        ClearRequests();
        _items.Clear();
        _streamed.Clear();
        Refresh();
        try
        {
            if (client is not null)
            {
                if (!client.IsShared && thread is not null && turn is not null)
                {
                    try { await client.InterruptAsync(thread, turn).WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception) { /* Closing stdin and the owned process is the fallback. */ }
                }
                await client.StopAsync();
                Log(client.IsShared ? "已断开绑定，原会话继续运行。" : "会话已结束。");
            }
        }
        catch (Exception error) { Report("结束会话失败：" + error.Message); }
        finally { _ending = false; Refresh(); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendManualAsync();

    private async Task SendManualAsync()
    {
        if (!_retry.CanSend || _closing || _ending) return;
        var text = ManualBox.Text;
        var client = _client;
        try
        {
            await _retry.SendManualAsync(text);
            if (_client == client && ManualBox.Text == text) ManualBox.Clear();
        }
        catch (Exception error) { Report(error.Message); }
    }

    private async void ManualBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        await SendManualAsync();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_stoppingTurn) return;
        _retry.StopPending();
        if (!_retry.Busy || _client is not { } client || _retry.ThreadId is not { } thread) return;
        _stoppingTurn = true;
        Refresh();
        try
        {
            var started = Stopwatch.GetTimestamp();
            while (_client == client && _retry.Busy && _retry.TurnId is null
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            if (_client != client || !_retry.Busy) return;
            if (_retry.TurnId is not { } turn) throw new IOException("尚未收到当前回合标识，请结束会话以停止后端。");
            await client.InterruptAsync(thread, turn);
            FooterText.Text = "已请求停止，等待当前回合结束。";
        }
        catch (Exception error) { Report("停止请求未确认：" + error.Message); }
        finally { _stoppingTurn = false; Refresh(); }
    }

    private void AutoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _retry is null) return;
        try { _retry.SetEnabled(AutoToggle.IsChecked == true); }
        catch (Exception error) { Report(error.Message); Refresh(); }
    }

    private void Strategy_Changed(object sender, SelectionChangedEventArgs e) => RefreshFields();
    private void Options_Changed(object sender, RoutedEventArgs e) => RefreshFields();
    private void Conversation_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RequestScroller is not null) RequestScroller.MaxHeight = Math.Clamp(e.NewSize.Height - 300, 70, 310);
    }

    private void RefreshFields()
    {
        if (FixedFields is null || JitterFields is null || LimitBox is null) return;
        FixedFields.Visibility = StrategyBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExponentialFields.Visibility = StrategyBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SequenceFields.Visibility = StrategyBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        JitterFields.IsEnabled = JitterToggle.IsChecked == true;
        LimitBox.IsEnabled = UnlimitedToggle.IsChecked != true;
    }

    private void Refresh()
    {
        var connected = _retry.ThreadId is not null && _client is not null && !_ending;
        StartButton.IsEnabled = _client is null && !_starting && !_ending && !_closing;
        ConnectExistingButton.IsEnabled = StartButton.IsEnabled;
        ConnectExistingButton.ToolTip = connected ? "当前窗口已有会话，请先结束或断开当前会话，再选择要绑定的会话。" : "读取共享后台会话列表，再选择要绑定的会话。";
        EndButton.IsEnabled = _client is not null && !_ending && !_closing;
        EndButton.Content = _client?.IsShared == true ? (connected ? "断开绑定" : "取消连接") : "结束会话";
        SharedHint.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        SharedHint.Text = _client?.IsShared == true
            ? "已绑定共享会话。手动消息请在本工具中发送；审批在原终端处理。断开绑定不会停止原会话。"
            : "当前窗口已有会话。要选择其他会话，请先结束当前会话；这会停止本窗口启动的 Codex。";
        SharedPicker.Visibility = _selectingSession && !connected ? Visibility.Visible : Visibility.Collapsed;
        ConversationWorkspace.Visibility = SharedPicker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SharedSetupHint.Visibility = !_starting && _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSessionsButton.IsEnabled = SessionFilter.IsEnabled = SessionsBox.IsEnabled = _client?.IsShared == true && !connected && !_starting && !_ending && !_closing;
        AttachButton.IsEnabled = RefreshSessionsButton.IsEnabled && SessionsBox.SelectedItem is CodexSession;
        DirectoryBox.IsEnabled = BrowseButton.IsEnabled = StartButton.IsEnabled;
        SendButton.IsEnabled = connected && _retry.CanSend && !_stoppingTurn;
        StopButton.IsEnabled = connected && (_retry.Busy || _retry.Waiting) && !_stoppingTurn;
        AutoToggle.IsEnabled = connected;
        _syncing = true;
        AutoToggle.IsChecked = _retry.Enabled;
        _syncing = false;
        SessionStatus.Text = _ending ? "正在结束" : _starting ? (_selectingSession ? "正在连接" : "正在启动") : !connected ? (_selectingSession ? "选择会话" : "未启动")
            : _requests.Count + _sharedRequests.Count > 0 ? "等待确认" : _retry.Busy || _sharedActive ? "正在执行" : _retry.Waiting ? "等待续发" : "可以输入";
        StatusDot.Fill = connected ? (Brush)FindResource("Accent") : Brushes.SlateGray;
        AutoStatus.Text = _retry.Status;
        AttemptsText.Text = $"本轮连续发送 {_retry.Attempts} 次";
        RequestScroller.Visibility = _requests.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshCountdown();
    }

    private void RefreshCountdown() => CountdownText.Text = _retry.Waiting
        ? $"{Math.Ceiling(_retry.Remaining.TotalSeconds):0} 秒后发送" : "未安排发送";

    private void OnMessage(JsonElement message)
    {
        var method = message.Text("method")!;
        var parameters = message.Part("params");
        if (_client?.IsShared == true)
        {
            if (_retry.ThreadId is null || parameters.Text("threadId") is { } sharedThread && sharedThread != _retry.ThreadId) return;
            if (message.Part("id").ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                // Observe requests without answering, rejecting, or taking approval control away from the CLI.
                if (_sharedRequests.Add(message.Part("id").IdKey())) Log("Codex 等待人工确认，请在原终端处理。");
                UpdateSharedRequests();
                return;
            }
            if (method == "serverRequest/resolved")
            {
                var resolvedId = parameters.Part("requestId");
                if (resolvedId.ValueKind is JsonValueKind.Number or JsonValueKind.String) _sharedRequests.Remove(resolvedId.IdKey());
                UpdateSharedRequests();
                return;
            }
            if (method == "thread/closed" || method == "thread/status/changed" && parameters.Part("status").Text("type") == "notLoaded")
            {
                OnDisconnected("目标会话已从共享后台卸载，监听已停止。");
                return;
            }
            if (method == "thread/status/changed") SetSharedStatus(parameters.Part("status"));
            if (method == "turn/started" && !_retry.Sending && parameters.Part("turn").Text("id") != _retry.TurnId && _retry.Enabled)
                _retry.Pause("检测到其他客户端开始回合，自动发送已暂停；请统一在本工具中发送。");
            if (method == "turn/completed") _sharedRequests.Clear();
        }
        if (message.Part("id").ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            HandleRequest(message);
            return;
        }
        if (parameters.Text("threadId") is { } thread && thread != _retry.ThreadId) return;
        if (method == "serverRequest/resolved")
        {
            RemoveRequest(parameters.Part("requestId"));
            return;
        }
        // Expire requests before completion is given to the scheduler.
        if (method == "turn/completed" && parameters.Part("turn").Text("id") is { } completedTurn)
            ClearRequests(completedTurn);
        if (_client?.IsShared == true) UpdateSharedRequests();
        _retry.Handle(method, parameters);
        switch (method)
        {
            case "turn/started":
                _items.Clear();
                _streamed.Clear();
                break;
            case "item/agentMessage/delta": AppendDelta(parameters, "Codex"); break;
            case "item/reasoning/summaryTextDelta": AppendDelta(parameters, "思考摘要"); break;
            case "item/plan/delta": AppendDelta(parameters, "计划"); break;
            case "item/commandExecution/outputDelta": AppendDelta(parameters, "命令输出"); break;
            case "item/fileChange/outputDelta": AppendDelta(parameters, "文件变更"); break;
            case "item/started":
            case "item/completed":
                RenderItem(parameters.Part("item"), method == "item/completed");
                break;
            case "error":
                var error = parameters.Part("error");
                var retrying = parameters.Part("willRetry").ValueKind == JsonValueKind.True;
                Log($"{(retrying ? "Codex 内置重试" : "错误")}：{error.Text("message")}\n{error.Text("additionalDetails")}".TrimEnd());
                break;
            case "turn/completed":
                var turn = parameters.Part("turn");
                Log(turn.Text("status") switch
                {
                    "completed" => "本轮完成。",
                    "interrupted" => "本轮已停止。",
                    "failed" => "本轮失败：" + turn.Part("error").Text("message"),
                    _ => "本轮结束。"
                });
                break;
            case "configWarning":
            case "warning":
                Log("提示：" + (parameters.Text("message") ?? parameters.Text("summary")));
                break;
        }
    }

    private void RenderItem(JsonElement item, bool completed)
    {
        var id = item.Text("id");
        if (id is null) return;
        var type = item.Text("type");
        if (type == "fileChange" && !completed) _items[id] = item.Clone();
        if (completed)
        {
            _items.Remove(id);
            if (!_streamed.Contains(id))
            {
                var text = type switch
                {
                    "agentMessage" or "plan" => item.Text("text"),
                    "commandExecution" => item.Text("aggregatedOutput"),
                    _ => null
                };
                if (!string.IsNullOrEmpty(text)) Log(text);
            }
            if (type == "commandExecution") Log($"命令结束：{item.Text("status")} · 退出码 {item.Part("exitCode")}");
            else if (type is "fileChange" or "mcpToolCall" or "dynamicToolCall" or "collabAgentToolCall")
                Log($"{ItemLabel(type)}：{item.Text("status")}");
            return;
        }
        switch (type)
        {
            case "commandExecution": Log($"执行命令（{item.Text("cwd")}）：\n{item.Text("command")}"); break;
            case "fileChange": Log("文件变更：\n" + RequestCard.DescribeChanges(item)); break;
            case "mcpToolCall": Log($"调用工具：{item.Text("server")}/{item.Text("tool")}"); break;
            case "dynamicToolCall":
            case "collabAgentToolCall": Log("调用工具：" + item.Text("tool")); break;
            case "webSearch": Log("正在搜索网页。"); break;
            case "contextCompaction": Log("正在整理会话上下文。"); break;
        }
    }

    private static string ItemLabel(string type) => type == "fileChange" ? "文件变更" : "工具调用";

    private void AppendDelta(JsonElement parameters, string label)
    {
        var id = parameters.Text("itemId") ?? label;
        _streamed.Add(id);
        if (_lastOutputItem != id)
        {
            _output.Append($"\n\n{label}：\n");
            _lastOutputItem = id;
        }
        _output.Append(parameters.Text("delta"));
    }

    private async void HandleRequest(JsonElement message)
    {
        var client = _client;
        if (client is null) return;
        var id = message.Part("id");
        var parameters = message.Part("params");
        var key = id.IdKey();
        if (_requests.ContainsKey(key)) return;
        try
        {
            if (parameters.Text("threadId") is { } thread && thread != _retry.ThreadId)
                throw new NotSupportedException("请求不属于当前会话。");
            _items.TryGetValue(parameters.Text("itemId") ?? "", out var item);
            var card = new RequestCard(message, item, async result =>
            {
                if (_client != client || !_requests.ContainsKey(key)) return;
                try
                {
                    await client.RespondAsync(id, result);
                    RemoveRequest(id);
                    Log("已提交人工确认。");
                }
                catch (Exception error)
                {
                    _retry.Pause("确认提交结果不明，自动发送已暂停");
                    throw new IOException("确认未能提交，请结束会话后重试。" + error.Message, error);
                }
            });
            _requests.Add(key, card);
            RequestsPanel.Children.Add(card);
            _retry.SetPendingRequests(_requests.Count);
            Log("Codex 等待人工确认，请处理输出区下方的请求。");
        }
        catch (Exception error)
        {
            _retry.Pause("请求无法处理，自动发送已暂停");
            Report(error.Message);
            try { await client.RejectUnsupportedAsync(id); }
            catch (Exception writeError) { Report(writeError.Message); }
        }
    }

    private void RemoveRequest(JsonElement id)
    {
        if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) return;
        if (_requests.Remove(id.IdKey(), out var card)) RequestsPanel.Children.Remove(card);
        _retry.SetPendingRequests(_requests.Count);
    }

    private void ClearRequests(string? turn = null)
    {
        foreach (var pair in _requests.Where(pair => turn is null || pair.Value.TurnId == turn).ToArray())
        {
            RequestsPanel.Children.Remove(pair.Value);
            _requests.Remove(pair.Key);
        }
        if (_client?.IsShared == true) UpdateSharedRequests();
        else _retry.SetPendingRequests(_requests.Count);
    }

    private void Log(string text)
    {
        _lastOutputItem = null;
        _output.Append($"\n\n[{DateTime.Now:HH:mm:ss}] {text}");
    }

    private void Report(string text)
    {
        FooterText.Text = text;
        Log(text);
    }

    private void ShowCompletionNotice()
    {
        if (_closing || _ending || !IsLoaded) return;
        _completionNotice?.Close();
        var notice = new CompletionNotice();
        _completionNotice = notice;
        notice.Closed += (_, _) =>
        {
            if (_completionNotice == notice) _completionNotice = null;
        };
        // No owner: an owned window would be hidden while the main window is minimized.
        try { notice.Show(); }
        catch (Win32Exception error) { notice.Close(); Report("完成通知未能显示：" + error.Message); }
    }

    private void FlushOutput()
    {
        if (_output.Length == 0) return;
        // ponytail: keep at most ~1 MB of visible text; add paged history only if requested.
        if (OutputBox.Text.Length + _output.Length > 1_000_000)
        {
            var tail = OutputBox.Text;
            OutputBox.Text = "[较早的显示内容已省略]\n" + tail[Math.Max(0, tail.Length - 400_000)..];
        }
        if (_output.Length > 500_000) _output.Remove(0, _output.Length - 500_000);
        OutputBox.AppendText(_output.ToString());
        _output.Clear();
        if (FollowOutputToggle.IsChecked == true) OutputBox.ScrollToEnd();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (_closing) return;
        if (WindowState != WindowState.Minimized)
        {
            _restoreState = WindowState;
            return;
        }
        if (_trayIcon is null)
        {
            var menu = new Forms.ContextMenuStrip(_trayComponents);
            menu.Items.Add("显示主窗口(&S)", null, (_, _) => RestoreFromTray());
            menu.Items.Add("退出(&X)", null, (_, _) => Close());
            _trayIcon = new Forms.NotifyIcon(_trayComponents)
            {
                Icon = _customTrayIcon ?? Drawing.SystemIcons.Application,
                Text = "Codex 自动续发（双击恢复）",
                ContextMenuStrip = menu
            };
            _trayIcon.MouseDoubleClick += (_, click) =>
            {
                if (click.Button == Forms.MouseButtons.Left) RestoreFromTray();
            };
        }
        _trayIcon.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (_closing) return;
        ShowActivated = true;
        WindowState = _restoreState;
        Show();
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_trayIcon is not null) _trayIcon.Visible = false;
        _trayComponents.Dispose();
        _customTrayIcon?.Dispose();
        _customTrayIcon = null;
        base.OnClosed(e);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        _timer.Stop();
        await EndSessionAsync();
        _closed = true;
        // EndSessionAsync can complete synchronously when no process exists.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
