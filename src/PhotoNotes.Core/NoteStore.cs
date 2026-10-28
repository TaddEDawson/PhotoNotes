using System.Text.Json;

namespace PhotoNotes.Core;

public sealed class NoteStore(string directory)
{
    public const int MaxDescriptionLength = 2048;
    private readonly string _directory = Path.GetFullPath(directory);

    public Task<string> LoadAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        var hash = NormalizeHash(contentHash);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json;
            try
            {
                json = File.ReadAllText(Path.Combine(_directory, hash + ".json"));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return string.Empty;
            }

            ImageNote? note;
            try
            {
                note = JsonSerializer.Deserialize<ImageNote>(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Invalid notes file for {hash}: {ex.Message}", ex);
            }
            if (note is null || note.Version != 1 || note.ContentHash != hash
                || note.Description is null || note.Description.Length > MaxDescriptionLength)
                throw new InvalidDataException($"Invalid or unsupported notes file for {hash}.");
            return note.Description;
        }, cancellationToken);
    }

    public Task SaveAsync(string contentHash, string description, CancellationToken cancellationToken = default)
    {
        var hash = NormalizeHash(contentHash);
        ArgumentNullException.ThrowIfNull(description);
        if (description.Length > MaxDescriptionLength)
            throw new ArgumentException($"Notes must be at most {MaxDescriptionLength} characters.", nameof(description));

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            var destination = Path.Combine(_directory, hash + ".json");
            var temporary = Path.Combine(_directory, $"{hash}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new ImageNote(1, hash, description));
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Publish only a complete file, leaving the previous note intact if writing fails.
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }, cancellationToken);
    }

    private static string NormalizeHash(string contentHash)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        if (contentHash.Length != 64 || !contentHash.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A complete SHA-256 content hash is required.", nameof(contentHash));
        return contentHash.ToLowerInvariant();
    }

    private sealed record ImageNote(int Version, string ContentHash, string Description);
}
