using MetadataExtractor.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhotoNotes.Core.Tests;

[TestClass]
public sealed class MetadataCoverageTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "PhotoNotes-metadata-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [DataTestMethod]
    [DataRow(".dng", FileType.Dng)]
    [DataRow(".CR2", FileType.Cr2)]
    [DataRow(".nef", FileType.Nef)]
    [DataRow(".ARW", FileType.Arw)]
    public async Task ScanAsync_ExtractsMetadataFromTiffBasedRaw(string extension, FileType expectedType)
    {
        var bytes = extension.Equals(".CR2", StringComparison.OrdinalIgnoreCase)
            ? TestImages.Cr2() : TestImages.TiffWithDetails(dng: extension == ".dng");
        var path = Path.Combine(_root, "raw" + extension);
        using (var stream = new MemoryStream(bytes))
            Assert.AreEqual(expectedType, FileTypeDetector.DetectFileType(stream, path));
        await File.WriteAllBytesAsync(path, bytes);
        var result = await new ImageScanner().ScanAsync(_root);
        Assert.AreEqual(0, result.Issues.Count, string.Join("; ", result.Issues.Select(i => i.Message)));
        Assert.AreEqual(1, result.Images.Count);
        Assert.AreEqual(new DateTime(2024, 7, 18, 12, 34, 56), result.Images[0].PhotoDate);
        Assert.IsTrue(result.Images[0].HasMetadata);
        Assert.IsTrue(result.Images[0].CanCopy);
    }

    [DataTestMethod]
    [DataRow("exif:DateTimeOriginal", "XMP original")]
    [DataRow("photoshop:DateCreated", "XMP Photoshop created")]
    [DataRow("xmp:CreateDate", "XMP created")]
    [DataRow("xmp:ModifyDate", "XMP modified")]
    [DataRow("tiff:DateTime", "XMP modified")]
    public async Task ScanAsync_UsesXmpDateFallbackWithoutTimezoneConversion(string property, string dateSource)
    {
        var xmp = Xmp($"{property}=\"2023-12-31T23:59:58-10:00\"");
        var image = await ScanJpeg(TestImages.Jpeg(xmp, exifDate: "0000:00:00 00:00:00"));
        Assert.AreEqual(new DateTime(2023, 12, 31, 23, 59, 58), image.PhotoDate);
        Assert.AreEqual(DateTimeKind.Unspecified, image.PhotoDate!.Value.Kind);
        Assert.AreEqual(dateSource, image.DateSource);
        Assert.AreEqual("2023/12/31", image.SuggestedFolder);
    }

    [TestMethod]
    public async Task ScanAsync_PrefersExifDateOverXmpFallback()
    {
        var image = await ScanJpeg(TestImages.Jpeg(Xmp("xmp:CreateDate=\"2020-01-01T00:00:00Z\"")));
        Assert.AreEqual(new DateTime(2024, 7, 18, 12, 34, 56), image.PhotoDate);
        Assert.AreEqual("EXIF original", image.DateSource);
    }

    [TestMethod]
    public async Task ScanAsync_PrefersXmpCaptureDateOverXmpCreateDate()
    {
        var xmp = Xmp("exif:DateTimeOriginal=\"2021-02-03T04:05:06Z\" xmp:CreateDate=\"2022-01-01T00:00:00Z\"");
        var image = await ScanJpeg(TestImages.Jpeg(xmp, includeExif: false));
        Assert.AreEqual(new DateTime(2021, 2, 3, 4, 5, 6), image.PhotoDate);
        Assert.AreEqual("XMP original", image.DateSource);
    }

    [DataTestMethod]
    [DataRow("not-a-date")]
    [DataRow("0000-00-00T00:00:00Z")]
    public async Task ScanAsync_InvalidXmpDatesRemainUndated(string value)
    {
        var image = await ScanJpeg(TestImages.Jpeg(Xmp($"xmp:CreateDate=\"{value}\""), includeExif: false));
        Assert.IsNull(image.PhotoDate);
        Assert.AreEqual("Undated", image.SuggestedFolder);
        Assert.IsTrue(image.CanCopy);
    }

    [DataTestMethod]
    [DataRow('N', 'W', 43.5, -80.25)]
    [DataRow('S', 'E', -43.5, 80.25)]
    public async Task ScanAsync_ExposesCameraLensAndSignedGps(char latitudeRef, char longitudeRef,
        double latitude, double longitude)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "details.tiff"),
            TestImages.TiffWithDetails(latitudeRef: latitudeRef, longitudeRef: longitudeRef));
        var scan = await new ImageScanner().ScanAsync(_root);
        Assert.AreEqual(0, scan.Issues.Count, string.Join("; ", scan.Issues.Select(i => i.Message)));
        var image = scan.Images[0];
        Assert.AreEqual("PhotoNotes PN-1", image.Camera);
        Assert.AreEqual("Example 35mm", image.Lens);
        Assert.AreEqual(latitude, image.Latitude!.Value, 0.000001);
        Assert.AreEqual(longitude, image.Longitude!.Value, 0.000001);
        Assert.AreEqual(FormattableString.Invariant($"{latitude:F6}, {longitude:F6}"), image.GpsCoordinates);
    }

    [TestMethod]
    public async Task ScanAsync_UsesXmpCameraAndLensWhenExifIsAbsent()
    {
        var image = await ScanJpeg(TestImages.Jpeg(
            Xmp("tiff:Make=\"Example\" tiff:Model=\"Example C1\" aux:Lens=\"Example 50mm\""),
            includeExif: false));
        Assert.AreEqual("Example C1", image.Camera);
        Assert.AreEqual("Example 50mm", image.Lens);
        Assert.IsNull(image.Latitude);
        Assert.IsNull(image.Longitude);
        Assert.AreEqual(string.Empty, image.GpsCoordinates);
    }

    [TestMethod]
    public async Task CreatePlan_UsesXmpDateFolderForPhotosWithoutExifDates()
    {
        await ScanJpeg(TestImages.Jpeg(Xmp("xmp:CreateDate=\"2022-04-05T06:07:08+14:00\""), includeExif: false));
        var scan = await new ImageScanner().ScanAsync(_root);
        var destination = Path.Combine(Path.GetTempPath(), "PhotoNotes-plan-" + Guid.NewGuid().ToString("N"));
        var plan = new CopyService().CreatePlan(scan, destination);
        Assert.AreEqual(Path.Combine(destination, "2022", "04", "05", "image.jpg"),
            plan.Entries[0].DestinationPath);
        Assert.IsFalse(Directory.Exists(destination));
    }

    private async Task<ImageRecord> ScanJpeg(byte[] bytes)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "image.jpg"), bytes);
        var scan = await new ImageScanner().ScanAsync(_root);
        Assert.AreEqual(0, scan.Issues.Count, string.Join("; ", scan.Issues.Select(i => i.Message)));
        return scan.Images.Single();
    }

    private static string Xmp(string attributes) => $$"""
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
          <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about=""
              xmlns:exif="http://ns.adobe.com/exif/1.0/"
              xmlns:xmp="http://ns.adobe.com/xap/1.0/"
              xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
              xmlns:tiff="http://ns.adobe.com/tiff/1.0/"
              xmlns:aux="http://ns.adobe.com/exif/1.0/aux/"
              {{attributes}}/>
          </rdf:RDF>
        </x:xmpmeta>
        """;
}
