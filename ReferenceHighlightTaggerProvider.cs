using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace ReferenceHighlight;

internal static class ReferenceHighlightMarkerNames
{
    public const string Read = "MarkerFormatDefinition/ReferenceHighlight.Read";
    public const string Write = "MarkerFormatDefinition/ReferenceHighlight.Write";
}

[Export(typeof(EditorFormatDefinition))]
[Name(ReferenceHighlightMarkerNames.Read)]
[UserVisible(true)]
internal sealed class ReadReferenceMarkerDefinition : MarkerFormatDefinition
{
    public ReadReferenceMarkerDefinition()
    {
        DisplayName = "Find All References Read";
        Fill = CreateBrush(0x20, 0xE0, 0x00, 0xF0);
        Border = CreatePen(0x14, 0x8A, 0x00, 0xFF);
        ZOrder = 50;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a)
    {
        SolidColorBrush brush = new(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(byte r, byte g, byte b, byte a)
    {
        Pen pen = new(CreateBrush(r, g, b, a), 1.0);
        pen.Freeze();
        return pen;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name(ReferenceHighlightMarkerNames.Write)]
[UserVisible(true)]
internal sealed class WriteReferenceMarkerDefinition : MarkerFormatDefinition
{
    public WriteReferenceMarkerDefinition()
    {
        DisplayName = "Find All References Write";
        Fill = CreateBrush(0xF0, 0x00, 0x8A, 0xF0);
        Border = CreatePen(0x9E, 0x00, 0x5B, 0xFF);
        ZOrder = 50;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a)
    {
        SolidColorBrush brush = new(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(byte r, byte g, byte b, byte a)
    {
        Pen pen = new(CreateBrush(r, g, b, a), 1.0);
        pen.Freeze();
        return pen;
    }
}

[Export(typeof(IViewTaggerProvider))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
[TagType(typeof(TextMarkerTag))]
internal sealed class ReferenceHighlightTaggerProvider : IViewTaggerProvider
{
    [Import]
    internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; } = null!;

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer)
        where T : ITag
    {
        if (buffer != textView.TextBuffer)
        {
            return null;
        }

        return buffer.Properties.GetOrCreateSingletonProperty(
            () => new ReferenceHighlightTagger(buffer, TextDocumentFactoryService)) as ITagger<T>;
    }
}

internal sealed class ReferenceHighlightTagger : ITagger<TextMarkerTag>, IDisposable
{
    private readonly ITextBuffer buffer;
    private readonly ITextDocumentFactoryService textDocumentFactoryService;

    public ReferenceHighlightTagger(ITextBuffer buffer, ITextDocumentFactoryService textDocumentFactoryService)
    {
        this.buffer = buffer;
        this.textDocumentFactoryService = textDocumentFactoryService;
        ReferenceHighlightStore.Changed += OnReferencesChanged;
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0 || !textDocumentFactoryService.TryGetTextDocument(buffer, out ITextDocument textDocument))
        {
            yield break;
        }

        ITextSnapshot snapshot = spans[0].Snapshot;
        IReadOnlyList<ReferenceLocation> references = ReferenceHighlightStore.GetReferencesForFile(textDocument.FilePath);

        foreach (ReferenceLocation reference in references)
        {
            SnapshotSpan? referenceSpan = TryCreateSpan(snapshot, reference);
            if (referenceSpan is not SnapshotSpan span || !Intersects(spans, span))
            {
                continue;
            }

            string markerType = reference.AccessKind == ReferenceAccessKind.Write
                ? ReferenceHighlightMarkerNames.Write
                : ReferenceHighlightMarkerNames.Read;

            yield return new TagSpan<TextMarkerTag>(span, new TextMarkerTag(markerType));
        }
    }

    public void Dispose()
    {
        ReferenceHighlightStore.Changed -= OnReferencesChanged;
    }

    private void OnReferencesChanged(object? sender, EventArgs e)
    {
        ITextSnapshot snapshot = buffer.CurrentSnapshot;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    private static SnapshotSpan? TryCreateSpan(ITextSnapshot snapshot, ReferenceLocation reference)
    {
        int lineNumber = reference.Line - 1;
        int columnNumber = reference.Column - 1;

        if (lineNumber < 0 || lineNumber >= snapshot.LineCount)
        {
            return null;
        }

        ITextSnapshotLine line = snapshot.GetLineFromLineNumber(lineNumber);
        if (columnNumber < 0 || columnNumber >= line.Length)
        {
            return null;
        }

        int start = line.Start.Position + columnNumber;
        start = MoveToIdentifierStart(snapshot, line, start);
        int end = MoveToIdentifierEnd(snapshot, line, start);

        return end > start ? new SnapshotSpan(snapshot, Span.FromBounds(start, end)) : null;
    }

    private static int MoveToIdentifierStart(ITextSnapshot snapshot, ITextSnapshotLine line, int position)
    {
        int lineStart = line.Start.Position;
        int lineEnd = line.End.Position;

        if (position < lineEnd && IsIdentifierChar(snapshot[position]))
        {
            return position;
        }

        for (int offset = 1; offset <= 2; offset++)
        {
            int before = position - offset;
            if (before >= lineStart && IsIdentifierChar(snapshot[before]))
            {
                return before;
            }

            int after = position + offset;
            if (after < lineEnd && IsIdentifierChar(snapshot[after]))
            {
                return after;
            }
        }

        return position;
    }

    private static int MoveToIdentifierEnd(ITextSnapshot snapshot, ITextSnapshotLine line, int start)
    {
        int end = start;
        int lineEnd = line.End.Position;

        while (end < lineEnd && IsIdentifierChar(snapshot[end]))
        {
            end++;
        }

        return end;
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    private static bool Intersects(NormalizedSnapshotSpanCollection spans, SnapshotSpan referenceSpan)
    {
        foreach (SnapshotSpan span in spans)
        {
            if (span.IntersectsWith(referenceSpan))
            {
                return true;
            }
        }

        return false;
    }
}
