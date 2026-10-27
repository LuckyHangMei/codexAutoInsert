# Codex 自动续发

Windows 桌面工具。可以启动一个新的 Codex 会话，也可以绑定本机共享后台中正在运行的会话，保留原会话 ID。

仅当回合**最终失败**，且最终错误包含以下任一文案时，按设置向同一个会话发送消息：

> We're currently experiencing high demand, which may cause temporary errors

> 负载已经达到上限，请稍后重试

后一类包括 anyrouter 的模型负载上限错误，不依赖模型名称或请求 ID。Codex 内置重试期间不会发送。普通对话、工具输出中的同名文字也不会触发；其他断流或 HTTP 500 错误仍不触发。

## 启动

运行发布目录中的 `CodexAutoContinue.exe`。目录中的文件需一起保留；从源码生成发布目录的命令见[开发与校验](#开发与校验)。

需要 Windows x64、.NET 10 Desktop Runtime，以及已安装并配置好的 Codex CLI。已核验 Codex CLI **0.154.0、0.164.0**，验证范围见“开发与校验”。支持 PATH 中的独立 `codex.exe` 和 npm 全局安装。

1. 选择工作目录，点击“启动会话”。登录、模型、权限沿用现有 Codex 配置。
2. 在左侧填写自动发送内容和等待策略，点击“保存并应用”，再开启“启用”。
3. 在右下角输入任务，点击“发送”或按 `Ctrl+Enter`。`Enter` 换行。

自动发送每次启动默认关闭。配置保存在 `%LOCALAPPDATA%\CodexAutoContinue\settings.json`，不写入 Codex 配置，也不保存登录凭据。

## 自定义托盘图标

在左侧设置区向下滚动到“托盘图标”，点击“选择图标”选择 `.ico` 文件。界面显示预览，选择后立即生效并保存；不重置续发倒计时，也不保存尚未应用的自动发送草稿。点击“恢复默认”可还原系统默认图标。

重启后沿用所选图标。图标引用原文件路径，请保留该文件；文件丢失或损坏时会提示并回退默认图标，其他设置不受影响。PNG、JPG 需先转换为 ICO。

## 绑定 Windows Terminal 中的已有会话

按 **Codex 会话名称、工作目录或会话 ID** 选择，不依赖 Windows Terminal 的页签标题。只列出本机共享后台中已加载、允许直接输入的主会话，不从历史记录另起一个会话。

如果目标已经接入共享后台，直接从第 3 步开始。普通启动的内嵌会话不能无缝接管，需要手动接入一次：

1. 记下原会话 ID，在原终端中正常退出 Codex。
2. 在终端运行以下命令，将同一会话接入共享后台。将示例文字替换为实际 ID：

   ```powershell
   codex app-server daemon start
   codex resume --remote unix:// "原会话ID"
   ```

3. 在小工具点击“连接已有会话”，在独立列表中按名称、目录或 ID 筛选，选择目标并点击“绑定所选会话”。连接失败时选择界面仍会保留，并显示原因和接入方法。保留原终端用于输出和审批。
4. 填写并保存续发内容、策略，再手动开启自动发送。若当前回合已最终失败且匹配目标错误，开启后开始倒计时。

绑定期间，**所有手动消息统一在小工具中发送**。原终端只查看输出、处理审批和提问。检测到其他客户端开启回合时，工具会暂停自动发送；发送前还会重新检查会话是否空闲，但 Codex 的即时发送接口不是多客户端原子锁，不支持两边同时发送。

“断开绑定”或关闭工具只断开监听，不停止原会话或共享后台。“停止生成”则会明确中断所绑定的当前回合。重新打开工具后需要重新选择会话，自动发送仍默认关闭。

工具只连接默认本地共享套接字：`%CODEX_HOME%\app-server-control\app-server-control.sock`；未设置 `CODEX_HOME` 时使用 `%USERPROFILE%\.codex`。终端和小工具必须使用同一个 `CODEX_HOME`。后台不可连接时会提示，不会自动启动、重启后台或改写 Codex 配置。

## 行为

- 固定间隔默认 30 秒；指数退避默认从 30 秒翻倍至基础上限 300 秒；自定义序列默认 `30, 60, 120`，耗尽后重复最后一项。
- 三种策略都可附加随机延迟，范围两端包含在内。默认关闭，初值 0～10 秒。指数上限不包括这段增量。
- 连续自动发送默认不限次数；可设置上限。正常完成或手动发送会清零连续次数。
- 手动或自动发送的回合正常完成后，右下角弹出“Codex 已完成回复”，10 秒后自动关闭，也可点击“知道了”。最小化时仍提示，不抢焦点；失败、内置重试和手动停止不提示。
- 保存设置会重新安排尚未发送的倒计时。启用自动发送可处理当前尚未处理的目标失败。
- 运行中可编辑草稿，但必须先“停止生成”才能手动发送。“停止生成”保留会话，并取消尚未执行的自动任务。
- 关闭自动发送只取消待发任务，不打断已经开始的回合。连接异常、非目标错误、发送结果不明或达到次数上限时暂停自动发送。
- 工具新建的会话：审批和提问显示在输出区下方，只有人工点击才会提交。绑定已有会话：审批仍在原终端处理，工具既不回答也不拒绝。
- 点击最小化会隐藏到系统托盘，不占任务栏；会话、自动发送和完成通知继续运行。双击托盘图标，或右键选择“显示主窗口”，恢复最小化前的普通或最大化窗口。
- 关闭窗口或选择托盘菜单“退出”会真正退出，只停止工具自己启动的 Codex；绑定的会话不受影响。

一次只管理一个会话。不读取终端屏幕，不模拟按键，不切换页签，不读写剪贴板。绑定时只载入最近一回合的摘要，然后显示实时输出；不会为历史成功弹出通知。界面显示约最近 100 万字符；更早的显示文本会被省略。新建会话的复杂非标准工具表单会明确报不支持，不会默认批准。

## 仓库结构

```text
src/CodexAutoContinue/           WPF 应用
tests/CodexAutoContinue.Checks/  可运行自检与模拟后端
docs/PLAN.md                     原始需求、设计与协议核验记录
.github/workflows/ci.yml         Windows 构建与普通自检
CodexAutoContinue.slnx           解决方案入口
CHANGELOG.md                    版本记录
```

`bin/`、`obj/`、`dist/` 和 `.work/` 为本地产物，不提交到仓库。用户设置和 Codex 登录凭据不属于仓库内容。

## 开发与校验

开发需要 Windows 和 .NET 10 SDK。仅使用 .NET 自带的 WPF 和 Windows Forms（托盘组件），无第三方 NuGet 依赖。以下命令在仓库根目录执行。

```powershell
dotnet build CodexAutoContinue.slnx -c Release
dotnet run --project src/CodexAutoContinue/CodexAutoContinue.csproj -c Release --no-build
dotnet run --project tests/CodexAutoContinue.Checks/CodexAutoContinue.Checks.csproj -c Release --no-build
```

自检是可执行程序，使用 `dotnet run`，不是 `dotnet test`。GitHub Actions 在 Windows 上构建解决方案并运行普通自检，不需要 Codex CLI、登录凭据或真实模型。

可选的桌面与本机 Codex 检查：

```powershell
dotnet run --project tests/CodexAutoContinue.Checks/CodexAutoContinue.Checks.csproj -c Release --no-build -- --tray
dotnet run --project tests/CodexAutoContinue.Checks/CodexAutoContinue.Checks.csproj -c Release --no-build -- --native
dotnet run --project tests/CodexAutoContinue.Checks/CodexAutoContinue.Checks.csproj -c Release --no-build -- --shared
dotnet run --project tests/CodexAutoContinue.Checks/CodexAutoContinue.Checks.csproj -c Release --no-build -- --window
```

发布为 Windows x64 文件夹：

```powershell
dotnet publish src/CodexAutoContinue/CodexAutoContinue.csproj -c Release -r win-x64 --self-contained false -o dist/win-x64
```

运行 `dist/win-x64/CodexAutoContinue.exe`，目标电脑需安装 .NET 10 Desktop Runtime。

普通自检检查调度、设置、图标选择与回退、通信、人工确认和 WPF 布局。`--native` 额外启动本机 Codex，使用隔离配置和本地模拟服务验证新建与绑定两种模式，不请求真实模型。`--shared` 只运行共享会话校验，覆盖原会话 ID、最终失败续发、原客户端审批和断开后继续运行。校验产物在 `.work\checks` 及 `.work\s*`。

`--tray` 短暂显示、最小化和恢复测试窗口，检查自定义图标与默认恢复、托盘隐藏、后台续发、通知、草稿保留、最大化恢复和退出清理。仅使用本地模拟后端，不启动真实 Codex。

`--window` 包含托盘自检，还会短暂显示并最小化测试窗口，检查最小化续发、前台窗口和剪贴板版本号。期间手动切窗或复制内容会影响该项结果；检查不读取剪贴板内容。

2026-10-27，Codex CLI 0.164.0：`--native` 共 146 项断言通过，覆盖普通自检、新建会话和共享会话联调。`--window` 的共享模式焦点保持断言两次未通过，检查期间前台切换到其他程序，原因未确定，未计为桌面验收通过。测试仅使用本地模拟服务，日志保留在 `.work/`，不提交仓库。

需求、默认值与协议核验依据见 [docs/PLAN.md](docs/PLAN.md)，版本记录见 [CHANGELOG.md](CHANGELOG.md)。
