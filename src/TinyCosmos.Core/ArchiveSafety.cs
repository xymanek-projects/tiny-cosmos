using System.Text;

namespace TinyCosmos.Core;

public enum ArchiveEntryKind
{
    Directory,
    RegularFile,
    Symlink
}

public sealed record ArchiveEntrySpec(string RelativePath, ArchiveEntryKind Kind, string? SymlinkTarget = null);

public static class ArchiveSafety
{
    public static TinyCosmosError? ValidateEntry(ArchiveEntrySpec entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RelativePath))
        {
            return Rejected("Archive path is empty.");
        }

        if (entry.RelativePath.StartsWith("/", StringComparison.Ordinal) ||
            entry.RelativePath.Contains("\\", StringComparison.Ordinal) ||
            entry.RelativePath.Contains("\0", StringComparison.Ordinal))
        {
            return Rejected("Archive path must be relative, slash-separated, and NUL-free.");
        }

        if (Encoding.UTF8.GetByteCount(entry.RelativePath) > 4096)
        {
            return Rejected("Archive path exceeds the maximum encoded length.");
        }

        var parts = entry.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Rejected("Archive path has no usable segments.");
        }

        foreach (var part in parts)
        {
            if (part is "." or "..")
            {
                return Rejected("Archive path cannot contain traversal segments.");
            }

            if (part.Equals(".git", StringComparison.Ordinal))
            {
                return Rejected("Archive entries cannot materialize Git administrative paths.");
            }
        }

        if (entry.Kind == ArchiveEntryKind.Symlink)
        {
            if (string.IsNullOrEmpty(entry.SymlinkTarget))
            {
                return Rejected("Symlink target is required.");
            }

            if (entry.SymlinkTarget.StartsWith("/", StringComparison.Ordinal) ||
                entry.SymlinkTarget.Contains("\0", StringComparison.Ordinal))
            {
                return Rejected("Symlink target must be relative and NUL-free.");
            }

            foreach (var part in entry.SymlinkTarget.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or "..")
                {
                    return Rejected("Symlink target cannot traverse outside the export root.");
                }
            }
        }

        return null;
    }

    public static TinyCosmosError? ValidateNoDuplicateNormalizedPaths(IEnumerable<ArchiveEntrySpec> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var normalized = string.Join('/', entry.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
            if (!seen.Add(normalized))
            {
                return Rejected("Archive contains duplicate normalized paths.");
            }
        }

        return null;
    }

    private static TinyCosmosError Rejected(string message) => new(TinyCosmosErrorCode.ExportRejected, message);
}
