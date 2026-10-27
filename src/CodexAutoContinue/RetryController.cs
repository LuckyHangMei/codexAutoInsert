using System.Diagnostics;
using System.Text.Json;

namespace CodexAutoContinue;

// State is owned by the UI dispatcher. Transport callbacks must be marshalled to it.
internal sealed class RetryController(Func<string, string, Task<string>> send)
{
    internal const string TargetMessage = "We're currently experiencing high demand, which may cause temporary errors";
    private Settings _settings = new();
    private CancellationTokenSource? _wait;
    private long _waitStarted;
    private TimeSpan _waitDuration;
    private string? _failureTurn;
    private string? _completedTurn;
    private string? _completedStatus;
    private long _sessionVersion;
    private bool _sending;
    private int _requestCount;
    private bool _stopRequested;

    public event Action? Changed;
    public event Action<string>? Notice;
    public event Action<string, bool>? MessageAccepted;
    public event Action<string, bool>? MessageSubmitting;
    public event Action? TurnSucceeded;
    public string? ThreadId { get; private set; }
    public string? TurnId { get; private set; }
    public bool Enabled { get; private set; }
    public bool Busy { get; private set; }
    public bool Sending => _sending;
    public long Attempts { get; private set; }
    public bool Waiting => _wait is not null;
    public bool CanSend => ThreadId is not null && !Busy && !_sending && _requestCount == 0;
    public string Status { get; private set; } = "自动发送未开启";
    public TimeSpan Remaining => _wait is null ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Max(0, (_waitDuration - Stopwatch.GetElapsedTime(_waitStarted)).Ticks));

    public void Bind(string threadId, JsonElement latestTurn = default)
    {
        Disconnect();
        ThreadId = threadId;
        if (latestTurn.Text("id") is { } id)
        {
            TurnId = id;
            Busy = true;
            if (latestTurn.Text("status") != "inProgress")
                Handle("turn/completed", JsonSerializer.SerializeToElement(new { threadId, turn = latestTurn }), notifySuccess: false);
        }
        Changed?.Invoke();
    }

    public void Disconnect()
    {
        _sessionVersion++;
        CancelWait();
        ThreadId = TurnId = _failureTurn = _completedTurn = _completedStatus = null;
        Busy = Enabled = _sending = false;
        _stopRequested = false;
        Attempts = 0;
        _requestCount = 0;
        Status = "自动发送未开启";
        Changed?.Invoke();
    }

    public void Apply(Settings settings)
    {
        settings.Validate(Enabled);
        CancelWait();
        _settings = settings;
        ScheduleIfEligible();
        Changed?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (ThreadId is null) throw new InvalidOperationException("请先启动会话。");
            _settings.Validate(true);
        }
        Enabled = enabled;
        CancelWait();
        Status = enabled ? "监听当前会话的最终失败" : "自动发送未开启";
        ScheduleIfEligible();
        Changed?.Invoke();
    }

    public void Pause(string reason)
    {
        Enabled = false;
        CancelWait();
        Status = reason;
        Changed?.Invoke();
    }

    public void SetPendingRequests(int count)
    {
        _requestCount = count;
        if (count > 0) CancelWait();
        else ScheduleIfEligible();
        Changed?.Invoke();
    }

    public void StopPending()
    {
        CancelWait();
        _failureTurn = null;
        _stopRequested = Busy || _sending;
        Status = Enabled ? "已取消待发任务，继续监听后续回合" : "自动发送未开启";
        Changed?.Invoke();
    }

    public void Handle(string method, JsonElement parameters, bool notifySuccess = true)
    {
        if (parameters.Text("threadId") != ThreadId || ThreadId is null) return;
        if (method == "turn/started")
        {
            var id = parameters.Part("turn").Text("id");
            if (id is null || id == _completedTurn) return;
            CancelWait();
            TurnId = id;
            Busy = true;
            _failureTurn = null;
        }
        else if (method == "turn/completed")
        {
            var turn = parameters.Part("turn");
            var id = turn.Text("id");
            if (id is null || id != TurnId || id == _completedTurn) return;
            _completedTurn = id;
            _completedStatus = turn.Text("status");
            Busy = false;
            CancelWait();
            _failureTurn = null;
            switch (_completedStatus)
            {
                case "completed":
                    Attempts = 0;
                    Status = Enabled ? "本轮已完成，连续次数已清零" : "自动发送未开启";
                    if (!_stopRequested && notifySuccess) TurnSucceeded?.Invoke();
                    break;
                case "failed" when _stopRequested:
                    Status = "已停止，不再续发本回合";
                    break;
                case "failed" when IsTargetFailure(turn.Part("error")):
                    _failureTurn = id;
                    Status = Enabled ? "已确认目标错误" : "目标错误已出现，自动发送未开启";
                    ScheduleIfEligible();
                    break;
                case "failed":
                    Pause("其他错误，自动发送已暂停");
                    break;
                default:
                    Status = Enabled ? "本轮已停止，等待手动输入" : "自动发送未开启";
                    break;
            }
        }
        // An error notification, even willRetry=false, never schedules a send.
        Changed?.Invoke();
    }

    public static bool IsTargetFailure(JsonElement error) =>
        error.Text("message") is { } message &&
        (message.Replace('\u2019', '\'').Contains(TargetMessage, StringComparison.Ordinal)
            || message.Contains("负载已经达到上限，请稍后重试", StringComparison.Ordinal));

    public async Task SendManualAsync(string text)
    {
        if (!CanSend) throw new InvalidOperationException("当前回合仍在运行或等待确认，请先停止或处理确认。");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入消息。");
        CancelWait();
        _failureTurn = null;
        Attempts = 0;
        await SendAsync(text, false);
    }

    private bool Eligible => Enabled && CanSend && _failureTurn is not null && _failureTurn == TurnId;

    private void ScheduleIfEligible()
    {
        if (_wait is not null || !Eligible) return;
        if (_settings.MaximumAttempts is int maximum && Attempts >= maximum)
        {
            Pause("已达到连续次数上限，自动发送已暂停");
            return;
        }
        _waitDuration = _settings.DelayFor(Attempts == long.MaxValue ? long.MaxValue : Attempts + 1);
        _waitStarted = Stopwatch.GetTimestamp();
        var cancellation = new CancellationTokenSource();
        _wait = cancellation;
        Status = "等待自动发送";
        Notice?.Invoke($"目标回合已失败，{_waitDuration.TotalSeconds:0} 秒后自动发送。");
        _ = WaitAndSendAsync(cancellation, _failureTurn!, _settings.AutoText, _sessionVersion);
    }

    private async Task WaitAndSendAsync(CancellationTokenSource cancellation, string failedTurn, string text, long version)
    {
        var token = cancellation.Token;
        try
        {
            // Chunk very long user-specified waits to stay inside Task.Delay's timer limit.
            while (!token.IsCancellationRequested && ReferenceEquals(_wait, cancellation) && Remaining > TimeSpan.Zero)
                await Task.Delay(Remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : Remaining, token);
            if (token.IsCancellationRequested || version != _sessionVersion || !ReferenceEquals(_wait, cancellation)
                || !Eligible || _failureTurn != failedTurn) return;
            CancelWait();
            _failureTurn = null;
            await SendAsync(text, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (version != _sessionVersion) return;
            Pause("发送结果无法确认，自动发送已暂停");
            Notice?.Invoke(error.Message);
        }
    }

    private async Task SendAsync(string text, bool automatic)
    {
        var threadId = ThreadId ?? throw new InvalidOperationException("会话已结束。");
        var version = _sessionVersion;
        var previousTurn = TurnId;
        TurnId = null;
        _sending = Busy = true;
        _stopRequested = false;
        MessageSubmitting?.Invoke(text, automatic);
        Changed?.Invoke();
        try
        {
            var acceptedTurn = await send(threadId, text);
            if (version != _sessionVersion) return;
            if (TurnId is null || TurnId == previousTurn) TurnId = acceptedTurn;
            if (automatic && Attempts < long.MaxValue) Attempts++;
            // A very fast final event may precede the RPC acknowledgment.
            if (_completedTurn == acceptedTurn && _completedStatus == "completed") Attempts = 0;
            MessageAccepted?.Invoke(text, automatic);
        }
        catch
        {
            if (version == _sessionVersion)
            {
                // Keep Busy: an unanswered request may already be executing remotely.
                Pause("发送结果无法确认，请停止或结束会话后再操作");
            }
            throw;
        }
        finally
        {
            if (version == _sessionVersion)
            {
                _sending = false;
                ScheduleIfEligible();
                Changed?.Invoke();
            }
        }
    }

    private void CancelWait()
    {
        var old = _wait;
        _wait = null;
        old?.Cancel();
        old?.Dispose();
    }
}

internal static class JsonFields
{
    public static string IdKey(this JsonElement id) => id.ValueKind == JsonValueKind.String
        ? "s:" + id.GetString() : "n:" + id.GetRawText();
    public static JsonElement Part(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    public static string? Text(this JsonElement element, string name) =>
        element.Part(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
