using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReferenceHighlight;

internal enum ReferenceAccessKind
{
    Read,
    Write,
}

internal readonly struct ReferenceLocation
{
    public ReferenceLocation(string filePath, int line, int column, ReferenceAccessKind accessKind)
    {
        FilePath = filePath;
        Line = line;
        Column = column;
        AccessKind = accessKind;
    }

    public string FilePath { get; }

    public int Line { get; }

    public int Column { get; }

    public ReferenceAccessKind AccessKind { get; }
}

internal static class ReferenceHighlightStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, List<ReferenceLocation>> referencesByPath = new(StringComparer.OrdinalIgnoreCase);
    private static List<ReferenceLocation> allReferences = new();

    public static event EventHandler? Changed;

    public static IReadOnlyList<ReferenceLocation> GetReferencesForFile(string filePath)
    {
        string normalizedPath = NormalizePath(filePath);

        lock (Gate)
        {
            if (referencesByPath.TryGetValue(normalizedPath, out List<ReferenceLocation>? references))
            {
                return references.ToArray();
            }

            string fileName = Path.GetFileName(normalizedPath);
            return allReferences
                .Where(reference => string.Equals(Path.GetFileName(reference.FilePath), fileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    public static void ReplaceAll(IEnumerable<ReferenceLocation> references)
    {
        Dictionary<string, List<ReferenceLocation>> next = references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.FilePath))
            .GroupBy(reference => NormalizePath(reference.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        lock (Gate)
        {
            referencesByPath = next;
            allReferences = next.Values.SelectMany(value => value).ToList();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string NormalizePath(string filePath)
    {
        try
        {
            return Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return filePath.Trim();
        }
    }
}
