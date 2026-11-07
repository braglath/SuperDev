using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace SuperDev.Services;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum AppAccent
{
    Blue,
    Teal,
    Green,
    Orange,
    Pink,
    Purple,
    Red
}

public enum AppDensity
{
    Comfortable,
    Compact
}

public enum AppFontSize
{
    Small,
    Medium,
    Large
}

public sealed class AppearanceSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public AppAccent Accent { get; set; } = AppAccent.Purple;
    public AppDensity Density { get; set; } = AppDensity.Comfortable;
    public AppFontSize FontSize { get; set; } = AppFontSize.Medium;
}

public static class AppearanceService
{
    public const string AppBackgroundBrush = "AppBackgroundBrush";
    public const string SurfaceBrush = "SurfaceBrush";
    public const string SurfaceInsetBrush = "SurfaceInsetBrush";
    public const string CardBrush = "CardBrush";
    public const string CardHoverBrush = "CardHoverBrush";
    public const string BorderBrush = "BorderBrush";
    public const string BorderSoftBrush = "BorderSoftBrush";
    public const string BorderSubtleBrush = "BorderSubtleBrush";
    public const string TextPrimaryBrush = "TextPrimaryBrush";
    public const string TextSecondaryBrush = "TextSecondaryBrush";
    public const string TextBodyBrush = "TextBodyBrush";
    public const string TextMutedBrush = "TextMutedBrush";
    public const string TextSubtleBrush = "TextSubtleBrush";
    public const string TextFaintBrush = "TextFaintBrush";
    public const string TextOnAccentBrush = "TextOnAccentBrush";
    public const string AccentBrush = "AccentBrush";
    public const string AccentHoverBrush = "AccentHoverBrush";
    public const string AccentPressedBrush = "AccentPressedBrush";
    public const string AccentTextBrush = "AccentTextBrush";
    public const string AccentTextHoverBrush = "AccentTextHoverBrush";
    public const string AccentSubtleBrush = "AccentSubtleBrush";
    public const string AccentSubtleHoverBrush = "AccentSubtleHoverBrush";
    public const string DangerBrush = "DangerBrush";
    public const string DangerHoverBrush = "DangerHoverBrush";
    public const string DangerTextBrush = "DangerTextBrush";
    public const string DangerTextHoverBrush = "DangerTextHoverBrush";
    public const string DangerSurfaceBrush = "DangerSurfaceBrush";
    public const string DangerSurfaceSoftBrush = "DangerSurfaceSoftBrush";
    public const string WarningTextBrush = "WarningTextBrush";
    public const string WarningSurfaceBrush = "WarningSurfaceBrush";
    public const string SuccessBrush = "SuccessBrush";
    public const string SuccessHoverBrush = "SuccessHoverBrush";
    public const string SuccessSubtleBrush = "SuccessSubtleBrush";
    public const string SuccessTextBrush = "SuccessTextBrush";
    public const string SuccessBorderBrush = "SuccessBorderBrush";
    public const string InfoTextBrush = "InfoTextBrush";
    public const string InfoSurfaceBrush = "InfoSurfaceBrush";
    public const string ReviewTextBrush = "ReviewTextBrush";
    public const string ReviewSurfaceBrush = "ReviewSurfaceBrush";

    private static readonly int[] FontSizes = [11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24, 28, 38, 92];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Dictionary<AppAccent, Color> Accents = new()
    {
        [AppAccent.Blue] = Color.Parse("#2563EB"),
        [AppAccent.Teal] = Color.Parse("#0D9488"),
        [AppAccent.Green] = Color.Parse("#16A34A"),
        [AppAccent.Orange] = Color.Parse("#EA580C"),
        [AppAccent.Pink] = Color.Parse("#DB2777"),
        [AppAccent.Purple] = Color.Parse("#7C3AED"),
        [AppAccent.Red] = Color.Parse("#DC2626")
    };

    private static bool _applying;
    private static bool _installed;
    private static WindowIcon? _windowIcon;
    private static MemoryStream? _iconStream;

    public static AppearanceSettings Settings { get; private set; } = new();

    public static event EventHandler? Changed;

    public static SolidColorBrush Brush(string key) =>
        (SolidColorBrush)Application.Current!.Resources[key]!;

    public static double Font(int size)
    {
        if (Application.Current?.Resources[$"Font{size}"] is double value)
            return value;

        return size;
    }

    public static Color Swatch(AppAccent accent) => Accents[accent];

    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;
        Settings = Load();
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) =>
            {
                if (_applying || Settings.Theme != AppTheme.System)
                    return;

                Apply(save: false);
            };

        Apply(save: false);
    }

    public static void SetTheme(AppTheme theme) => Change(settings => settings.Theme = theme, settings => settings.Theme != theme);

    public static void SetAccent(AppAccent accent) => Change(settings => settings.Accent = accent, settings => settings.Accent != accent);

    public static void SetDensity(AppDensity density) => Change(settings => settings.Density = density, settings => settings.Density != density);

    public static void SetFontSize(AppFontSize fontSize) => Change(settings => settings.FontSize = fontSize, settings => settings.FontSize != fontSize);

    public static void ApplyWindowIcon(Window window)
    {
        if (_windowIcon is not null)
            window.Icon = _windowIcon;
    }

    private static void Change(Action<AppearanceSettings> assign, Func<AppearanceSettings, bool> differs)
    {
        if (!differs(Settings))
            return;

        assign(Settings);
        Apply(save: true);
    }

    private static void Apply(bool save)
    {
        if (Application.Current is null)
            return;

        _applying = true;
        try
        {
            var dark = UseDark();
            ApplyVariant();
            Paint(dark);
            PaintCalendar();
            ApplyFluent();
            ApplyMetrics();
            ApplyIcon();
            if (save)
                Save();
        }
        finally
        {
            _applying = false;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool UseDark()
    {
        if (Settings.Theme == AppTheme.Light)
            return false;

        if (Settings.Theme == AppTheme.Dark)
            return true;

        return Application.Current?.ActualThemeVariant != ThemeVariant.Light;
    }

    private static void ApplyVariant()
    {
        if (Application.Current is null)
            return;

        Application.Current.RequestedThemeVariant = Settings.Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private static void Paint(bool dark)
    {
        var accent = Accents[Settings.Accent];
        Set(AppBackgroundBrush, dark ? "#0B1120" : "#F8FAFC");
        Set(SurfaceBrush, dark ? "#0F172A" : "#FFFFFF");
        Set(SurfaceInsetBrush, dark ? "#111827" : "#F8FAFC");
        Set(CardBrush, dark ? "#172033" : "#F1F5F9");
        Set(CardHoverBrush, dark ? "#1E2A3F" : "#E2E8F0");
        Set(BorderBrush, dark ? "#334155" : "#CBD5E1");
        Set(BorderSoftBrush, dark ? "#263449" : "#E2E8F0");
        Set(BorderSubtleBrush, dark ? "#1E293B" : "#E2E8F0");
        Set(TextPrimaryBrush, dark ? "#F8FAFC" : "#0F172A");
        Set(TextSecondaryBrush, dark ? "#E2E8F0" : "#1E293B");
        Set(TextBodyBrush, dark ? "#CBD5E1" : "#334155");
        Set(TextMutedBrush, dark ? "#94A3B8" : "#64748B");
        Set(TextSubtleBrush, dark ? "#64748B" : "#64748B");
        Set(TextFaintBrush, dark ? "#475569" : "#94A3B8");
        Set(TextOnAccentBrush, "#FFFFFF");
        Set(DangerBrush, "#DC2626");
        Set(DangerHoverBrush, dark ? "#EF4444" : "#B91C1C");
        Set(DangerTextBrush, dark ? "#F87171" : "#B91C1C");
        Set(DangerTextHoverBrush, dark ? "#FCA5A5" : "#991B1B");
        Set(DangerSurfaceBrush, dark ? "#3F1D1D" : "#FEE2E2");
        Set(DangerSurfaceSoftBrush, dark ? "#3F1D24" : "#FEE2E2");
        Set(WarningTextBrush, dark ? "#FBBF24" : "#B45309");
        Set(WarningSurfaceBrush, dark ? "#3A2F12" : "#FEF3C7");
        Set(SuccessBrush, dark ? "#22C55E" : "#16A34A");
        Set(SuccessHoverBrush, dark ? "#4ADE80" : "#15803D");
        Set(SuccessSubtleBrush, dark ? "#14301F" : "#E8F7EE");
        Set(SuccessTextBrush, dark ? "#4ADE80" : "#15803D");
        Set(SuccessBorderBrush, dark ? "#166534" : "#86EFAC");
        Set(InfoTextBrush, dark ? "#38BDF8" : "#0369A1");
        Set(InfoSurfaceBrush, dark ? "#0C3044" : "#E0F2FE");
        Set(ReviewTextBrush, dark ? "#C4B5FD" : "#6D28D9");
        Set(ReviewSurfaceBrush, dark ? "#2E2458" : "#EDE9FE");

        if (dark && Settings.Accent == AppAccent.Blue)
        {
            Set(AccentBrush, "#2563EB");
            Set(AccentHoverBrush, "#3B82F6");
            Set(AccentPressedBrush, "#1D4ED8");
            Set(AccentTextBrush, "#60A5FA");
            Set(AccentTextHoverBrush, "#93C5FD");
            Set(AccentSubtleBrush, "#172F55");
            Set(AccentSubtleHoverBrush, "#1E3A5F");
        }
        else if (!dark && Settings.Accent == AppAccent.Blue)
        {
            Set(AccentBrush, "#2563EB");
            Set(AccentHoverBrush, "#1D4ED8");
            Set(AccentPressedBrush, "#1E40AF");
            Set(AccentTextBrush, "#1D4ED8");
            Set(AccentTextHoverBrush, "#1E3A8A");
            Set(AccentSubtleBrush, "#DBEAFE");
            Set(AccentSubtleHoverBrush, "#BFDBFE");
        }
        else if (dark && Settings.Accent == AppAccent.Purple)
        {
            Set(AccentBrush, "#7C3AED");
            Set(AccentHoverBrush, "#8B5CF6");
            Set(AccentPressedBrush, "#6D28D9");
            Set(AccentTextBrush, "#C4B5FD");
            Set(AccentTextHoverBrush, "#DDD6FE");
            Set(AccentSubtleBrush, "#3B2468");
            Set(AccentSubtleHoverBrush, "#4C1D95");
        }
        else if (!dark && Settings.Accent == AppAccent.Purple)
        {
            Set(AccentBrush, "#7C3AED");
            Set(AccentHoverBrush, "#6D28D9");
            Set(AccentPressedBrush, "#5B21B6");
            Set(AccentTextBrush, "#6D28D9");
            Set(AccentTextHoverBrush, "#5B21B6");
            Set(AccentSubtleBrush, "#F3EAFE");
            Set(AccentSubtleHoverBrush, "#E9D5FF");
        }
        else
        {
            var surface = Color.Parse(dark ? "#172033" : "#F1F5F9");
            Set(AccentBrush, dark ? accent : Darken(accent, 0.04));
            Set(AccentHoverBrush, dark ? Lighten(accent, 0.16) : Darken(accent, 0.1));
            Set(AccentPressedBrush, Darken(accent, dark ? 0.14 : 0.2));
            Set(AccentTextBrush, dark ? Lighten(accent, 0.34) : Darken(accent, 0.06));
            Set(AccentTextHoverBrush, dark ? Lighten(accent, 0.48) : Darken(accent, 0.16));
            Set(AccentSubtleBrush, Mix(surface, accent, dark ? 0.42 : 0.16));
            Set(AccentSubtleHoverBrush, Mix(surface, accent, dark ? 0.55 : 0.26));
        }
    }

    private static void PaintCalendar()
    {
        Copy("CalendarViewForeground", TextPrimaryBrush);
        Copy("CalendarViewBackground", SurfaceBrush);
        Copy("CalendarViewBorderBrush", SurfaceBrush);
        Copy("CalendarViewCalendarItemForeground", TextSecondaryBrush);
        Copy("CalendarViewHoverBorderBrush", BorderBrush);
        Copy("CalendarViewPressedBorderBrush", AccentHoverBrush);
        Copy("CalendarViewSelectedBorderBrush", AccentBrush);
        Copy("CalendarViewSelectedHoverBorderBrush", AccentHoverBrush);
        Copy("CalendarViewSelectedPressedBorderBrush", AccentPressedBrush);
        Copy("CalendarViewOutOfScopeForeground", TextFaintBrush);
        Copy("CalendarViewBlackoutForeground", TextFaintBrush);
        Copy("CalendarViewWeekDayForegroundDisabled", TextFaintBrush);
        Copy("CalendarViewNavigationButtonBorderBrushPointerOver", BorderSubtleBrush);
        Copy("CalendarViewNavigationButtonForegroundPointerOver", TextPrimaryBrush);
        Copy("CalendarViewNavigationButtonForegroundPressed", AccentTextHoverBrush);
    }

    private static void ApplyFluent()
    {
        if (Application.Current?.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } theme)
            return;

        theme.DensityStyle = Settings.Density == AppDensity.Compact
            ? DensityStyle.Compact
            : DensityStyle.Normal;

        var accent = Brush(AccentBrush).Color;
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (theme.Palettes.TryGetValue(variant, out var palette))
                palette.Accent = accent;
        }

        if (Application.Current is { } app)
            app.Resources["SystemAccentColor"] = accent;
    }

    private static void ApplyMetrics()
    {
        var compact = Settings.Density == AppDensity.Compact;
        var scale = Settings.FontSize switch
        {
            AppFontSize.Small => 0.9,
            AppFontSize.Large => 1.15,
            _ => 1d
        };

        foreach (var size in FontSizes)
            SetDouble($"Font{size}", Math.Max(1, (int)Math.Round(size * scale)));

        SetDouble("ControlHeight", compact ? 32 : 40);
        SetDouble("ControlHeightSm", compact ? 30 : 36);
        SetDouble("ControlHeightLg", compact ? 36 : 44);
        SetDouble("ControlHeightXl", compact ? 44 : 52);
        SetDouble("ControlHeightXs", compact ? 26 : 32);
        SetDouble("IconButtonSize", compact ? 28 : 34);
        SetDouble("SectionSpacing", compact ? 16 : 24);
        SetDouble("CalendarFontSize", Font(13));
        SetDouble("CalendarDayButtonFontSize", Font(13));
        SetThickness("PageMargin", compact ? new Thickness(20) : new Thickness(32));
        SetThickness("PagePadding", compact ? new Thickness(16, 18, 16, 24) : new Thickness(24, 28, 24, 40));
        SetThickness("CardPadding", compact ? new Thickness(8) : new Thickness(12));
        SetThickness("HomePadding", compact ? new Thickness(16, 8, 16, 24) : new Thickness(28, 8, 28, 32));
        SetDouble("Space8", compact ? 6 : 8);
        SetDouble("Space12", compact ? 8 : 12);
        SetDouble("Space16", compact ? 12 : 16);
        SetDouble("Space24", compact ? 16 : 24);
        SetDouble("Space32", compact ? 20 : 32);
    }

    private static void ApplyIcon()
    {
        try
        {
            var accent = Brush(AccentBrush).Color;
            var bitmap = new RenderTargetBitmap(new PixelSize(256, 256));
            using (var context = bitmap.CreateDrawingContext())
            {
                var plate = new RectangleGeometry(new Rect(0, 0, 256, 256), 58, 58);
                context.DrawGeometry(new SolidColorBrush(accent), null, plate);
                var check = Geometry.Parse("M 68,132 L 110,174 L 192,84");
                var pen = new Pen(Brushes.White, 22, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                context.DrawGeometry(null, pen, check);
            }

            var stream = new MemoryStream();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            var bytes = stream.ToArray();
            stream.Position = 0;
            _iconStream = stream;
            _windowIcon = new WindowIcon(stream);
            if (Application.Current is { } app)
                app.Resources["AppWindowIcon"] = _windowIcon;

            MacDockIcon.ApplyPng(bytes);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                    window.Icon = _windowIcon;
            }
        }
        catch (Exception)
        {
            MacDockIcon.Apply();
        }
    }

    private static void Set(string key, string hex) => Set(key, Color.Parse(hex));

    private static void Set(string key, Color color)
    {
        if (Application.Current?.Resources[key] is SolidColorBrush brush)
            brush.Color = color;
    }

    private static void Copy(string target, string source)
    {
        if (Application.Current?.Resources[target] is SolidColorBrush dest &&
            Application.Current.Resources[source] is SolidColorBrush from)
            dest.Color = from.Color;
    }

    private static void SetDouble(string key, double value)
    {
        if (Application.Current is null)
            return;

        Application.Current.Resources.Remove(key);
        Application.Current.Resources.Add(key, value);
    }

    private static void SetThickness(string key, Thickness value)
    {
        if (Application.Current is null)
            return;

        Application.Current.Resources.Remove(key);
        Application.Current.Resources.Add(key, value);
    }

    private static Color Lighten(Color color, double amount) => Mix(color, Colors.White, amount);

    private static Color Darken(Color color, double amount) => Mix(color, Colors.Black, amount);

    private static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte start, byte end) => (byte)Math.Round(start + (end - start) * amount);
        return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    private static AppearanceSettings Load()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path))
                return new AppearanceSettings();

            return JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(path), Json)
                   ?? new AppearanceSettings();
        }
        catch (Exception)
        {
            return new AppearanceSettings();
        }
    }

    private static void Save()
    {
        try
        {
            var path = SettingsPath();
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // A failed preference write must not block the rest of the app.
        }
    }

    private static string SettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Super Dev",
            "appearance.json");
}
