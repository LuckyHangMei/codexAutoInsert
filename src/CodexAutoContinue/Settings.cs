using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAutoContinue;

internal enum WaitStrategy { Fixed, Exponential, Sequence }

internal sealed record Settings
{
    public string WorkingDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string AutoText { get; init; } = "";
    public string TrayIconPath { get; init; } = "";
    public WaitStrategy Strategy { get; init; }
    public int FixedSeconds { get; init; } = 30;
    public int InitialSeconds { get; init; } = 30;
    public int MaximumBaseSeconds { get; init; } = 300;
    public string Sequence { get; init; } = "30, 60, 120";
    public bool JitterEnabled { get; init; }
    public int JitterMinimumSeconds { get; init; }
    public int JitterMaximumSeconds { get; init; } = 10;
    public int? MaximumAttempts { get; init; }

    public int[] ParseSequence()
    {
        var values = Sequence.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0 || values.Length > 1000)
            throw new ArgumentException("自定义间隔需要 1～1000 个正整数秒数。");
        return values.Select(value => int.TryParse(value, out var number) && number > 0
            ? number : throw new ArgumentException("自定义间隔只能包含正整数秒数。")).ToArray();
    }

    public void Validate(bool requireText = false)
    {
        if (WorkingDirectory is null || AutoText is null || TrayIconPath is null || Sequence is null)
            throw new ArgumentException("设置文件中有缺失的文本字段。");
        if (!Enum.IsDefined(Strategy)) throw new ArgumentException("等待策略无效。");
        if (FixedSeconds <= 0 || InitialSeconds <= 0 || MaximumBaseSeconds <= 0)
            throw new ArgumentException("等待间隔必须是正整数秒数。");
        if (MaximumBaseSeconds < InitialSeconds)
            throw new ArgumentException("指数策略的基础等待上限不能小于初始间隔。");
        _ = ParseSequence();
        if (JitterMinimumSeconds < 0 || JitterMaximumSeconds < JitterMinimumSeconds)
            throw new ArgumentException("随机延迟范围必须满足：0 ≤ 最小值 ≤ 最大值。");
        if (MaximumAttempts is <= 0) throw new ArgumentException("连续次数上限必须为正整数，或选择不限。");
        if (requireText && string.IsNullOrWhiteSpace(AutoText)) throw new ArgumentException("请先填写并保存自动发送内容。");
    }

    public TimeSpan DelayFor(long attempt, Random? random = null)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        long seconds;
        switch (Strategy)
        {
            case WaitStrategy.Exponential:
                seconds = InitialSeconds;
                // Cap before multiplying; an unlimited retry streak must not overflow.
                for (long n = 1; n < attempt && seconds < MaximumBaseSeconds; n++)
                    seconds = Math.Min(seconds * 2, MaximumBaseSeconds);
                break;
            case WaitStrategy.Sequence:
                var sequence = ParseSequence();
                seconds = sequence[(int)Math.Min(attempt - 1, sequence.Length - 1)];
                break;
            default:
                seconds = FixedSeconds;
                break;
        }
        if (JitterEnabled)
            seconds += (random ?? Random.Shared).NextInt64(JitterMinimumSeconds, (long)JitterMaximumSeconds + 1);
        return TimeSpan.FromSeconds(seconds);
    }
}

internal sealed class SettingsStore(string? path = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAutoContinue", "settings.json");

    public Settings Load()
    {
        if (!File.Exists(FilePath)) return new Settings();
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions)
            ?? throw new InvalidDataException("设置文件为空。");
        settings.Validate();
        return settings;
    }

    public void Save(Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(FilePath))
            File.Replace(temporary, FilePath, FilePath + ".bak", true);
        else
            File.Move(temporary, FilePath);
    }
}
