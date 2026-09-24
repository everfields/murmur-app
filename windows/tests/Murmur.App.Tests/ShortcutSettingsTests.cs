using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Views;
using Murmur.Core;
using Shouldly;

namespace Murmur.AppTests;

public sealed class ShortcutSettingsTests
{
    [AvaloniaFact]
    public void Selecting_toggle_hold_and_off_updates_the_saved_mode_and_instructions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"murmur-shortcut-{Guid.NewGuid():N}.json");
        var settings = new AppSettings(path);
        var window = new SettingsWindow(settings);
        try
        {
            window.Show();
            var keys = window.GetVisualDescendants().OfType<TransportKey>().ToList();
            var toggle = keys.Single(k => Equals(k.Content, "WIN + SHIFT + D"));
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings.Data.UseToggleShortcut.ShouldBeTrue();
            new AppSettings(path).Data.UseToggleShortcut.ShouldBeTrue();
            toggle.IsEngaged.ShouldBeTrue();
            window.GetVisualDescendants().OfType<TextBlock>().ShouldContain(t => t.Text != null && t.Text.Contains("again to stop", StringComparison.Ordinal));
            toggle.Bounds.Right.ShouldBeLessThanOrEqualTo(window.Bounds.Width);

            keys.Single(k => Equals(k.Content, "RIGHT CTRL")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings.Data.UseToggleShortcut.ShouldBeFalse();
            settings.Data.PushToTalkKey.ShouldBe(PushToTalkKeys.RightControl);
            toggle.IsEngaged.ShouldBeFalse();

            keys.Single(k => Equals(k.Content, "OFF")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings.Data.PushToTalkKey.ShouldBe(PushToTalkKeys.None);
            settings.Data.UseToggleShortcut.ShouldBeFalse();
        }
        finally
        {
            window.Close();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
