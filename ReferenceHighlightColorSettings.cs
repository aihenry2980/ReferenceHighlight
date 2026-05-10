using DrawingColor = System.Drawing.Color;
using WpfColor = System.Windows.Media.Color;

namespace ReferenceHighlight;

internal readonly struct ReferenceHighlightColors
{
    public ReferenceHighlightColors(WpfColor readColor, WpfColor writeColor)
    {
        ReadColor = readColor;
        WriteColor = writeColor;
    }

    public WpfColor ReadColor { get; }

    public WpfColor WriteColor { get; }
}

internal static class ReferenceHighlightColorSettings
{
    private static readonly ReferenceHighlightColors DefaultLightColors = new(
        WpfColor.FromRgb(32, 224, 0),
        WpfColor.FromRgb(240, 0, 138));

    private static readonly ReferenceHighlightColors DefaultDarkColors = new(
        WpfColor.FromRgb(38, 235, 28),
        WpfColor.FromRgb(255, 20, 147));

    public static ReferenceHighlightColors GetColors(bool darkTheme)
    {
        ReferenceHighlightOptionsPage? options = ReferenceHighlightPackage.OptionsPage;
        if (options is null)
        {
            return darkTheme ? DefaultDarkColors : DefaultLightColors;
        }

        return darkTheme
            ? new ReferenceHighlightColors(ToWpfColor(options.DarkReadColor), ToWpfColor(options.DarkWriteColor))
            : new ReferenceHighlightColors(ToWpfColor(options.LightReadColor), ToWpfColor(options.LightWriteColor));
    }

    public static WpfColor GetDefaultReadColor(bool darkTheme)
        => darkTheme ? DefaultDarkColors.ReadColor : DefaultLightColors.ReadColor;

    public static WpfColor GetDefaultWriteColor(bool darkTheme)
        => darkTheme ? DefaultDarkColors.WriteColor : DefaultLightColors.WriteColor;

    private static WpfColor ToWpfColor(DrawingColor color)
        => WpfColor.FromArgb(color.A, color.R, color.G, color.B);
}
