using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CodexAutoContinue;
using Drawing = System.Drawing;

internal static class IconCheck
{
    internal static async Task RunAsync(string scratch, Action<bool, string> assert)
    {
        var iconPath = Path.Combine(scratch, "custom-icon.ICO");
        using (var output = File.Create(iconPath)) Drawing.SystemIcons.Information.Save(output);
        var invalidPath = Path.Combine(scratch, "invalid-icon.ico");
        File.WriteAllText(invalidPath, "not an icon");
        var store = new SettingsStore(Path.Combine(scratch, "icon-settings.json"));
        var saved = new Settings { WorkingDirectory = scratch, AutoText = "preserve saved text", FixedSeconds = 17 };
        store.Save(saved);
        var windows = new List<MainWindow>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var change = typeof(MainWindow).GetMethod("ChangeTrayIcon", flags)!;
        var iconField = typeof(MainWindow).GetField("_customTrayIcon", flags)!;
        try
        {
            var window = new MainWindow(store);
            windows.Add(window);
            var preview = (Image)window.FindName("TrayIconPreview");
            assert(preview.Source is not null && iconField.GetValue(window) is null, "default tray icon has a preview");
            ((TextBox)window.FindName("AutoTextBox")).Text = "unsaved draft";
            change.Invoke(window, [iconPath]);
            var selectedIcon = iconField.GetValue(window);
            assert(selectedIcon is Drawing.Icon && preview.Source is not null && ((TextBox)window.FindName("TrayIconPathBox")).Text == iconPath,
                "custom ICO selection updates the icon, preview and displayed path");
            var content = (FrameworkElement)window.Content;
            var imagePath = Path.Combine(scratch, "custom-icon-settings.png");
            Program.Render(content, 952, 586, imagePath);
            ((Button)window.FindName("ResetTrayIconButton")).BringIntoView();
            Program.Render(content, 952, 586, imagePath);
            assert(store.Load() == saved with { TrayIconPath = iconPath }
                && ((TextBox)window.FindName("AutoTextBox")).Text == "unsaved draft",
                "icon selection saves only the icon setting and preserves unsaved text");
            using (var exclusive = File.Open(iconPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                assert(exclusive.CanWrite, "loaded icon does not lock the source file");

            foreach (var rejected in new[] { invalidPath, Path.Combine(scratch, "missing-icon.ico"), Path.Combine(scratch, "wrong-format.png") })
            {
                var before = store.Load();
                change.Invoke(window, [rejected]);
                assert(store.Load() == before && ReferenceEquals(selectedIcon, iconField.GetValue(window)),
                    "invalid or missing icon preserves settings and current icon: " + rejected);
            }
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(store.Load().TrayIconPath == iconPath && store.Load().AutoText == "unsaved draft",
                "saving automatic-send settings preserves the chosen tray icon");
            var reopened = new MainWindow(store);
            windows.Add(reopened);
            assert(iconField.GetValue(reopened) is Drawing.Icon && ((CheckBox)reopened.FindName("AutoToggle")).IsChecked == false,
                "reopening restores the custom icon without enabling automation");
            ((Button)window.FindName("ResetTrayIconButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(store.Load().TrayIconPath == "" && iconField.GetValue(window) is null && preview.Source is not null,
                "reset restores and persists the default icon");

            foreach (var unavailable in new[] { Path.Combine(scratch, "missing-icon.ico"), invalidPath })
            {
                store.Save(store.Load() with { TrayIconPath = unavailable });
                var fallback = new MainWindow(store);
                windows.Add(fallback);
                assert(iconField.GetValue(fallback) is null && ((Image)fallback.FindName("TrayIconPreview")).Source is not null
                    && ((TextBox)fallback.FindName("AutoTextBox")).Text == "unsaved draft",
                    "unavailable startup icon falls back without discarding other settings: " + unavailable);
                ((Button)fallback.FindName("ResetTrayIconButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                assert(store.Load().TrayIconPath == "", "unavailable startup icon can be reset normally");
            }

            File.WriteAllText(store.FilePath, "{broken");
            var corrupt = new MainWindow(store);
            windows.Add(corrupt);
            change.Invoke(corrupt, [iconPath]);
            assert(File.ReadAllText(store.FilePath) == "{broken" && iconField.GetValue(corrupt) is null,
                "icon selection never overwrites a corrupt settings file");
        }
        finally
        {
            foreach (var window in windows)
            {
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                await Program.UntilAsync(() => closed);
                assert(iconField.GetValue(window) is null, "window close releases its custom icon");
            }
        }
        Console.WriteLine("Custom tray icon settings / validation / fallback / cleanup OK");
    }
}
