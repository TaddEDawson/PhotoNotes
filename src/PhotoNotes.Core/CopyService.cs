using System.Globalization;

namespace PhotoNotes.Core;

public sealed class CopyService
{
    public CopyPlan CreatePlan(ScanResult scan, string destinationRoot, bool omitIdenticalCopies = true)
    {
        destinationRoot = Path.GetFullPath(destinationRoot);
        if (IsWithin(destinationRoot, scan.SourceRoot) || IsWithin(scan.SourceRoot, destinationRoot))
            throw new ArgumentException("Source and destination must be separate, non-nested directories.");
        EnsureNoReparsePoints(destinationRoot);

        var entries = new List<CopyPlanEntry>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in scan.Images.Where(i => i.CanCopy))
        {
            if (omitIdenticalCopies && !seenHashes.Add(image.ContentHash!))
                continue;

            var directory = image.PhotoDate is { } date
                ? Path.Combine(destinationRoot, date.ToString("yyyy", CultureInfo.InvariantCulture),
                    date.ToString("MM", CultureInfo.InvariantCulture), date.ToString("dd", CultureInfo.InvariantCulture))
                : Path.Combine(destinationRoot, "Undated");
            var candidate = Path.Combine(directory, image.FileName);
            var suffix = 0;
            while (File.Exists(candidate) || Directory.Exists(candidate) || !reserved.Add(candidate))
            {
                suffix++;
                var name = $"{Path.GetFileNameWithoutExtension(image.FileName)}_{image.ContentHash![..12]}_{suffix}";
                candidate = Path.Combine(directory, name + Path.GetExtension(image.FileName));
            }
            entries.Add(new(image, candidate));
        }
        return new(scan.SourceRoot, destinationRoot, entries);
    }

    public async Task<CopyResult> CopyAsync(CopyPlan plan, IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsWithin(plan.DestinationRoot, plan.SourceRoot) || IsWithin(plan.SourceRoot, plan.DestinationRoot))
            throw new ArgumentException("Source and destination must be separate, non-nested directories.");
        var copied = 0;
        var issues = new List<ScanIssue>();
        foreach (var entry in plan.Entries)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(copied, issues, IsCanceled: true);
            try
            {
                await CopyOneAsync(plan, entry, cancellationToken).ConfigureAwait(false);
                copied++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(copied, issues, IsCanceled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                issues.Add(new(entry.Image.SourcePath, ex.Message));
            }
            progress?.Report(new(copied, plan.Entries.Count, entry.DestinationPath));
        }
        return new(copied, issues, IsCanceled: cancellationToken.IsCancellationRequested);
    }

    private static async Task CopyOneAsync(CopyPlan plan, CopyPlanEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.Image.CanCopy || !IsWithin(entry.Image.SourcePath, plan.SourceRoot)
            || !IsWithin(entry.DestinationPath, plan.DestinationRoot)
            || Path.GetFullPath(entry.DestinationPath).Equals(Path.GetFullPath(plan.DestinationRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Copy entry is outside the plan roots or is not a valid image.");

        EnsureNoReparsePoints(entry.Image.SourcePath);
        EnsureNoReparsePoints(entry.DestinationPath);
        using var source = new FileStream(entry.Image.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (ImageIdentity.Hash(source, cancellationToken) != entry.Image.ContentHash)
            throw new IOException("Source changed since scanning. Scan again before copying.");
        source.Position = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(entry.DestinationPath)!);
        EnsureNoReparsePoints(entry.DestinationPath);

        // CreateNew is the final guard against destination files appearing after preview.
        await using var target = new FileStream(entry.DestinationPath, FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        try
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Position = 0;
            if (ImageIdentity.Hash(target, cancellationToken) != entry.Image.ContentHash)
                throw new IOException("Copied file verification failed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await target.DisposeAsync().ConfigureAwait(false);
            try
            {
                File.Delete(entry.DestinationPath);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Copy failed: {ex.Message} Partial file could not be removed: " +
                    $"{entry.DestinationPath}. {cleanupError.Message}", ex);
            }
            throw;
        }
    }

    private static bool IsWithin(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Symbolic links and junctions are not allowed in copy paths: {current}");
        }
    }
}
