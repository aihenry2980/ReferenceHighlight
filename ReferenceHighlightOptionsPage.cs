using System.ComponentModel;
using System.Drawing;
using Microsoft.VisualStudio.Shell;

namespace ReferenceHighlight;

public sealed class ReferenceHighlightOptionsPage : DialogPage
{
    [Category("Light theme")]
    [DisplayName("Read color")]
    [Description("Highlight color for read references on light backgrounds.")]
    public Color LightReadColor { get; set; } = Color.FromArgb(32, 224, 0);

    [Category("Light theme")]
    [DisplayName("Write color")]
    [Description("Highlight color for write references on light backgrounds.")]
    public Color LightWriteColor { get; set; } = Color.FromArgb(240, 0, 138);

    [Category("Dark theme")]
    [DisplayName("Read color")]
    [Description("Highlight color for read references on dark backgrounds.")]
    public Color DarkReadColor { get; set; } = Color.FromArgb(38, 235, 28);

    [Category("Dark theme")]
    [DisplayName("Write color")]
    [Description("Highlight color for write references on dark backgrounds.")]
    public Color DarkWriteColor { get; set; } = Color.FromArgb(255, 20, 147);
}
