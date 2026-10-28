using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Xmp;
using IO = System.IO;

namespace PhotoNotes.Core;

public sealed class ImageScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tif", ".tiff",
        ".dng", ".cr2", ".nef", ".arw"
    };

    public Task<ScanResult> ScanAsync(string sourceRoot, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(sourceRoot, progress, cancellationToken), cancellationToken);

    private static ScanResult Scan(string sourceRoot, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        if (!IO.Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceRoot}");

        var images = new List<ImageRecord>();
        var issues = new List<ScanIssue>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    issues.Add(new(folder, "Skipped symbolic link or junction."));
                    continue;
                }

                foreach (var path in IO.Directory.EnumerateFileSystemEntries(folder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new(path, ex.Message));
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        issues.Add(new(path, "Skipped symbolic link or junction."));
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(path);
                    else if (Extensions.Contains(Path.GetExtension(path)))
                    {
                        var image = ReadImage(path, cancellationToken);
                        images.Add(image);
                        if (image.Error is not null)
                            issues.Add(new(path, image.Error));
                        progress?.Report(new(images.Count, path));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(folder, ex.Message));
            }
        }

        return new(sourceRoot, images.OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray(), issues);
    }

    private static ImageRecord ReadImage(string path, CancellationToken cancellationToken)
    {
        long size = 0;
        string? contentHash = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            size = stream.Length;
            contentHash = ImageIdentity.Hash(stream, cancellationToken);
            stream.Position = 0;
            var directories = ImageMetadataReader.ReadMetadata(stream, Path.GetFileName(path));
            var metadata = ReadEntries(directories).ToArray();
            var errors = directories.SelectMany(d => d.Errors).ToArray();
            var (date, dateSource) = GetPhotoDate(directories, metadata);
            var make = ExifText(ExifDirectoryBase.TagMake) ?? XmpText("tiff:Make") ?? string.Empty;
            var model = ExifText(ExifDirectoryBase.TagModel) ?? XmpText("tiff:Model") ?? string.Empty;
            var camera = model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                ? model : $"{make} {model}".Trim();
            var lens = ExifText(ExifDirectoryBase.TagLensModel)
                       ?? XmpText("exifEX:LensModel") ?? XmpText("aux:Lens") ?? string.Empty;
            var location = directories.OfType<GpsDirectory>().Select(d => d.GetGeoLocation())
                .FirstOrDefault(location => location is not null);
            return new(path, size, date, dateSource, ImageIdentity.Create(Path.GetFileName(path), metadata),
                contentHash, metadata, errors.Length == 0 ? null : string.Join("; ", errors),
                camera, lens, location?.Latitude, location?.Longitude);

            string? ExifText(int tag) => directories.OfType<ExifDirectoryBase>()
                .Select(d => d.GetString(tag)?.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            string? XmpText(string name) => metadata
                .FirstOrDefault(m => m.Directory == "XMP properties" && m.Name == name
                                     && !string.IsNullOrWhiteSpace(m.Value))?.Value.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ImageProcessingException or MetadataException or FormatException
                                   or InvalidOperationException)
        {
            return new(path, size, null, "Unavailable", null, contentHash, [], ex.Message);
        }
    }

    private static (DateTime?, string) GetPhotoDate(IReadOnlyList<MetadataExtractor.Directory> directories,
        IReadOnlyList<MetadataEntry> metadata)
    {
        foreach (var (tag, label) in new[]
                 {
                     (ExifDirectoryBase.TagDateTimeOriginal, "EXIF original"),
                     (ExifDirectoryBase.TagDateTimeDigitized, "EXIF digitized"),
                     (ExifDirectoryBase.TagDateTime, "EXIF modified")
                 })
        {
            foreach (var directory in directories.OfType<ExifDirectoryBase>())
            {
                if (directory.TryGetDateTime(tag, out var date))
                    return (DateTime.SpecifyKind(date, DateTimeKind.Unspecified), label);
            }
        }
        foreach (var (name, label) in new[]
                 {
                     ("exif:DateTimeOriginal", "XMP original"),
                     ("photoshop:DateCreated", "XMP Photoshop created"),
                     ("xmp:CreateDate", "XMP created"),
                     ("xmp:ModifyDate", "XMP modified")
                 })
        {
            foreach (var entry in metadata.Where(m => m.Directory == "XMP properties" && m.Name == name))
                if (DateTimeOffset.TryParse(entry.Value, CultureInfo.InvariantCulture, DateTimeStyles.None,
                        out var timestamp))
                    return (timestamp.DateTime, label);
        }
        return (null, "No EXIF/XMP date");
    }

    private static IEnumerable<MetadataEntry> ReadEntries(IEnumerable<MetadataExtractor.Directory> directories)
    {
        foreach (var directory in directories.Where(d => d is not FileTypeDirectory))
        {
            foreach (var tag in directory.Tags)
                yield return new(directory.Name, tag.Type, tag.Name, CanonicalValue(directory.GetObject(tag.Type)));
            if (directory is XmpDirectory xmp)
                foreach (var property in xmp.GetXmpProperties())
                    yield return new("XMP properties", 0, property.Key, property.Value);
        }
    }

    private static string CanonicalValue(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => Convert.ToHexStringLower(bytes),
        Array array => System.Text.Json.JsonSerializer.Serialize(array.Cast<object?>().Select(CanonicalValue)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
