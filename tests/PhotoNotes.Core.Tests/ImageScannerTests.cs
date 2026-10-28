using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhotoNotes.Core.Tests;

[TestClass]
public sealed class ImageScannerTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "PhotoNotes-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task ScanAsync_RecursesAndFindsRenamedDuplicates()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(_root, "photo.TIFF"), TestImages.Tiff());
        await File.WriteAllBytesAsync(Path.Combine(nested, "renamed.tif"), TestImages.Tiff());
        await File.WriteAllTextAsync(Path.Combine(nested, "ignored.txt"), "not an image");

        var result = await new ImageScanner().ScanAsync(_root);

        Assert.AreEqual(2, result.Images.Count);
        Assert.AreEqual(1, result.DuplicateFiles);
        Assert.AreEqual(0, result.Issues.Count);
        Assert.AreEqual(result.Images[0].ContentHash, result.Images[1].ContentHash);
        Assert.AreNotEqual(result.Images[0].MetadataId, result.Images[1].MetadataId);
        foreach (var image in result.Images)
        {
            Assert.AreEqual(new DateTime(2024, 7, 18, 12, 34, 56), image.PhotoDate);
            Assert.AreEqual("2024/07/18", image.SuggestedFolder);
            Assert.IsTrue(image.HasMetadata);
            Assert.IsTrue(image.CanCopy);
            Assert.AreEqual(64, image.MetadataId!.Length);
            Assert.AreEqual(64, image.ContentHash!.Length);
            Assert.IsTrue(image.Metadata.Any(m => m.Name == "Make" && m.Value == "PhotoNotes"));
        }
    }

    [DataTestMethod]
    [DataRow(".jpg")]
    [DataRow(".png")]
    [DataRow(".heic")]
    [DataRow(".tiff")]
    public async Task ScanAsync_ExtractsExifFromSupportedContainers(string extension)
    {
        var bytes = extension switch
        {
            ".jpg" => TestImages.Jpeg(),
            ".png" => TestImages.Png(),
            ".heic" => TestImages.Heic(),
            _ => TestImages.Tiff()
        };
        await File.WriteAllBytesAsync(Path.Combine(_root, "sample" + extension), bytes);
        var scan = await new ImageScanner().ScanAsync(_root);
        Assert.AreEqual(0, scan.Issues.Count, string.Join("; ", scan.Issues.Select(i => i.Message)));
        Assert.AreEqual(1, scan.Images.Count);
        Assert.AreEqual(new DateTime(2024, 7, 18, 12, 34, 56), scan.Images[0].PhotoDate);
        Assert.IsTrue(scan.Images[0].CanCopy);
    }

    [TestMethod]
    public async Task ScanAsync_ReportsCorruptImagesAndContinues()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "bad.png"), "not actually an image");
        await File.WriteAllBytesAsync(Path.Combine(_root, "good.tiff"), TestImages.Tiff());
        var result = await new ImageScanner().ScanAsync(_root);
        Assert.AreEqual(2, result.Images.Count);
        Assert.AreEqual(1, result.Issues.Count);
        Assert.IsFalse(result.Images.Single(i => i.FileName == "bad.png").CanCopy);
    }

    [TestMethod]
    public async Task ScanAsync_ReportsBrokenHeicReferencesAndContinues()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "bad.heic"), TestImages.Heic(brokenReference: true));
        await File.WriteAllBytesAsync(Path.Combine(_root, "good.tiff"), TestImages.Tiff());
        var result = await new ImageScanner().ScanAsync(_root);
        Assert.AreEqual(2, result.Images.Count);
        Assert.AreEqual(1, result.Issues.Count);
        Assert.IsFalse(result.Images.Single(i => i.FileName == "bad.heic").CanCopy);
    }

    [TestMethod]
    public async Task ScanAsync_DoesNotTreatMatchingMetadataAsIdenticalContent()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        var bytes = TestImages.Tiff();
        await File.WriteAllBytesAsync(Path.Combine(_root, "image.tiff"), bytes);
        bytes[^1] = 128;
        await File.WriteAllBytesAsync(Path.Combine(nested, "image.tiff"), bytes);
        var result = await new ImageScanner().ScanAsync(_root);
        Assert.AreEqual(result.Images[0].MetadataId, result.Images[1].MetadataId);
        Assert.AreNotEqual(result.Images[0].ContentHash, result.Images[1].ContentHash);
        Assert.AreEqual(0, result.DuplicateFiles);
    }

    [TestMethod]
    public async Task ScanAsync_MetadataIdsDoNotDependOnCurrentCulture()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "image.tiff"), TestImages.Tiff());
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var first = await new ImageScanner().ScanAsync(_root);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = await new ImageScanner().ScanAsync(_root);
            Assert.AreEqual(first.Images[0].MetadataId, second.Images[0].MetadataId);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public async Task ScanAsync_UsesUndatedWhenExifDateIsInvalid()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "unknown.tiff"), TestImages.Tiff("0000:00:00 00:00:00"));
        var result = await new ImageScanner().ScanAsync(_root);
        Assert.IsNull(result.Images[0].PhotoDate);
        Assert.AreEqual("Undated", result.Images[0].SuggestedFolder);
    }

    [TestMethod]
    public async Task ScanAsync_ExtractsXmpPropertiesNotJustTheirCount()
    {
        const string xmp = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" dc:format="image/jpeg"/>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        await File.WriteAllBytesAsync(Path.Combine(_root, "xmp.jpg"), TestImages.Jpeg(xmp));
        var result = await new ImageScanner().ScanAsync(_root);
        Assert.AreEqual(0, result.Issues.Count);
        Assert.IsTrue(result.Images[0].Metadata.Any(m => m.Name == "dc:format" && m.Value == "image/jpeg"));
    }

    [TestMethod]
    public void CreateId_IsIndependentOfMetadataOrderCultureAndPath()
    {
        MetadataEntry[] tags = [new("EXIF", 2, "Model", "Camera"), new("EXIF", 1, "Make", "Maker")];
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var first = ImageIdentity.Create(Path.Combine("one", "image.jpg"), tags);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = ImageIdentity.Create(Path.Combine("two", "IMAGE.JPG"), tags.Reverse());
            Assert.AreEqual(first, second);
            Assert.AreNotEqual(first, ImageIdentity.Create("image.jpg", [tags[0] with { Value = "Other" }, tags[1]]));
            Assert.AreNotEqual(first, ImageIdentity.Create("image.jpg", [tags[0] with { Name = "Other" }, tags[1]]));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public async Task ScanAsync_CancellationIsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            new ImageScanner().ScanAsync(_root, cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task ScanAsync_SkipsDirectoryLinks()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "actual")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(target, "image.tiff"), TestImages.Tiff());
        var link = Path.Combine(_root, "loop");
        // Junctions do not require the symlink privilege on Windows.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")!,
            Arguments = $"/c mklink /J \"{link}\" \"{_root}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
        try
        {
            var scan = await new ImageScanner().ScanAsync(_root);
            Assert.AreEqual(1, scan.Images.Count);
            Assert.IsTrue(scan.Issues.Any(i => i.Path == link && i.Message.Contains("junction")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
