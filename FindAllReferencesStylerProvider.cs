using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Utilities;

namespace ReferenceHighlight;

[Export(typeof(ITableControlEventProcessorProvider))]
[Name(nameof(FindAllReferencesStylerProvider))]
[DataSource(StandardTableDataSources2.FindAllReferencesTableDataSource)]
[ManagerType(StandardTables2.FindAllReferences)]
internal sealed class FindAllReferencesStylerProvider : ITableControlEventProcessorProvider
{
    public ITableControlEventProcessor? GetAssociatedEventProcessor(IWpfTableControl tableControl)
    {
        FindAllReferencesVisualStyler.Attach(tableControl);
        return NullTableControlEventProcessor.Instance;
    }
}

internal static class FindAllReferencesVisualStyler
{
    private const double ReferenceFontSize = 18.0;
    private const string ReadText = "Read";
    private const string WriteText = "Write";
    private const double SameRowTolerance = 8.0;
    private const double MinimumHighlightWidth = 10.0;
    private const double MaximumHighlightHeight = 40.0;
    private const double CodeColumnHeaderLeftPadding = 80.0;

    private static readonly DependencyProperty IsAttachedProperty =
        DependencyProperty.RegisterAttached(
            "IsAttached",
            typeof(bool),
            typeof(FindAllReferencesVisualStyler),
            new PropertyMetadata(false));
    private static readonly DependencyProperty HasFallbackBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "HasFallbackBackground",
            typeof(bool),
            typeof(FindAllReferencesVisualStyler),
            new PropertyMetadata(false));
    private static readonly DependencyProperty OriginalTextProperty =
        DependencyProperty.RegisterAttached(
            "OriginalText",
            typeof(string),
            typeof(FindAllReferencesVisualStyler),
            new PropertyMetadata(null));
    private static readonly DependencyProperty AppliedSearchTextProperty =
        DependencyProperty.RegisterAttached(
            "AppliedSearchText",
            typeof(string),
            typeof(FindAllReferencesVisualStyler),
            new PropertyMetadata(null));

    public static void Attach(IWpfTableControl tableControl)
    {
        if (tableControl.Control is not FrameworkElement root)
        {
            return;
        }

        if ((bool)root.GetValue(IsAttachedProperty))
        {
            QueueRefresh(root, tableControl);
            return;
        }

        root.SetValue(IsAttachedProperty, true);
        root.Loaded += (_, _) => QueueRefresh(root, tableControl);
        root.LayoutUpdated += (_, _) => QueueRefresh(root, tableControl);
        tableControl.EntriesChanged += (_, _) => QueueRefresh(root, tableControl);
        QueueRefresh(root, tableControl);
    }

    private static void QueueRefresh(FrameworkElement root, IWpfTableControl tableControl)
    {
        if (root.Dispatcher.HasShutdownStarted || root.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if ((bool)root.GetValue(PendingRefreshProperty))
        {
            return;
        }

        root.SetValue(PendingRefreshProperty, true);
#pragma warning disable VSTHRD001
        _ = root.Dispatcher.BeginInvoke(new Action(() =>
        {
            root.SetValue(PendingRefreshProperty, false);
            Refresh(root, tableControl);
        }));
#pragma warning restore VSTHRD001
    }

    private static readonly DependencyProperty PendingRefreshProperty =
        DependencyProperty.RegisterAttached(
            "PendingRefresh",
            typeof(bool),
            typeof(FindAllReferencesVisualStyler),
            new PropertyMetadata(false));

    private static void Refresh(FrameworkElement root, IWpfTableControl tableControl)
    {
        IReadOnlyList<ReferenceLocation> references = FindAllReferencesTableReader.Read(tableControl);
        if (references.Count > 0)
        {
            ReferenceHighlightStore.ReplaceAll(references);
        }

        ApplyToRoot(root);
    }

    public static void ApplyToRoot(FrameworkElement root)
    {
        ApplyFontSizing(root);
        ApplyReadWriteStyling(root);
    }

    private static void ApplyFontSizing(FrameworkElement root)
    {
        root.SetCurrentValue(TextElement.FontSizeProperty, ReferenceFontSize);

        foreach (FrameworkElement element in EnumerateVisualDescendants<FrameworkElement>(root))
        {
            switch (element)
            {
                case TextBlock textBlock:
                    textBlock.SetCurrentValue(TextBlock.FontSizeProperty, ReferenceFontSize);
                    break;
                case Control control:
                    control.SetCurrentValue(Control.FontSizeProperty, ReferenceFontSize);
                    break;
            }
        }
    }

    private static void ApplyReadWriteStyling(FrameworkElement root)
    {
        List<TextBlockInfo> textBlocks = EnumerateVisualDescendants<TextBlock>(root)
            .Select(textBlock => TextBlockInfo.TryCreate(root, textBlock, out TextBlockInfo info) ? info : null)
            .Where(info => info is not null)
            .Select(info => info!)
            .ToList();
        List<HighlightVisualInfo> highlightVisuals = EnumerateVisualDescendants<FrameworkElement>(root)
            .Select(element => HighlightVisualInfo.TryCreate(root, element, out HighlightVisualInfo info) ? info : null)
            .Where(info => info is not null)
            .Select(info => info!)
            .ToList();
        CodeColumnBounds? codeColumnBounds = CodeColumnBounds.TryCreate(textBlocks, out CodeColumnBounds bounds)
            ? bounds
            : null;
        string? searchText = TryGetSearchText(textBlocks, out string foundSearchText)
            ? foundSearchText
            : null;

        foreach (TextBlockInfo info in textBlocks.Where(info => (bool)info.Block.GetValue(HasFallbackBackgroundProperty)))
        {
            info.Block.ClearValue(TextBlock.BackgroundProperty);
            info.Block.ClearValue(TextBlock.ForegroundProperty);
            info.Block.SetValue(HasFallbackBackgroundProperty, false);
        }

        foreach (TextBlockInfo kindBlock in textBlocks.Where(info => IsKindText(info.NormalizedText)))
        {
            TextBlockInfo? codeBlock = FindCodeBlockForKind(textBlocks, kindBlock, codeColumnBounds)
                ?? FindHighlightedIdentifierForKind(textBlocks, kindBlock, codeColumnBounds);
            ReferenceBrush accentBrush = GetBrushes(codeBlock?.Block ?? kindBlock.Block).GetBrushForKind(kindBlock.NormalizedText);
            _ = ApplyWholeTextRunHighlight(kindBlock.Block, accentBrush);

            if (codeBlock is null)
            {
                bool updatedWithoutCodeBlock = TryRetintCodeTextBlocks(textBlocks, kindBlock, codeColumnBounds, searchText, accentBrush);
                updatedWithoutCodeBlock |= TryRetintHighlightVisuals(highlightVisuals, kindBlock, codeColumnBounds, accentBrush);

                continue;
            }

            bool updated = TryRetintCodeTextBlocks(textBlocks, kindBlock, codeColumnBounds, searchText, accentBrush);
            updated |= TryRetintExistingHighlight(codeBlock.Block, accentBrush);
            updated |= TryApplySearchHighlight(codeBlock.Block, searchText, accentBrush);
            updated |= TryRetintIdentifierTextBlock(codeBlock.Block, accentBrush);

            updated |= TryRetintHighlightVisuals(highlightVisuals, kindBlock, codeColumnBounds, accentBrush);

            if (!updated)
            {
                ApplyHighlightToTextBlock(codeBlock.Block, accentBrush);
                codeBlock.Block.SetValue(HasFallbackBackgroundProperty, true);
            }
        }
    }

    private static TextBlockInfo? FindCodeBlockForKind(
        IReadOnlyList<TextBlockInfo> textBlocks,
        TextBlockInfo kindBlock,
        CodeColumnBounds? codeColumnBounds)
    {
        return textBlocks
            .Where(info => info.Block != kindBlock.Block)
            .Where(info => LooksLikeCodeSnippet(info.Block))
            .Where(info => IsInsideCodeColumn(info.Bounds, codeColumnBounds))
            .Select(info => new
            {
                Info = info,
                VerticalDistance = Math.Abs(info.CenterY - kindBlock.CenterY),
                Left = info.Bounds.Left,
            })
            .Where(candidate => candidate.VerticalDistance <= Math.Max(SameRowTolerance, kindBlock.Bounds.Height))
            .OrderBy(candidate => candidate.VerticalDistance)
            .ThenBy(candidate => candidate.Left)
            .Select(candidate => candidate.Info)
            .FirstOrDefault();
    }

    private static TextBlockInfo? FindHighlightedIdentifierForKind(
        IReadOnlyList<TextBlockInfo> textBlocks,
        TextBlockInfo kindBlock,
        CodeColumnBounds? codeColumnBounds)
    {
        return textBlocks
            .Where(info => info.Block != kindBlock.Block)
            .Where(info => !IsKindText(info.NormalizedText))
            .Where(info => HasExistingHighlight(info.Block))
            .Where(info => IsInsideCodeColumn(info.Bounds, codeColumnBounds))
            .Select(info => new
            {
                Info = info,
                VerticalDistance = Math.Abs(info.CenterY - kindBlock.CenterY),
                Left = info.Bounds.Left,
            })
            .Where(candidate => candidate.VerticalDistance <= Math.Max(SameRowTolerance, kindBlock.Bounds.Height))
            .OrderBy(candidate => candidate.VerticalDistance)
            .ThenBy(candidate => candidate.Left)
            .Select(candidate => candidate.Info)
            .FirstOrDefault();
    }

    private static bool TryRetintCodeTextBlocks(
        IReadOnlyList<TextBlockInfo> textBlocks,
        TextBlockInfo kindBlock,
        CodeColumnBounds? codeColumnBounds,
        string? searchText,
        ReferenceBrush accentBrush)
    {
        bool updated = false;
        double verticalTolerance = Math.Max(SameRowTolerance, kindBlock.Bounds.Height);

        foreach (TextBlockInfo info in textBlocks)
        {
            if (info.Block == kindBlock.Block
                || IsKindText(info.NormalizedText)
                || Math.Abs(info.CenterY - kindBlock.CenterY) > verticalTolerance
                || !IsInsideCodeColumn(info.Bounds, codeColumnBounds))
            {
                continue;
            }

            updated |= TryRetintExistingHighlight(info.Block, accentBrush);
            updated |= TryApplySearchHighlight(info.Block, searchText, accentBrush);
            updated |= TryRetintIdentifierTextBlock(info.Block, accentBrush);
        }

        return updated;
    }

    private static bool TryRetintHighlightVisuals(
        IReadOnlyList<HighlightVisualInfo> highlightVisuals,
        TextBlockInfo kindBlock,
        CodeColumnBounds? codeColumnBounds,
        ReferenceBrush accentBrush)
    {
        bool updated = false;

        foreach (HighlightVisualInfo visual in highlightVisuals.Where(info => IsSameRowCodeHighlight(info, kindBlock, codeColumnBounds)))
        {
            visual.SetBrush(accentBrush.Background);
            updated = true;
        }

        return updated;
    }

    private static bool IsSameRowCodeHighlight(
        HighlightVisualInfo highlight,
        TextBlockInfo kindBlock,
        CodeColumnBounds? codeColumnBounds)
    {
        double verticalTolerance = Math.Max(SameRowTolerance, kindBlock.Bounds.Height);

        return Math.Abs(highlight.CenterY - kindBlock.CenterY) <= verticalTolerance
            && IsInsideCodeColumn(highlight.Bounds, codeColumnBounds)
            && highlight.Bounds.Width >= MinimumHighlightWidth
            && highlight.Bounds.Height <= MaximumHighlightHeight;
    }

    private static bool IsInsideCodeColumn(Rect bounds, CodeColumnBounds? codeColumnBounds)
        => codeColumnBounds is null
            || (bounds.Left >= codeColumnBounds.Left - SameRowTolerance
                && bounds.Right <= codeColumnBounds.Right + SameRowTolerance);

    private static bool TryRetintIdentifierTextBlock(TextBlock textBlock, ReferenceBrush accentBrush)
    {
        if (!HasExistingHighlight(textBlock))
        {
            return false;
        }

        ApplyHighlightToTextBlock(textBlock, accentBrush);
        return true;
    }

    private static bool TryApplySearchHighlight(TextBlock textBlock, string? searchText, ReferenceBrush accentBrush)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return false;
        }

        string originalText = GetOriginalText(textBlock);
        List<TextSegment> segments = SplitByIdentifier(originalText, searchText!);
        if (!segments.Any(segment => segment.IsMatch))
        {
            return false;
        }

        textBlock.Inlines.Clear();
        foreach (TextSegment segment in segments)
        {
            Run run = new(segment.Text)
            {
                FontSize = ReferenceFontSize,
            };

            if (segment.IsMatch)
            {
                run.Background = accentBrush.Background;
                run.Foreground = accentBrush.Foreground;
            }

            textBlock.Inlines.Add(run);
        }

        textBlock.SetValue(AppliedSearchTextProperty, searchText);
        return true;
    }

    private static string GetOriginalText(TextBlock textBlock)
    {
        if (textBlock.GetValue(OriginalTextProperty) is string originalText)
        {
            return originalText;
        }

        originalText = GetTextBlockText(textBlock);
        textBlock.SetValue(OriginalTextProperty, originalText);
        return originalText;
    }

    private static List<TextSegment> SplitByIdentifier(string text, string identifier)
    {
        List<TextSegment> segments = new();
        int searchIndex = 0;

        while (searchIndex < text.Length)
        {
            int matchIndex = IndexOfIdentifier(text, identifier, searchIndex);
            if (matchIndex < 0)
            {
                segments.Add(new TextSegment(text.Substring(searchIndex), false));
                break;
            }

            if (matchIndex > searchIndex)
            {
                segments.Add(new TextSegment(text.Substring(searchIndex, matchIndex - searchIndex), false));
            }

            segments.Add(new TextSegment(text.Substring(matchIndex, identifier.Length), true));
            searchIndex = matchIndex + identifier.Length;
        }

        if (segments.Count == 0)
        {
            segments.Add(new TextSegment(text, false));
        }

        return segments;
    }

    private static int IndexOfIdentifier(string text, string identifier, int startIndex)
    {
        int index = startIndex;

        while (index < text.Length)
        {
            index = text.IndexOf(identifier, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            int before = index - 1;
            int after = index + identifier.Length;
            if ((before < 0 || !IsIdentifierChar(text[before]))
                && (after >= text.Length || !IsIdentifierChar(text[after])))
            {
                return index;
            }

            index += identifier.Length;
        }

        return -1;
    }

    private static bool TryRetintExistingHighlight(TextBlock textBlock, ReferenceBrush accentBrush)
    {
        bool updated = false;

        foreach (Inline inline in textBlock.Inlines)
        {
            updated |= TryRetintInline(inline, accentBrush);
        }

        return updated;
    }

    private static bool TryRetintInline(Inline inline, ReferenceBrush accentBrush)
    {
        if (inline is Run run)
        {
            if (!HasExistingHighlight(run))
            {
                return false;
            }

            run.Background = accentBrush.Background;
            run.Foreground = accentBrush.Foreground;
            return true;
        }

        if (inline is Span span)
        {
            bool updated = false;

            foreach (Inline childInline in span.Inlines)
            {
                updated |= TryRetintInline(childInline, accentBrush);
            }

            return updated;
        }

        return false;
    }

    private static bool HasExistingHighlight(Run run)
    {
        if (run.Background is SolidColorBrush solidBrush)
        {
            Color color = solidBrush.Color;
            return color.A > 0 && (color.R != 0 || color.G != 0 || color.B != 0);
        }

        return run.Background is not null;
    }

    private static bool HasExistingHighlight(TextBlock textBlock)
    {
        if (textBlock.Background is SolidColorBrush solidBrush)
        {
            Color color = solidBrush.Color;
            return color.A > 0 && (color.R != 0 || color.G != 0 || color.B != 0);
        }

        return textBlock.Background is not null;
    }

    private static void ApplyHighlightToTextBlock(TextBlock textBlock, ReferenceBrush accentBrush)
    {
        textBlock.SetCurrentValue(TextBlock.BackgroundProperty, accentBrush.Background);
        textBlock.SetCurrentValue(TextBlock.ForegroundProperty, accentBrush.Foreground);
    }

    private static bool ApplyWholeTextRunHighlight(TextBlock textBlock, ReferenceBrush accentBrush)
    {
        string text = GetOriginalText(textBlock);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        textBlock.ClearValue(TextBlock.BackgroundProperty);
        textBlock.Inlines.Clear();
        textBlock.Inlines.Add(new Run(text)
        {
            Background = accentBrush.Background,
            Foreground = accentBrush.Foreground,
            FontSize = ReferenceFontSize,
        });
        return true;
    }

    private static string GetTextBlockText(TextBlock textBlock)
    {
        if (!string.IsNullOrEmpty(textBlock.Text))
        {
            return textBlock.Text;
        }

        if (!textBlock.Inlines.Any())
        {
            return string.Empty;
        }

        return string.Concat(textBlock.Inlines.Select(GetInlineText));
    }

    private static string GetInlineText(Inline inline)
    {
        if (inline is Run run)
        {
            return run.Text;
        }

        if (inline is Span span)
        {
            return string.Concat(span.Inlines.Select(GetInlineText));
        }

        return string.Empty;
    }

    private static bool LooksLikeCodeSnippet(TextBlock textBlock)
    {
        string text = GetTextBlockText(textBlock);
        if (string.IsNullOrWhiteSpace(text) || IsKindText(NormalizeText(text)))
        {
            return false;
        }

        if (LooksLikeFileName(text))
        {
            return false;
        }

        return text.IndexOfAny(new[] { '(', ')', '{', '}', ';', '=', '.', '[', ']' }) >= 0
            || text.Contains("::", StringComparison.Ordinal)
            || text.Contains("->", StringComparison.Ordinal)
            || text.Contains("<", StringComparison.Ordinal)
            || text.Contains(">", StringComparison.Ordinal);
    }

    private static bool LooksLikeFileName(string text)
        => text.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".cxx", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".cc", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".c", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".fs", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSearchText(IReadOnlyList<TextBlockInfo> textBlocks, out string searchText)
    {
        foreach (TextBlockInfo info in textBlocks)
        {
            string text = info.NormalizedText;
            if (text.IndexOf(" references", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            int firstQuote = text.IndexOf('\'');
            int secondQuote = firstQuote >= 0 ? text.IndexOf('\'', firstQuote + 1) : -1;
            if (firstQuote >= 0 && secondQuote > firstQuote + 1)
            {
                searchText = text.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                return true;
            }
        }

        searchText = string.Empty;
        return false;
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsKindText(string? text)
        => string.Equals(text, ReadText, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, WriteText, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text!.Trim();
    }

    private static ReferenceBrushes GetBrushes(DependencyObject element)
    {
        ReferenceHighlightColors colors = ReferenceHighlightColorSettings.GetColors(IsOnDarkSurface(element));
        return new ReferenceBrushes(
            CreateReferenceBrush(colors.ReadColor),
            CreateReferenceBrush(colors.WriteColor));
    }

    private static bool IsOnDarkSurface(DependencyObject element)
        => TryFindBackgroundColor(element, out Color color) && GetRelativeLuminance(color) < 0.45;

    private static bool TryFindBackgroundColor(DependencyObject element, out Color color)
    {
        DependencyObject? current = element;

        while (current is not null)
        {
            if (TryGetBackgroundBrush(current, out Brush? brush)
                && brush is SolidColorBrush solidBrush
                && solidBrush.Color.A > 16)
            {
                color = solidBrush.Color;
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        color = default;
        return false;
    }

    private static double GetRelativeLuminance(Color color)
        => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

    private static bool TryGetBackgroundBrush(DependencyObject element, out Brush? brush)
    {
        try
        {
            brush = element switch
            {
                Border border => border.Background,
                TextBlock textBlock => textBlock.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                _ => null,
            };

            return brush is not null;
        }
        catch (InvalidCastException)
        {
            brush = null;
            return false;
        }
    }

    private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in EnumerateVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static ReferenceBrush CreateReferenceBrush(Color color)
    {
        SolidColorBrush background = new(color);
        background.Freeze();

        Brush foreground = GetRelativeLuminance(color) >= 0.5 ? Brushes.Black : Brushes.White;
        return new ReferenceBrush(background, foreground);
    }

    private readonly struct ReferenceBrushes
    {
        public ReferenceBrushes(ReferenceBrush readBrush, ReferenceBrush writeBrush)
        {
            ReadBrush = readBrush;
            WriteBrush = writeBrush;
        }

        private ReferenceBrush ReadBrush { get; }

        private ReferenceBrush WriteBrush { get; }

        public ReferenceBrush GetBrushForKind(string kind)
            => string.Equals(kind, WriteText, StringComparison.OrdinalIgnoreCase) ? WriteBrush : ReadBrush;
    }

    private readonly struct ReferenceBrush
    {
        public ReferenceBrush(Brush background, Brush foreground)
        {
            Background = background;
            Foreground = foreground;
        }

        public Brush Background { get; }

        public Brush Foreground { get; }
    }

    private readonly struct TextSegment
    {
        public TextSegment(string text, bool isMatch)
        {
            Text = text;
            IsMatch = isMatch;
        }

        public string Text { get; }

        public bool IsMatch { get; }
    }

    private sealed class TextBlockInfo
    {
        private TextBlockInfo(TextBlock block, Rect bounds, string text)
        {
            Block = block;
            Bounds = bounds;
            NormalizedText = NormalizeText(text);
        }

        public TextBlock Block { get; }

        public Rect Bounds { get; }

        public string NormalizedText { get; }

        public double CenterY => Bounds.Top + (Bounds.Height / 2.0);

        public static bool TryCreate(FrameworkElement root, TextBlock block, out TextBlockInfo info)
        {
            info = null!;

            string text = GetTextBlockText(block);
            if (!block.IsVisible || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                GeneralTransform transform = block.TransformToAncestor(root);
                Point origin = transform.Transform(new Point(0, 0));
                double width = block.ActualWidth;
                double height = block.ActualHeight;

                if (width <= 0.0 || height <= 0.0)
                {
                    return false;
                }

                info = new TextBlockInfo(block, new Rect(origin, new Size(width, height)), text);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private sealed class CodeColumnBounds
    {
        private CodeColumnBounds(double left, double right)
        {
            Left = left;
            Right = right;
        }

        public double Left { get; }

        public double Right { get; }

        public static bool TryCreate(IReadOnlyList<TextBlockInfo> textBlocks, out CodeColumnBounds bounds)
        {
            bounds = null!;

            TextBlockInfo? codeHeader = textBlocks
                .Where(info => string.Equals(info.NormalizedText, "Code", StringComparison.OrdinalIgnoreCase))
                .OrderBy(info => info.Bounds.Top)
                .FirstOrDefault();

            if (codeHeader is null)
            {
                return false;
            }

            TextBlockInfo? nextHeader = textBlocks
                .Where(info => info.Block != codeHeader.Block)
                .Where(info => Math.Abs(info.CenterY - codeHeader.CenterY) <= Math.Max(SameRowTolerance, codeHeader.Bounds.Height))
                .Where(info => info.Bounds.Left > codeHeader.Bounds.Left + SameRowTolerance)
                .OrderBy(info => info.Bounds.Left)
                .FirstOrDefault();

            if (nextHeader is null)
            {
                return false;
            }

            double left = Math.Max(0.0, codeHeader.Bounds.Left - CodeColumnHeaderLeftPadding);
            double right = Math.Max(left, nextHeader.Bounds.Left - SameRowTolerance);
            bounds = new CodeColumnBounds(left, right);
            return true;
        }
    }

    private sealed class HighlightVisualInfo
    {
        private HighlightVisualInfo(FrameworkElement element, Rect bounds, HighlightBrushKind brushKind)
        {
            Element = element;
            Bounds = bounds;
            BrushKind = brushKind;
        }

        private FrameworkElement Element { get; }

        private HighlightBrushKind BrushKind { get; }

        public Rect Bounds { get; }

        public double CenterY => Bounds.Top + (Bounds.Height / 2.0);

        public void SetBrush(Brush brush)
        {
            switch (BrushKind)
            {
                case HighlightBrushKind.BorderBackground:
                    ((Border)Element).SetCurrentValue(Border.BackgroundProperty, brush);
                    break;
                case HighlightBrushKind.PanelBackground:
                    ((Panel)Element).SetCurrentValue(Panel.BackgroundProperty, brush);
                    break;
                case HighlightBrushKind.ControlBackground:
                    ((Control)Element).SetCurrentValue(Control.BackgroundProperty, brush);
                    break;
                case HighlightBrushKind.TextBlockBackground:
                    ((TextBlock)Element).SetCurrentValue(TextBlock.BackgroundProperty, brush);
                    break;
                case HighlightBrushKind.ShapeFill:
                    ((Shape)Element).SetCurrentValue(Shape.FillProperty, brush);
                    break;
            }
        }

        public static bool TryCreate(FrameworkElement root, FrameworkElement element, out HighlightVisualInfo info)
        {
            info = null!;

            if (!element.IsVisible || !TryGetHighlightBrushKind(element, out HighlightBrushKind brushKind))
            {
                return false;
            }

            try
            {
                GeneralTransform transform = element.TransformToAncestor(root);
                Point origin = transform.Transform(new Point(0, 0));
                double width = element.ActualWidth;
                double height = element.ActualHeight;

                if (width <= 0.0 || height <= 0.0)
                {
                    return false;
                }

                info = new HighlightVisualInfo(element, new Rect(origin, new Size(width, height)), brushKind);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool TryGetHighlightBrushKind(FrameworkElement element, out HighlightBrushKind brushKind)
        {
            if (!TryGetBackgroundBrush(element, out Brush? brush)
                && element is Shape shape)
            {
                brush = shape.Fill;
            }

            if (!IsExistingTokenHighlightBrush(brush))
            {
                brushKind = default;
                return false;
            }

            brushKind = element switch
            {
                Border => HighlightBrushKind.BorderBackground,
                TextBlock => HighlightBrushKind.TextBlockBackground,
                Panel => HighlightBrushKind.PanelBackground,
                Control => HighlightBrushKind.ControlBackground,
                Shape => HighlightBrushKind.ShapeFill,
                _ => default,
            };
            return true;
        }

        private static bool IsExistingTokenHighlightBrush(Brush? brush)
        {
            if (brush is not SolidColorBrush solidBrush)
            {
                return brush is not null;
            }

            Color color = solidBrush.Color;
            if (color.A <= 16 || (color.R == 0 && color.G == 0 && color.B == 0))
            {
                return false;
            }

            double luminance = GetRelativeLuminance(color);
            bool lowSaturation = Math.Abs(color.R - color.G) < 35
                && Math.Abs(color.G - color.B) < 35
                && Math.Abs(color.R - color.B) < 35;
            bool beigeHighlight = color.R >= 180 && color.G >= 175 && color.B >= 130;
            bool blueSelectionHighlight = color.B > color.R + 25 && color.B > color.G + 10;

            return beigeHighlight || blueSelectionHighlight || (lowSaturation && luminance is > 0.25 and < 0.9);
        }
    }

    private enum HighlightBrushKind
    {
        BorderBackground,
        PanelBackground,
        ControlBackground,
        TextBlockBackground,
        ShapeFill,
    }

    internal static bool IsReadWriteText(string? text)
        => IsKindText(NormalizeText(text));
}

internal static class FindAllReferencesTableReader
{
    private static readonly string[] PathKeys =
    {
        StandardTableKeyNames.Path,
        StandardTableKeyNames2.Path,
        StandardTableKeyNames.DisplayPath,
        StandardTableKeyNames.DocumentName,
        "file",
        "File",
    };

    private static readonly string[] LineKeys =
    {
        StandardTableKeyNames.Line,
        "Line",
        "line",
    };

    private static readonly string[] ColumnKeys =
    {
        StandardTableKeyNames.Column,
        "Column",
        "column",
        "col",
        "Col",
    };

    private static readonly string[] AccessKindKeys =
    {
        "Read/Write",
        "readwrite",
        "readWrite",
        "referencekind",
        "referenceKind",
        "referenceaccess",
        "referenceAccess",
        "referenceKindText",
        "kind",
        "Kind",
    };

    public static IReadOnlyList<ReferenceLocation> Read(IWpfTableControl tableControl)
    {
        List<ReferenceLocation> references = new();

        foreach (ITableEntryHandle entryHandle in tableControl.Entries)
        {
            if (TryReadReference(tableControl, entryHandle, out ReferenceLocation reference))
            {
                references.Add(reference);
            }
        }

        return references;
    }

    private static bool TryReadReference(
        IWpfTableControl tableControl,
        ITableEntryHandle entryHandle,
        out ReferenceLocation reference)
    {
        reference = default;

        if (!TryGetFirstString(entryHandle, PathKeys, out string? filePath)
            || !TryGetFirstInt(entryHandle, LineKeys, out int line)
            || !TryGetFirstInt(entryHandle, ColumnKeys, out int column)
            || !TryGetAccessKind(tableControl, entryHandle, out ReferenceAccessKind accessKind))
        {
            return false;
        }

        reference = new ReferenceLocation(filePath!, line, column, accessKind);
        return true;
    }

    private static bool TryGetAccessKind(
        IWpfTableControl tableControl,
        ITableEntryHandle entryHandle,
        out ReferenceAccessKind accessKind)
    {
        if (TryGetFirstString(entryHandle, AccessKindKeys, out string? rawAccessKind)
            && TryParseAccessKind(rawAccessKind, out accessKind))
        {
            return true;
        }

        foreach (ColumnState columnState in tableControl.ColumnStates.Where(state => state.IsVisible))
        {
            if (TryGetString(entryHandle, columnState.Name, out string? columnText)
                && TryParseAccessKind(columnText, out accessKind))
            {
                return true;
            }
        }

        accessKind = default;
        return false;
    }

    private static bool TryParseAccessKind(string? text, out ReferenceAccessKind accessKind)
    {
        string normalizedText = string.IsNullOrWhiteSpace(text) ? string.Empty : text!.Trim();

        if (string.Equals(normalizedText, "Write", StringComparison.OrdinalIgnoreCase))
        {
            accessKind = ReferenceAccessKind.Write;
            return true;
        }

        if (string.Equals(normalizedText, "Read", StringComparison.OrdinalIgnoreCase))
        {
            accessKind = ReferenceAccessKind.Read;
            return true;
        }

        accessKind = default;
        return false;
    }

    private static bool TryGetFirstString(ITableEntryHandle entryHandle, IEnumerable<string> keys, out string? value)
    {
        foreach (string key in keys)
        {
            if (TryGetString(entryHandle, key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetString(ITableEntryHandle entryHandle, string key, out string? value)
    {
        if (TryGetValue(entryHandle, key, out object? rawValue) && rawValue is not null)
        {
            value = Convert.ToString(rawValue, CultureInfo.InvariantCulture);
            return value is not null && !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static bool TryGetFirstInt(ITableEntryHandle entryHandle, IEnumerable<string> keys, out int value)
    {
        foreach (string key in keys)
        {
            if (TryGetValue(entryHandle, key, out object? rawValue) && TryConvertToInt(rawValue, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryConvertToInt(object? value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case uint uintValue when uintValue <= int.MaxValue:
                result = (int)uintValue;
                return true;
            case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                result = (int)longValue;
                return true;
            case string stringValue:
                return int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetValue(ITableEntryHandle entryHandle, string key, out object? value)
    {
        if (entryHandle.TryGetEntry(out ITableEntry entry) && entry.TryGetValue(key, out value))
        {
            return true;
        }

        if (entryHandle.TryGetSnapshot(out ITableEntriesSnapshot snapshot, out int index)
            && snapshot.TryGetValue(index, key, out value))
        {
            return true;
        }

        value = null;
        return false;
    }
}

internal sealed class NullTableControlEventProcessor : ITableControlEventProcessor
{
    public static NullTableControlEventProcessor Instance { get; } = new();

    private NullTableControlEventProcessor()
    {
    }

    public void PreprocessMouseLeftButtonDown(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PostprocessMouseLeftButtonDown(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PreprocessMouseRightButtonDown(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PostprocessMouseRightButtonDown(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PreprocessMouseLeftButtonUp(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PostprocessMouseLeftButtonUp(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PreprocessMouseRightButtonUp(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PostprocessMouseRightButtonUp(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PreprocessMouseUp(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PostprocessMouseUp(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PreprocessMouseDown(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PostprocessMouseDown(ITableEntryHandle entry, System.Windows.Input.MouseButtonEventArgs e)
    {
    }

    public void PreprocessMouseMove(ITableEntryHandle entry, System.Windows.Input.MouseEventArgs e)
    {
    }

    public void PostprocessMouseMove(ITableEntryHandle entry, System.Windows.Input.MouseEventArgs e)
    {
    }

    public void PreprocessMouseWheel(ITableEntryHandle entry, System.Windows.Input.MouseWheelEventArgs e)
    {
    }

    public void PostprocessMouseWheel(ITableEntryHandle entry, System.Windows.Input.MouseWheelEventArgs e)
    {
    }

    public void PreprocessMouseEnter(ITableEntryHandle entry, System.Windows.Input.MouseEventArgs e)
    {
    }

    public void PostprocessMouseEnter(ITableEntryHandle entry, System.Windows.Input.MouseEventArgs e)
    {
    }

    public void PreprocessMouseLeave(ITableEntryHandle entry, System.Windows.Input.MouseEventArgs e)
    {
    }

    public void PostprocessMouseLeave(ITableEntryHandle entry, System.Windows.Input.MouseEventArgs e)
    {
    }

    public void PreprocessDragLeave(ITableEntryHandle entry, DragEventArgs e)
    {
    }

    public void PostprocessDragLeave(ITableEntryHandle entry, DragEventArgs e)
    {
    }

    public void PreprocessDragOver(ITableEntryHandle entry, DragEventArgs e)
    {
    }

    public void PostprocessDragOver(ITableEntryHandle entry, DragEventArgs e)
    {
    }

    public void PreprocessDragEnter(ITableEntryHandle entry, DragEventArgs e)
    {
    }

    public void PostprocessDragEnter(ITableEntryHandle entry, DragEventArgs e)
    {
    }

    public void PreprocessDrop(ITableEntryHandle entry, DragEventArgs e)
    {
    }

    public void PostprocessDrop(ITableEntryHandle entry, DragEventArgs e)
    {
    }

    public void PreprocessQueryContinueDrag(ITableEntryHandle entry, QueryContinueDragEventArgs e)
    {
    }

    public void PostprocessQueryContinueDrag(ITableEntryHandle entry, QueryContinueDragEventArgs e)
    {
    }

    public void PreprocessGiveFeedback(ITableEntryHandle entry, GiveFeedbackEventArgs e)
    {
    }

    public void PostprocessGiveFeedback(ITableEntryHandle entry, GiveFeedbackEventArgs e)
    {
    }

    public void PreprocessNavigate(ITableEntryHandle entry, TableEntryNavigateEventArgs e)
    {
    }

    public void PostprocessNavigate(ITableEntryHandle entry, TableEntryNavigateEventArgs e)
    {
    }

    public void PreprocessNavigateToHelp(ITableEntryHandle entry, TableEntryEventArgs e)
    {
    }

    public void PostprocessNavigateToHelp(ITableEntryHandle entry, TableEntryEventArgs e)
    {
    }

    public void PreprocessSelectionChanged(TableSelectionChangedEventArgs e)
    {
    }

    public void PostprocessSelectionChanged(TableSelectionChangedEventArgs e)
    {
    }

    public void PreviewKeyDown(System.Windows.Input.KeyEventArgs args)
    {
    }

    public void KeyDown(System.Windows.Input.KeyEventArgs args)
    {
    }

    public void PreviewKeyUp(System.Windows.Input.KeyEventArgs args)
    {
    }

    public void KeyUp(System.Windows.Input.KeyEventArgs args)
    {
    }
}
