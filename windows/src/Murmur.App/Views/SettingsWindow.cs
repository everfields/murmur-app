using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;
using Murmur.Speech;

namespace Murmur.App.Views;

/// <summary>Settings: the hotkey and the model.</summary>
public sealed class SettingsWindow : Window
{
    private const string OffNote =
        "No global key. Start and stop dictation with RECORD in the Murmur window, or from "
      + "the tray icon.";

    private const string KeyNote =
        "Hold this key anywhere to dictate. The key is passed through to the focused app "
      + "rather than swallowed, so it never gets stuck down.";

    /// <summary>
    /// The trigger options, in recommendation order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>OFF is first and is what a new install gets.</b> Every key that is comfortable to
    /// hold is also a key people press for other reasons: Right Shift was on this list and
    /// had to be withdrawn, because dictation fired on every capital letter.
    /// </para>
    /// <para>
    /// Right Alt is included but listed last and carries a warning: on German, Polish, UK,
    /// Nordic and most Latin-American layouts it is AltGr, and binding push-to-talk there
    /// breaks typing <c>@</c>, <c>€</c>, <c>\</c> and <c>|</c>.
    /// </para>
    /// </remarks>
    private static readonly (int Key, string Label, string? Warning)[] Keys =
    [
        (PushToTalkKeys.None, "OFF", null),
        (PushToTalkKeys.RightControl, "RIGHT CTRL", null),
        (PushToTalkKeys.CapsLock, "CAPS LOCK", null),
        (PushToTalkKeys.F13, "F13", null),
        (PushToTalkKeys.RightAlt, "RIGHT ALT", "Right Alt is AltGr on many European layouts — "
                                             + "binding it here will interfere with typing @, €, \\ and |."),
    ];

    private readonly AppSettings _settings;
    private readonly StackPanel _keyRow;
    private readonly TextBlock _keyWarning;
    private readonly TextBlock _keyNote;

    /// <summary>Builds the settings window.</summary>
    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "Murmur Settings";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = Tokens.Brushes.Chassis;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _keyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
        };

        _keyWarning = new TextBlock
        {
            FontFamily = Tokens.Fonts.Grotesque,
            FontSize = Tokens.Fonts.Label,
            Foreground = new SolidColorBrush(Tokens.Colors.MeterAmber),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        _keyNote = Note(OffNote);

        foreach (var (key, label, warning) in Keys)
        {
            var button = new TransportKey { Content = label, EngagedColor = Tokens.Colors.Ink };
            button.Click += (_, _) => SelectKey(key, warning);
            _keyRow.Children.Add(button);
        }

        Content = BuildContent();
        SelectKey(_settings.Data.PushToTalkKey, WarningFor(_settings.Data.PushToTalkKey));
    }

    private static string? WarningFor(int key) =>
        Keys.FirstOrDefault(k => k.Key == key).Warning;

    private StackPanel BuildContent() => new StackPanel
    {
        Margin = new Thickness(Tokens.Space.Panel),
        Spacing = Tokens.Space.Wide,
        Children =
        {
            Section("PUSH TO TALK", new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    _keyRow,
                    _keyWarning,
                    _keyNote,
                },
            }),

            Section("MODEL", BuildModelSection()),

            Section("BEHAVIOUR", new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    Toggle("Type transcripts into the focused app", _settings.Data.InjectText,
                        v => Save(_settings.Data with { InjectText = v })),
                    Toggle("Keep a transcript history", _settings.Data.KeepHistory,
                        v => Save(_settings.Data with { KeepHistory = v })),
                },
            }),
        },
    };

    private static StackPanel BuildModelSection()
    {
        var located = ParakeetTranscriber.Locate();
        var found = located is not null;
        var variant = located is null ? null : ParakeetTranscriber.VariantOf(located);

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new Lamp
                {
                    IsLit = found,
                    LampColor = found ? Tokens.Colors.MeterGreen : Tokens.Colors.MeterAmber,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = found ? $"{variant?.Name ?? "Parakeet"} ready" : "Model not installed",
                    FontFamily = Tokens.Fonts.Grotesque,
                    FontSize = Tokens.Fonts.Body,
                    Foreground = Tokens.Brushes.Ink,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var detail = found
            // Showing the resolved path matters: "model not found" is unactionable without
            // knowing which directory was actually checked. The languages line matters for the
            // same reason: which model is installed decides what the user is allowed to say.
            ? Note(variant is null
                ? $"Loaded from {located}"
                : $"Transcribes {variant.Languages}.\nLoaded from {located}")
            : Note("Windows has no built-in speech engine equivalent to Apple's, so Murmur "
                 + "cannot transcribe until the Parakeet model is downloaded (~661 MB). "
                 + "See docs/PARAKEET-WINDOWS.md. Expected in:\n"
                 + string.Join("\n", ParakeetTranscriber.DefaultSearchPaths()));

        var section = new StackPanel { Spacing = Tokens.Space.Snug, Children = { status, detail } };

        // An English-only model does not refuse Spanish — it mishears it as English words, so
        // the failure looks like a bad microphone rather than a missing download. Say so before
        // the user spends an evening blaming their accent.
        if (variant is { IsMultilingual: false })
        {
            section.Children.Add(new TextBlock
            {
                Text = "Dictating in Spanish — or any language other than English — needs the "
                     + "multilingual Parakeet v3 model. Install it to "
                     + Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Murmur", "models", "parakeet-v3")
                     + " and it will be picked up in preference to this one.",
                FontFamily = Tokens.Fonts.Grotesque,
                FontSize = Tokens.Fonts.Label,
                Foreground = new SolidColorBrush(Tokens.Colors.MeterAmber),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return section;
    }

    private void SelectKey(int key, string? warning)
    {
        for (var i = 0; i < Keys.Length; i++)
        {
            ((TransportKey)_keyRow.Children[i]).IsEngaged = Keys[i].Key == key;
        }

        _keyWarning.Text = warning ?? string.Empty;
        _keyWarning.IsVisible = warning is not null;

        // "Hold this key anywhere to dictate" under a row where OFF is engaged would be a
        // straightforward lie about how to start recording.
        _keyNote.Text = key == PushToTalkKeys.None ? OffNote : KeyNote;

        if (_settings.Data.PushToTalkKey != key) Save(_settings.Data with { PushToTalkKey = key });
    }

    private void Save(SettingsData data) => _settings.Update(data);

    private static BrushedPanel Section(string label, Control content) => new BrushedPanel
    {
        Child = new StackPanel
        {
            Margin = new Thickness(Tokens.Space.Roomy),
            Spacing = Tokens.Space.Base,
            Children = { new Silkscreen { Text = label, IsLarge = true }, content },
        },
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Grotesque,
        FontSize = Tokens.Fonts.Label,
        Foreground = new SolidColorBrush(Tokens.Colors.InkSecondary),
        TextWrapping = TextWrapping.Wrap,
    };

    private static CheckBox Toggle(string label, bool value, Action<bool> onChange)
    {
        var box = new CheckBox
        {
            IsChecked = value,
            Content = new TextBlock
            {
                Text = label,
                FontFamily = Tokens.Fonts.Grotesque,
                FontSize = Tokens.Fonts.Body,
                Foreground = Tokens.Brushes.Ink,
            },
        };

        box.IsCheckedChanged += (_, _) => onChange(box.IsChecked ?? false);
        return box;
    }
}
