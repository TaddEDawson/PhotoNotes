namespace PhotoNotes.Core;

public sealed record MetadataEntry(string Directory, int TagId, string Name, string Value);

public sealed record ImageRecord(
    string SourcePath,
    long Size,
    DateTime? PhotoDate,
    string DateSource,
    string? MetadataId,
    string? ContentHash,
    IReadOnlyList<MetadataEntry> Metadata,
    string? Error)
{
    public string FileName => Path.GetFileName(SourcePath);
    public bool HasMetadata => Metadata.Count > 0;
    public bool CanCopy => Error is null && ContentHash is not null;
    public string SuggestedFolder => PhotoDate?.ToString("yyyy/MM/dd",
        System.Globalization.CultureInfo.InvariantCulture) ?? "Undated";
}

public sealed record ScanIssue(string Path, string Message);
public sealed record ScanProgress(int FilesProcessed, string CurrentPath);

public sealed record ScanResult(
    string SourceRoot,
    IReadOnlyList<ImageRecord> Images,
    IReadOnlyList<ScanIssue> Issues)
{
    public int DuplicateFiles => Images.Where(i => i.ContentHash is not null)
        .GroupBy(i => i.ContentHash, StringComparer.Ordinal)
        .Sum(group => group.Count() - 1);
}

public sealed record CopyPlanEntry(ImageRecord Image, string DestinationPath);
public sealed record CopyPlan(string SourceRoot, string DestinationRoot, IReadOnlyList<CopyPlanEntry> Entries);
public sealed record CopyProgress(int Copied, int Total, string CurrentPath);
public sealed record CopyResult(int Copied, IReadOnlyList<ScanIssue> Issues, bool IsCanceled = false);
