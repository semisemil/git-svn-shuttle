using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace GitSvnShuttle.UiPreview;

internal static class ThemePalette
{
    public static void Apply(string theme)
    {
        var dark = !string.Equals(theme, "light", System.StringComparison.OrdinalIgnoreCase);
        var values = dark ? Dark() : Light();
        foreach (var pair in values)
        {
            Application.Current.Resources[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
        }
    }

    private static Dictionary<object, string> Dark() => new Dictionary<object, string>
    {
        [VsBrushes.ToolWindowBackgroundKey] = "#181A1F",
        [VsBrushes.ToolWindowTextKey] = "#F1F3F5",
        [VsBrushes.ToolWindowBorderKey] = "#343840",
        [VsBrushes.ToolWindowButtonHoverActiveKey] = "#333842",
        [VsBrushes.CommandBarGradientKey] = "#1E2127",
        [VsBrushes.CommandBarGradientBeginKey] = "#22252C",
        [VsBrushes.CommandBarBorderKey] = "#343840",
        [VsBrushes.CommandBarToolBarSeparatorKey] = "#3D424B",
        [VsBrushes.CommandBarSelectedKey] = "#2B6EA6",
        [VsBrushes.CommandBarTextSelectedKey] = "#FFFFFF",
        [VsBrushes.ComboBoxBackgroundKey] = "#252932",
        [VsBrushes.ComboBoxBorderKey] = "#414650",
        [VsBrushes.ComboBoxMouseOverBackgroundBeginKey] = "#303641",
        [VsBrushes.ComboBoxMouseOverBorderKey] = "#59616E",
        [VsBrushes.ComboBoxMouseDownBackgroundKey] = "#393F4B",
        [VsBrushes.ComboBoxMouseDownBorderKey] = "#6C7685",
        [VsBrushes.ComboBoxDisabledBackgroundKey] = "#202329",
        [VsBrushes.ComboBoxDisabledBorderKey] = "#30343B",
        [VsBrushes.ComboBoxDisabledGlyphKey] = "#707782",
        [VsBrushes.AccentMediumKey] = "#1976B9",
        [VsBrushes.AccentBorderKey] = "#3C91D0",
        [VsBrushes.AccentLightKey] = "#67B7F1",
        [VsBrushes.AccentDarkKey] = "#0B456E",
        [VsBrushes.HighlightTextKey] = "#FFFFFF",
        [VsBrushes.InfoBackgroundKey] = "#1F3442",
        [VsBrushes.InfoTextKey] = "#D7ECFA",
    };

    private static Dictionary<object, string> Light() => new Dictionary<object, string>
    {
        [VsBrushes.ToolWindowBackgroundKey] = "#F7F8FA",
        [VsBrushes.ToolWindowTextKey] = "#1F242B",
        [VsBrushes.ToolWindowBorderKey] = "#D6DAE0",
        [VsBrushes.ToolWindowButtonHoverActiveKey] = "#E5EAF0",
        [VsBrushes.CommandBarGradientKey] = "#FFFFFF",
        [VsBrushes.CommandBarGradientBeginKey] = "#F2F4F7",
        [VsBrushes.CommandBarBorderKey] = "#D8DCE2",
        [VsBrushes.CommandBarToolBarSeparatorKey] = "#D1D5DB",
        [VsBrushes.CommandBarSelectedKey] = "#D9EDFC",
        [VsBrushes.CommandBarTextSelectedKey] = "#0F4C75",
        [VsBrushes.ComboBoxBackgroundKey] = "#FFFFFF",
        [VsBrushes.ComboBoxBorderKey] = "#CBD0D7",
        [VsBrushes.ComboBoxMouseOverBackgroundBeginKey] = "#EEF3F7",
        [VsBrushes.ComboBoxMouseOverBorderKey] = "#9CA7B3",
        [VsBrushes.ComboBoxMouseDownBackgroundKey] = "#E1E8EE",
        [VsBrushes.ComboBoxMouseDownBorderKey] = "#7D8A98",
        [VsBrushes.ComboBoxDisabledBackgroundKey] = "#F0F1F3",
        [VsBrushes.ComboBoxDisabledBorderKey] = "#E1E3E6",
        [VsBrushes.ComboBoxDisabledGlyphKey] = "#A1A7AF",
        [VsBrushes.AccentMediumKey] = "#0878B9",
        [VsBrushes.AccentBorderKey] = "#0067A3",
        [VsBrushes.AccentLightKey] = "#006DAA",
        [VsBrushes.AccentDarkKey] = "#004D75",
        [VsBrushes.HighlightTextKey] = "#FFFFFF",
        [VsBrushes.InfoBackgroundKey] = "#E5F3FB",
        [VsBrushes.InfoTextKey] = "#173B52",
    };
}
