using System.Text;
using TinyCosmos.Core;
using TinyCosmos.Protocol;

namespace TinyCosmos.Manager;

public interface IGuestFileService
{
    Task<FileReadResult> ReadAsync(GroupBundle target, FileReadPayload payload, CancellationToken cancellationToken);

    Task<FileWriteResult> WriteAsync(GroupBundle target, FileWritePayload payload, CancellationToken cancellationToken);

    Task<FileListResult> ListAsync(GroupBundle target, FileListPayload payload, CancellationToken cancellationToken);

    Task<FileSearchResult> SearchAsync(GroupBundle target, FileSearchPayload payload, CancellationToken cancellationToken);
}

public sealed class NotReadyGuestFileService : IGuestFileService
{
    public Task<FileReadResult> ReadAsync(GroupBundle target, FileReadPayload payload, CancellationToken cancellationToken) => NotReady<FileReadResult>();

    public Task<FileWriteResult> WriteAsync(GroupBundle target, FileWritePayload payload, CancellationToken cancellationToken) => NotReady<FileWriteResult>();

    public Task<FileListResult> ListAsync(GroupBundle target, FileListPayload payload, CancellationToken cancellationToken) => NotReady<FileListResult>();

    public Task<FileSearchResult> SearchAsync(GroupBundle target, FileSearchPayload payload, CancellationToken cancellationToken) => NotReady<FileSearchResult>();

    private static Task<T> NotReady<T>()
    {
        throw new TinyCosmosException(new TinyCosmosError(
            TinyCosmosErrorCode.NotReady,
            "Guest file operations require a verified SSH/SFTP data plane for the sandbox."));
    }
}

public sealed class RecordingGuestFileService : IGuestFileService
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public List<object> Requests { get; } = [];

    public Task<FileReadResult> ReadAsync(GroupBundle target, FileReadPayload payload, CancellationToken cancellationToken)
    {
        Requests.Add(payload);
        _files.TryGetValue(payload.Path, out var bytes);
        bytes ??= Encoding.UTF8.GetBytes("recorded:" + payload.Path);
        var truncated = bytes.Length > payload.MaxBytes;
        var returned = truncated ? bytes.AsSpan(0, payload.MaxBytes).ToArray() : bytes;
        return Task.FromResult(new FileReadResult(payload.Path, Convert.ToBase64String(returned), truncated));
    }

    public Task<FileWriteResult> WriteAsync(GroupBundle target, FileWritePayload payload, CancellationToken cancellationToken)
    {
        Requests.Add(payload);
        var bytes = Convert.FromBase64String(payload.ContentBase64);
        if (!payload.Overwrite && _files.ContainsKey(payload.Path))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Guest file already exists."));
        }

        _files[payload.Path] = bytes;
        return Task.FromResult(new FileWriteResult(payload.Path, bytes.Length));
    }

    public Task<FileListResult> ListAsync(GroupBundle target, FileListPayload payload, CancellationToken cancellationToken)
    {
        Requests.Add(payload);
        var entries = _files.Keys
            .Where(path => path.StartsWith(payload.Path.TrimEnd('/') + "/", StringComparison.Ordinal) || path == payload.Path)
            .Order(StringComparer.Ordinal)
            .Take(payload.MaxEntries + 1)
            .Select(path => new FileEntry(path, IsDirectory: false, _files[path].Length, Executable: false, ModifiedAt: null))
            .ToArray();
        var truncated = entries.Length > payload.MaxEntries;
        return Task.FromResult(new FileListResult(payload.Path, entries.Take(payload.MaxEntries).ToArray(), truncated));
    }

    public Task<FileSearchResult> SearchAsync(GroupBundle target, FileSearchPayload payload, CancellationToken cancellationToken)
    {
        Requests.Add(payload);
        var matches = _files
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => Encoding.UTF8.GetString(pair.Value)
                .Split('\n')
                .Select((line, index) => (pair.Key, LineNumber: index + 1, Line: line)))
            .Where(line => line.Key.StartsWith(payload.Path.TrimEnd('/') + "/", StringComparison.Ordinal) && line.Line.Contains(payload.Pattern, StringComparison.Ordinal))
            .Take(payload.MaxMatches + 1)
            .Select(line => new FileSearchMatch(line.Key, line.LineNumber, line.Line))
            .ToArray();
        var truncated = matches.Length > payload.MaxMatches;
        return Task.FromResult(new FileSearchResult(matches.Take(payload.MaxMatches).ToArray(), truncated));
    }
}
