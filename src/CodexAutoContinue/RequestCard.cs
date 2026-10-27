using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexAutoContinue;

// Requests stay inside the window. Only a user's click can produce an approval or answer.
internal sealed class RequestCard : Border
{
    private readonly StackPanel _body = new();
    private readonly WrapPanel _buttons = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    private readonly Func<object, Task> _respond;
    public string? TurnId { get; }

    public RequestCard(JsonElement message, JsonElement item, Func<object, Task> respond)
    {
        _respond = respond;
        var parameters = message.Part("params");
        TurnId = parameters.Text("turnId");
        Background = new SolidColorBrush(Color.FromRgb(255, 250, 235));
        BorderBrush = new SolidColorBrush(Color.FromRgb(228, 206, 150));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12);
        Margin = new Thickness(0, 0, 0, 8);
        Child = _body;

        switch (message.Text("method"))
        {
            case "item/commandExecution/requestApproval":
                Title(parameters.Text("kind") == "stdin" ? "确认命令输入" : "确认执行命令");
                Text(parameters.Text("reason"));
                Text("工作目录：" + parameters.Text("cwd"));
                Details(parameters.Text("command") ?? "未提供命令文本");
                if (parameters.Part("networkApprovalContext").ValueKind == JsonValueKind.Object)
                    Details(JsonSerializer.Serialize(parameters.Part("networkApprovalContext"), SettingsStore.JsonOptions));
                ApprovalButtons();
                break;
            case "item/fileChange/requestApproval":
                Title("确认文件变更");
                Text(parameters.Text("reason"));
                if (parameters.Text("grantRoot") is { } root) Text("请求写入目录：" + root);
                Details(DescribeChanges(item));
                ApprovalButtons();
                break;
            case "item/permissions/requestApproval":
                Title("确认额外权限");
                Text(parameters.Text("reason"));
                Text("工作目录：" + parameters.Text("cwd"));
                var permissions = parameters.Part("permissions");
                Details(JsonSerializer.Serialize(permissions, SettingsStore.JsonOptions));
                var grants = permissions.EnumerateObject()
                    .Where(p => p.Name is "network" or "fileSystem" && p.Value.ValueKind is not JsonValueKind.Null)
                    .ToDictionary(p => p.Name, p => p.Value.Clone());
                Button("允许本回合", () => new { permissions = grants, scope = "turn" });
                Button("会话内允许", () => new { permissions = grants, scope = "session" });
                Button("拒绝", () => new { permissions = new { }, scope = "turn" });
                break;
            case "item/tool/requestUserInput":
                AddQuestions(parameters);
                break;
            case "mcpServer/elicitation/request":
                AddElicitation(parameters);
                break;
            default:
                throw new NotSupportedException("当前版本无法处理 Codex 请求：" + message.Text("method"));
        }
        _body.Children.Add(_error);
        _body.Children.Add(_buttons);
    }

    internal static string DescribeChanges(JsonElement item)
    {
        var changes = item.Part("changes");
        if (changes.ValueKind != JsonValueKind.Array) return "未收到文件变更详情。";
        return string.Join("\n\n", changes.EnumerateArray().Select(change =>
            $"{change.Text("path")}\n{change.Text("diff")}"));
    }

    private void ApprovalButtons()
    {
        Button("允许本次", () => new { decision = "accept" });
        Button("会话内允许", () => new { decision = "acceptForSession" });
        Button("拒绝", () => new { decision = "decline" });
        Button("停止回合", () => new { decision = "cancel" });
    }

    private void AddQuestions(JsonElement parameters)
    {
        Title("Codex 需要你的回答");
        var readers = new Dictionary<string, Func<string>>();
        foreach (var question in parameters.Part("questions").EnumerateArray())
        {
            var id = question.Text("id") ?? throw new ArgumentException("问题缺少标识。");
            Text(question.Text("header"), true);
            Text(question.Text("question"));
            if (question.Part("isSecret").ValueKind == JsonValueKind.True)
            {
                var password = new PasswordBox { Margin = new Thickness(0, 4, 0, 8) };
                AutomationProperties.SetName(password, question.Text("question") ?? id);
                _body.Children.Add(password);
                readers.Add(id, () => password.Password);
                continue;
            }
            ComboBox? choices = null;
            if (question.Part("options") is { ValueKind: JsonValueKind.Array } options && options.GetArrayLength() > 0)
            {
                choices = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
                AutomationProperties.SetName(choices, question.Text("question") ?? id);
                foreach (var option in options.EnumerateArray())
                    choices.Items.Add(new ComboBoxItem
                    {
                        Content = new TextBlock { Text = option.Text("label") + " — " + option.Text("description"), TextWrapping = TextWrapping.Wrap },
                        Tag = option.Text("label")
                    });
                _body.Children.Add(choices);
            }
            TextBox? input = null;
            if (choices is null || question.Part("isOther").ValueKind == JsonValueKind.True)
            {
                if (choices is not null) Text("也可以填写自己的回答：");
                input = Input(question.Text("question") ?? id);
            }
            readers.Add(id, () => !string.IsNullOrWhiteSpace(input?.Text) ? input.Text
                : (choices?.SelectedItem as ComboBoxItem)?.Tag as string ?? "");
        }
        Button("提交回答", () =>
        {
            var answers = new Dictionary<string, object>();
            foreach (var (id, read) in readers)
            {
                var value = read();
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("请回答每个问题，或点击跳过。" );
                answers.Add(id, new { answers = new[] { value } });
            }
            return new { answers };
        });
        Button("跳过问题", () => new { answers = new Dictionary<string, object>() });
    }

    private void AddElicitation(JsonElement parameters)
    {
        Title("工具请求：" + parameters.Text("serverName"));
        Text(parameters.Text("message"));
        if (parameters.Text("mode") == "url")
        {
            var url = parameters.Text("url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new NotSupportedException("工具提供的链接不是 HTTP/HTTPS 地址，未打开。" );
            Details(uri.AbsoluteUri);
            var open = new Button { Content = "打开链接", Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception error) { _error.Text = error.Message; }
            };
            _buttons.Children.Add(open);
            Button("已完成", () => ElicitationResult("accept", null));
        }
        else
        {
            var schema = parameters.Part("requestedSchema");
            if (schema.Text("type") != "object" || schema.Part("properties").ValueKind != JsonValueKind.Object)
                throw new NotSupportedException("工具请求的表单结构暂不支持，未提交任何回答。" );
            var required = schema.Part("required") is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : [];
            var fields = new Dictionary<string, Func<object?>>();
            foreach (var property in schema.Part("properties").EnumerateObject())
                fields.Add(property.Name, FormField(property.Name, property.Value, required.Contains(property.Name)));
            Button("提交", () =>
            {
                var content = new Dictionary<string, object>();
                foreach (var (name, read) in fields)
                {
                    var value = read();
                    if (value is not null) content.Add(name, value);
                    else if (required.Contains(name)) throw new ArgumentException($"请填写 {name}。" );
                }
                return ElicitationResult("accept", content);
            });
        }
        Button("拒绝", () => ElicitationResult("decline", null));
        Button("取消", () => ElicitationResult("cancel", null));
    }

    private static object ElicitationResult(string action, object? content) => new { action, content, _meta = (object?)null };

    private Func<object?> FormField(string name, JsonElement schema, bool required)
    {
        var label = schema.Text("title") ?? name;
        Text(label + (required ? " *" : "（可选）"), true);
        Text(schema.Text("description"));
        var type = schema.Text("type");
        var options = Choices(type == "array" ? schema.Part("items") : schema).ToArray();
        if (options.Length > 0)
        {
            if (type == "array")
            {
                var boxes = options.Select(option => new CheckBox { Content = option.label, Tag = option.value, Margin = new Thickness(0, 4, 0, 4) }).ToArray();
                foreach (var box in boxes) _body.Children.Add(box);
                return () =>
                {
                    var selected = boxes.Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToArray();
                    if (!required && selected.Length == 0) return null;
                    CheckRange(selected.Length, schema, "minItems", "maxItems", label);
                    return selected;
                };
            }
            var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetName(combo, label);
            foreach (var option in options) combo.Items.Add(new ComboBoxItem { Content = option.label, Tag = option.value });
            _body.Children.Add(combo);
            return () => (combo.SelectedItem as ComboBoxItem)?.Tag;
        }
        if (type == "boolean")
        {
            var choice = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetName(choice, label);
            choice.Items.Add(new ComboBoxItem { Content = "是", Tag = true });
            choice.Items.Add(new ComboBoxItem { Content = "否", Tag = false });
            _body.Children.Add(choice);
            return () => (choice.SelectedItem as ComboBoxItem)?.Tag;
        }
        if (type is not ("string" or "number" or "integer"))
            throw new NotSupportedException($"暂不支持表单字段 {label} 的类型：{type}。" );
        var input = Input(label);
        if (schema.Part("default").ValueKind is JsonValueKind.String or JsonValueKind.Number)
            input.Text = schema.Part("default").ToString();
        return () =>
        {
            if (input.Text.Length == 0) return null;
            if (type == "string")
            {
                CheckRange(input.Text.EnumerateRunes().Count(), schema, "minLength", "maxLength", label);
                var valid = schema.Text("format") switch
                {
                    "email" => MailAddress.TryCreate(input.Text, out _),
                    "uri" => Uri.TryCreate(input.Text, UriKind.Absolute, out _),
                    "date" => DateOnly.TryParseExact(input.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    "date-time" => DateTimeOffset.TryParse(input.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    _ => true
                };
                if (!valid) throw new ArgumentException($"{label} 的格式不正确。" );
                return input.Text;
            }
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || !double.IsFinite(number) || (type == "integer" && Math.Truncate(number) != number))
                throw new ArgumentException($"{label} 必须为{(type == "integer" ? "整数" : "数字")}。" );
            CheckRange(number, schema, "minimum", "maximum", label);
            return number;
        };
    }

    private static IEnumerable<(string value, string label)> Choices(JsonElement schema)
    {
        if (schema.Part("enum") is { ValueKind: JsonValueKind.Array } values)
        {
            var labels = schema.Part("enumNames") is { ValueKind: JsonValueKind.Array } names
                ? names.EnumerateArray().Select(x => x.GetString()).ToArray() : [];
            var index = 0;
            foreach (var value in values.EnumerateArray())
            {
                var text = value.GetString() ?? "";
                yield return (text, index < labels.Length ? labels[index] ?? text : text);
                index++;
            }
        }
        else
        {
            var options = schema.Part("oneOf");
            if (options.ValueKind != JsonValueKind.Array) options = schema.Part("anyOf");
            if (options.ValueKind != JsonValueKind.Array) yield break;
            foreach (var option in options.EnumerateArray())
                yield return (option.Text("const") ?? "", option.Text("title") ?? option.Text("const") ?? "");
        }
    }

    private static void CheckRange(double value, JsonElement schema, string min, string max, string label)
    {
        if ((schema.Part(min) is { ValueKind: JsonValueKind.Number } lower && value < lower.GetDouble())
            || (schema.Part(max) is { ValueKind: JsonValueKind.Number } upper && value > upper.GetDouble()))
            throw new ArgumentException($"{label} 超出允许范围。" );
    }

    private void Title(string text) => _body.Children.Add(new TextBlock
        { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });

    private void Text(string? text, bool bold = false)
    {
        if (!string.IsNullOrWhiteSpace(text)) _body.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        });
    }

    private TextBox Input(string label)
    {
        var input = new TextBox { Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(input, label);
        _body.Children.Add(input);
        return input;
    }

    private void Details(string text) => _body.Children.Add(new TextBox
    {
        Text = text, IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
        MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 8)
    });

    private void Button(string text, Func<object> answer)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4) };
        button.Click += async (_, _) =>
        {
            try
            {
                var response = answer();
                IsEnabled = false;
                _error.Text = "";
                await _respond(response);
            }
            catch (Exception error) { IsEnabled = true; _error.Text = error.Message; }
        };
        _buttons.Children.Add(button);
    }
}
