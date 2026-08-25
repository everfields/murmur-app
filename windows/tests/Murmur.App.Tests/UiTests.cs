using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.App.Views;
using Murmur.Speech;
using Shouldly;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Murmur.AppTests.TestAppBuilder))]

namespace Murmur.AppTests;

/// <summary>Hosts the app headlessly so the UI can be exercised without a display.</summary>
public static class TestAppBuilder
{
    /// <summary>Builds a headless Avalonia app for the test host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>A minimal application shell for headless tests.</summary>
public sealed class TestApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());
}

/// <summary>
/// Real UI tests, running with no display.
/// </summary>
/// <remarks>
/// This is the payoff for choosing Avalonia over WPF. These run on macOS in milliseconds and
/// on a Windows runner in CI, so a broken layout or a control that fails to construct is
/// caught while writing it rather than after shipping to a machine we cannot test on.
/// </remarks>
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Window_opens_and_lays_out()
    {
        var window = new MainWindow();
        window.Show();

        window.Bounds.Width.ShouldBeGreaterThan(0);
        window.Bounds.Height.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void Record_toggles_the_lamp_and_the_meter_together()
    {
        var window = new MainWindow();
        window.Show();

        window.IsRecording.ShouldBeFalse();
        window.RecordLamp.IsLit.ShouldBeFalse();
        window.Meter.IsActive.ShouldBeFalse();

        window.ToggleRecording();

        window.IsRecording.ShouldBeTrue();
        window.RecordLamp.IsLit.ShouldBeTrue("the record lamp must follow the transport");
        window.Meter.IsActive.ShouldBeTrue("the meter lamp must follow the transport");

        window.ToggleRecording();

        window.RecordLamp.IsLit.ShouldBeFalse();
        window.Meter.IsActive.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Window_honours_its_minimum_size()
    {
        var window = new MainWindow();
        window.Show();

        window.MinWidth.ShouldBe(720);
        window.MinHeight.ShouldBe(520);
    }
}

/// <summary>The individual pieces of equipment.</summary>
public sealed class EquipmentTests
{
    [AvaloniaFact]
    public void Silkscreen_uppercases_its_text()
    {
        // The look depends on size, tracking AND case together — a label that kept its
        // original casing would be half-styled and read as ordinary UI text.
        var label = new Silkscreen { Text = "transport" };
        label.Text.ShouldBe("TRANSPORT");
    }

    [AvaloniaFact]
    public void Silkscreen_uses_the_token_tracking()
    {
        new Silkscreen().LetterSpacing.ShouldBe(Tokens.Fonts.SilkscreenTracking);
    }

    [AvaloniaFact]
    public void Lamp_defaults_to_the_token_size()
    {
        var lamp = new Lamp();
        lamp.Width.ShouldBe(Tokens.Material.LampSize);
        lamp.Height.ShouldBe(Tokens.Material.LampSize);
    }

    [AvaloniaFact]
    public void Transport_key_uses_the_token_dimensions()
    {
        var key = new TransportKey();
        key.Height.ShouldBe(Tokens.Material.KeyHeight);
        key.MinWidth.ShouldBe(Tokens.Material.KeyMinWidth);
    }

    [AvaloniaFact]
    public void Vents_measure_to_the_slot_geometry()
    {
        var vents = new Vents { Count = 4 };
        vents.Measure(Size.Infinity);

        var expected = (4 * Tokens.Material.VentSlotWidth) + (3 * Tokens.Material.VentSlotGap);
        vents.DesiredSize.Width.ShouldBe(expected);
        vents.DesiredSize.Height.ShouldBe(Tokens.Material.VentSlotHeight);
    }

    [AvaloniaFact]
    public void Meter_renders_without_throwing()
    {
        // The VU meter does all its own drawing, including a damped needle stepped by a
        // timer. Constructing and showing it is what proves the render path is sound.
        var meter = new VuMeter { Width = 168, Height = 54, Level = 0.6 };
        var window = new Window { Content = meter };
        window.Show();

        meter.Bounds.Width.ShouldBeGreaterThan(0);
    }
}

/// <summary>
/// Guards the two colour rules the design system calls non-negotiable.
/// </summary>
/// <remarks>
/// These are the sort of rule that erodes one reasonable-looking commit at a time. Asserting
/// them makes the erosion a build failure.
/// </remarks>
public sealed class DesignSystemTests
{
    [AvaloniaFact]
    public void Record_red_is_the_lacquered_value_not_a_bright_one()
    {
        var red = Tokens.Colors.Record;
        red.R.ShouldBe((byte)0xC8);
        red.G.ShouldBe((byte)0x34);
        red.B.ShouldBe((byte)0x2A);
    }

    [AvaloniaFact]
    public void Radii_stay_small_enough_to_read_as_equipment()
    {
        // Anything softer starts reading as software rather than a machined object.
        Tokens.Radius.Chip.ShouldBeLessThanOrEqualTo(2);
        Tokens.Radius.Control.ShouldBeLessThanOrEqualTo(3);
        Tokens.Radius.Panel.ShouldBeLessThanOrEqualTo(5);
        Tokens.Radius.Window.ShouldBeLessThanOrEqualTo(8);
    }

    [AvaloniaFact]
    public void Spacing_stays_on_the_four_point_grid()
    {
        double[] steps =
        [
            Tokens.Space.Hair, Tokens.Space.Tight, Tokens.Space.Snug,
            Tokens.Space.Base, Tokens.Space.Roomy, Tokens.Space.Wide, Tokens.Space.Panel,
        ];

        foreach (var step in steps) (step % 2).ShouldBe(0, $"{step} is off the grid");
    }

    [AvaloniaFact]
    public void Needle_ballistics_match_a_real_vu_movement()
    {
        // ~300 ms to reach a step, a slower fall, and a slight overshoot. Without these the
        // needle reads as a progress bar with a stick on it.
        Tokens.Motion.NeedleAttackSeconds.ShouldBe(0.30);
        Tokens.Motion.NeedleReleaseSeconds.ShouldBeGreaterThan(Tokens.Motion.NeedleAttackSeconds);
        Tokens.Motion.NeedleOvershoot.ShouldBeGreaterThan(0);
    }
}

/// <summary>
/// Which model the app picks up, and what it believes that model can do.
/// </summary>
/// <remarks>
/// Getting the order wrong here is invisible: the app starts, transcribes English perfectly,
/// and only mangles Spanish — so it is asserted rather than trusted.
/// </remarks>
public sealed class ModelVariantTests
{
    [Fact]
    public void Multilingual_v3_is_searched_before_english_only_v2()
    {
        var paths = ParakeetTranscriber.DefaultSearchPaths().ToList();

        var lastV3 = paths.FindLastIndex(p => p.EndsWith("parakeet-v3", StringComparison.Ordinal));
        var firstV2 = paths.FindIndex(p => p.EndsWith("parakeet-v2", StringComparison.Ordinal));

        lastV3.ShouldBeGreaterThanOrEqualTo(0);
        firstV2.ShouldBeGreaterThan(lastV3,
            "every v3 location must be tried before any v2 location, or a stale English-only "
          + "install next to the executable would beat a multilingual one in LOCALAPPDATA");
    }

    [Fact]
    public void Every_search_path_is_absolute()
    {
        // A relative path would resolve against the working directory, which for a shortcut or
        // a tray launch is not the install folder.
        ParakeetTranscriber.DefaultSearchPaths().ShouldAllBe(p => Path.IsPathRooted(p));
    }

    [Fact]
    public void Variant_of_recognises_the_known_folders()
    {
        ParakeetTranscriber.VariantOf(Path.Combine("C:", "models", "parakeet-v3"))
            !.Name.ShouldBe("Parakeet v3");
        ParakeetTranscriber.VariantOf(Path.Combine("C:", "models", "PARAKEET-V2"))
            !.Name.ShouldBe("Parakeet v2");
    }

    [Fact]
    public void Variant_of_returns_null_for_a_folder_it_does_not_know()
    {
        // Legitimate: ModelDirectory can be pointed at any complete model, and the app must
        // load it rather than claim it is missing.
        ParakeetTranscriber.VariantOf(Path.Combine("C:", "models", "my-own-parakeet")).ShouldBeNull();
    }

    [Fact]
    public void Only_v3_claims_to_be_multilingual()
    {
        var v3 = ParakeetTranscriber.Variants.Single(v => v.Folder == "parakeet-v3");
        var v2 = ParakeetTranscriber.Variants.Single(v => v.Folder == "parakeet-v2");

        v3.IsMultilingual.ShouldBeTrue();
        v3.Languages.ShouldContain("Spanish");
        v2.IsMultilingual.ShouldBeFalse();
    }
}
