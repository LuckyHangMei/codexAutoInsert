using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexAutoContinue;

internal sealed partial class CompletionNotice : Window
{
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromSeconds(10) };

    public CompletionNotice()
    {
        Title = "Codex 回复完成";
        Width = 360;
        Height = 172;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        SourceInitialized += (_, _) =>
        {
            // WPF owns this borrowed HWND. WS_EX_NOACTIVATE also covers click/close activation,
            // whereas ShowActivated only controls the initial Show operation. Windows x64/ARM64.
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(PreventMouseActivation);
            var style = GetWindowLongPtrW(handle, -20);
            if (style == 0 && Marshal.GetLastPInvokeError() is var readError && readError != 0) throw new Win32Exception(readError);
            if (SetWindowLongPtrW(handle, -20, style | 0x08000000) == 0
                && Marshal.GetLastPInvokeError() is var writeError && writeError != 0) throw new Win32Exception(writeError);
        };
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - Width - 16);
        Top = Math.Max(area.Top, area.Bottom - Height - 16);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "Codex 已完成回复", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(15, 118, 110))
        });
        body.Children.Add(new TextBlock
        {
            Text = "本轮已正常完成。\n可回到主窗口查看回复。", FontSize = 14,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12)
        });
        var footer = new DockPanel();
        var close = new Button { Content = "知道了", Padding = new Thickness(12, 5, 12, 5) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(new TextBlock
        {
            Text = "10 秒后自动关闭", FontSize = 12, Foreground = Brushes.SlateGray,
            VerticalAlignment = VerticalAlignment.Center
        });
        body.Children.Add(footer);
        Content = new Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(15, 118, 110)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18), Child = body
        };
        _dismiss.Tick += (_, _) => Close();
        Loaded += (_, _) => _dismiss.Start();
        Closed += (_, _) => _dismiss.Stop();
    }

    private static nint PreventMouseActivation(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
        handled = true;
        return 3; // MA_NOACTIVATE: deliver the click without activating the window.
    }

    // WinUser.h: LONG_PTR (HWND, int) and LONG_PTR (HWND, int, LONG_PTR).
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint window, int index);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);
}
