using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CodexAutoContinue;

internal static class FakeServer
{
    internal static ProcessStartInfo Launch(string mode = "normal")
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("--fake-server");
        info.ArgumentList.Add(mode);
        return info;
    }

    internal static async Task RunAsync(string mode)
    {
        Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
        var turn = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            var id = message.Part("id");
            switch (message.Text("method"))
            {
                case "initialize": await Reply(id, new { }); break;
                case "thread/start": await Reply(id, new { thread = new { id = "thread-1" } }); break;
                case "turn/start":
                    var turnId = "turn-" + ++turn;
                    await Emit("turn/started", new { threadId = "thread-1", turn = new { id = turnId, status = "inProgress" } });
                    await Emit("item/agentMessage/delta", new { threadId = "thread-1", turnId, itemId = "item-" + turn,
                        delta = message.Part("params").Part("input")[0].Text("text") });
                    var failed = mode == "target-failure" && turn == 1;
                    await Emit("turn/completed", new { threadId = "thread-1", turn = new { id = turnId,
                        status = failed ? "failed" : "completed", error = failed ? new { message = RetryController.TargetMessage } : null } });
                    await Reply(id, new { turn = new { id = turnId } });
                    break;
                case "turn/interrupt": await Reply(id, new { }); break;
                case "checks/approval":
                    await Write(new { id = "approval-中文", method = "item/commandExecution/requestApproval", @params = new
                    { threadId = "thread-1", turnId = "turn-1", itemId = "item-command", command = "echo test", cwd = "." } });
                    await Reply(id, new { });
                    break;
                case "checks/close": return;
                case "checks/malformed": await Console.Out.WriteLineAsync("not JSON"); break;
                case "checks/hang": break;
                case null when id.ValueKind == JsonValueKind.String:
                    await Emit("checks/replied", new { id, result = message.Part("result") });
                    break;
            }
        }
        if (mode == "linger") await Task.Delay(Timeout.Infinite);
    }

    private static Task Reply(JsonElement id, object result) => Write(new { id, result });
    private static Task Emit(string method, object parameters) => Write(new { method, @params = parameters });
    private static async Task Write(object value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
        await Console.Out.FlushAsync();
    }
}
