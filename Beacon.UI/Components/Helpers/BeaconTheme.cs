using MudBlazor;

namespace Beacon.UI.Components.Helpers;

public static class BeaconTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#10b981",
            PrimaryDarken = "#047857",
            PrimaryLighten = "#6ee7b7",
            PrimaryContrastText = "#ffffff",

            Secondary = "#0d9488",
            SecondaryDarken = "#0f766e",
            SecondaryLighten = "#5eead4",

            Tertiary = "#0ea5e9",
            TertiaryDarken = "#0369a1",
            TertiaryLighten = "#7dd3fc",

            Info = "#0ea5e9",
            Success = "#10b981",
            Warning = "#f59e0b",
            Error = "#e11d48",
            Dark = "#0b1220",

            AppbarBackground = "rgba(255,255,255,0.85)",
            AppbarText = "#0f172a",
            DrawerBackground = "#fafbfc",
            DrawerText = "#334155",
            DrawerIcon = "#475569",

            Background = "#f8fafc",
            Surface = "#ffffff",

            TextPrimary = "#0f172a",
            TextSecondary = "#475569",
            TextDisabled = "#94a3b8",

            ActionDefault = "#475569",
            ActionDisabled = "rgba(15,23,42,0.26)",
            ActionDisabledBackground = "rgba(15,23,42,0.08)",

            Divider = "rgba(15,23,42,0.08)",
            DividerLight = "rgba(15,23,42,0.05)",
            LinesDefault = "rgba(15,23,42,0.1)",
            LinesInputs = "rgba(15,23,42,0.18)",

            TableLines = "rgba(15,23,42,0.06)",
            TableStriped = "rgba(15,23,42,0.02)",
            TableHover = "rgba(16,185,129,0.06)"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#34d399",
            PrimaryDarken = "#059669",
            PrimaryLighten = "#a7f3d0",
            PrimaryContrastText = "#022c22",

            Secondary = "#2dd4bf",
            SecondaryDarken = "#0d9488",
            SecondaryLighten = "#99f6e4",

            Tertiary = "#38bdf8",
            TertiaryDarken = "#0284c7",
            TertiaryLighten = "#bae6fd",

            Info = "#38bdf8",
            Success = "#34d399",
            Warning = "#fbbf24",
            Error = "#fb7185",
            Dark = "#020617",

            AppbarBackground = "rgba(11,18,32,0.8)",
            AppbarText = "#e2e8f0",
            DrawerBackground = "#0b1220",
            DrawerText = "#cbd5e1",
            DrawerIcon = "#94a3b8",

            Background = "#070b14",
            Surface = "#0f172a",

            TextPrimary = "#e2e8f0",
            TextSecondary = "#94a3b8",
            TextDisabled = "#475569",

            ActionDefault = "#94a3b8",
            ActionDisabled = "rgba(226,232,240,0.26)",
            ActionDisabledBackground = "rgba(226,232,240,0.08)",

            Divider = "rgba(226,232,240,0.08)",
            DividerLight = "rgba(226,232,240,0.05)",
            LinesDefault = "rgba(226,232,240,0.1)",
            LinesInputs = "rgba(226,232,240,0.18)",

            TableLines = "rgba(226,232,240,0.06)",
            TableStriped = "rgba(226,232,240,0.02)",
            TableHover = "rgba(52,211,153,0.08)"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            DrawerWidthLeft = "260px"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "SF Pro Display", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif"],
                LetterSpacing = "0"
            },
            H1 = new H1Typography { FontWeight = "700", LetterSpacing = "-0.5px" },
            H2 = new H2Typography { FontWeight = "700", LetterSpacing = "-0.4px" },
            H3 = new H3Typography { FontWeight = "700", LetterSpacing = "-0.3px" },
            H4 = new H4Typography { FontWeight = "700", LetterSpacing = "-0.3px" },
            H5 = new H5Typography { FontWeight = "600", LetterSpacing = "-0.2px" },
            H6 = new H6Typography { FontWeight = "600", LetterSpacing = "-0.1px" },
            Button = new ButtonTypography { TextTransform = "none", FontWeight = "500" }
        }
    };
}
