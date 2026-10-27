using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexAutoContinue;
using Forms = System.Windows.Forms;

internal static class TrayCheck
{
    internal static async Task RunAsync(string scratch, Action<bool, string> assert)
    {
        var store = new SettingsStore(Path.Combine(scratch, "tray-settings.json"));
        var iconPath = Path.Combine(scratch, "live-tray-icon.ico");
        using (var output = File.Create(iconPath)) System.Drawing.SystemIcons.Information.Save(output);
        store.Save(new Settings { WorkingDirectory = scratch, AutoText = "tray continuation", FixedSeconds = 1 });
        var window = new MainWindow(store) { ShowActivated = false };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        await using var client = new CodexClient(FakeServer.Launch("target-failure"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            window.Show();
            await Program.UntilAsync(() => window.IsLoaded);
            typeof(MainWindow).GetMethod("UseClient", flags)!.Invoke(window, [client]);
            var retry = (RetryController)typeof(MainWindow).GetField("_retry", flags)!.GetValue(window)!;
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("_timer", flags)!.GetValue(window)!;
            retry.Bind(await client.StartAsync(scratch));
            ((CheckBox)window.FindName("AutoToggle")).IsChecked = true;
            var manual = (TextBox)window.FindName("ManualBox");
            var output = (TextBox)window.FindName("OutputBox");
            manual.Text = "tray initial request";
            ((Button)window.FindName("SendButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            manual.Text = "keep this draft";
            await Program.UntilAsync(() => retry.Waiting);
            var remaining = retry.Remaining;
            typeof(MainWindow).GetMethod("ChangeTrayIcon", flags)!.Invoke(window, [iconPath]);
            assert(retry.Waiting && retry.Enabled && retry.Remaining <= remaining,
                "changing the tray icon does not restart the pending automatic send");
            window.WindowState = WindowState.Minimized;
            await Program.UntilAsync(() => !window.IsVisible);
            var tray = (Forms.NotifyIcon)typeof(MainWindow).GetField("_trayIcon", flags)!.GetValue(window)!;
            var menu = tray.ContextMenuStrip!;
            var disposed = false;
            tray.Disposed += (_, _) => disposed = true;
            assert(tray.Visible && tray.Icon is not null && window.WindowState == WindowState.Minimized,
                "minimize hides the window and shows its tray icon");
            assert(ReferenceEquals(tray.Icon, typeof(MainWindow).GetField("_customTrayIcon", flags)!.GetValue(window)),
                "the shell tray uses the selected custom icon");
            assert(!closed && retry.Enabled && timer.IsEnabled, "minimize keeps the session, retry and UI timer running");

            await Program.UntilAsync(() => output.Text.Contains("本轮完成。") && !retry.Busy);
            assert(retry.ThreadId == "thread-1" && retry.TurnId == "turn-2" && retry.Attempts == 0,
                "hidden window automatically continues once in the same session");
            assert(output.Text.Contains("自动发送：") && !window.IsVisible && tray.Visible,
                "automatic continuation does not restore the hidden main window");
            var notice = Application.Current.Windows.OfType<CompletionNotice>().Single();
            assert(notice.IsVisible && !notice.IsActive, "completion notice still appears without activation while in the tray");

            menu.Items[0].PerformClick();
            await Program.UntilAsync(() => window.IsVisible);
            assert(window.WindowState == WindowState.Normal && !tray.Visible, "tray menu restores the normal window");
            assert(manual.Text == "keep this draft" && retry.Enabled && retry.ThreadId == "thread-1",
                "restoring preserves draft, session and automation");
            notice.Close();
            ((Button)window.FindName("ResetTrayIconButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(store.Load().TrayIconPath == "" && typeof(MainWindow).GetField("_customTrayIcon", flags)!.GetValue(window) is null
                && tray.Icon is not null, "reset replaces an existing tray icon with the default");
            typeof(MainWindow).GetMethod("ChangeTrayIcon", flags)!.Invoke(window, [iconPath]);
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            await Program.UntilAsync(() => !window.IsVisible);
            assert(ReferenceEquals(tray, typeof(MainWindow).GetField("_trayIcon", flags)!.GetValue(window)),
                "repeated minimization reuses a single tray icon");
            typeof(Forms.NotifyIcon).GetMethod("OnMouseDoubleClick", flags)!.Invoke(tray,
                [new Forms.MouseEventArgs(Forms.MouseButtons.Left, 2, 0, 0, 0)]);
            await Program.UntilAsync(() => window.IsVisible);
            assert(window.WindowState == WindowState.Maximized && !tray.Visible,
                "double-click restores the previous maximized state");

            window.WindowState = WindowState.Minimized;
            await Program.UntilAsync(() => !window.IsVisible);
            menu.Items[1].PerformClick();
            await Program.UntilAsync(() => closed, 15);
            assert(disposed && menu.IsDisposed && !tray.Visible,
                $"tray exit cleans up icon and menu (disposed={disposed}, menuDisposed={menu.IsDisposed}, visible={tray.Visible})");
            assert(retry.ThreadId is null && !retry.Enabled && !timer.IsEnabled,
                "tray exit follows normal session and timer cleanup");
            assert(!((TextBlock)window.FindName("FooterText")).Text.Contains("结束会话失败"),
                "tray exit stops its owned backend without a shutdown error");
            assert(!Application.Current.Windows.OfType<CompletionNotice>().Any(), "tray exit leaves no completion notice");
            assert(typeof(MainWindow).GetField("_customTrayIcon", flags)!.GetValue(window) is null, "tray exit releases the custom icon");
        }
        finally
        {
            if (!closed)
            {
                window.Close();
                await Program.UntilAsync(() => closed, 15);
            }
        }
        Console.WriteLine("Tray minimize / restore / background continuation / exit OK");
    }
}
