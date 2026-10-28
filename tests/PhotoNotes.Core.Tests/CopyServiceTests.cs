using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhotoNotes.Core.Tests;

[TestClass]
public sealed class CopyServiceTests
{
    private string _root = null!;
    private string _source = null!;
    private string _destination = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "PhotoNotes-copy-tests-" + Guid.NewGuid().ToString("N"))).FullName;
        _source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        _destination = Path.Combine(_root, "destination");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task CopyAsync_VerifiesCopyAndLeavesSourceUnchanged()
    {
        var source = Path.Combine(_source, "image.tiff");
        var bytes = TestImages.Tiff();
        await File.WriteAllBytesAsync(source, bytes);
        var service = new CopyService();
        var scan = await new ImageScanner().ScanAsync(_source);
        var plan = service.CreatePlan(scan, _destination);
        Assert.IsFalse(Directory.Exists(_destination), "Preview must not create directories.");
        Assert.AreEqual(Path.Combine(_destination, "2024", "07", "18", "image.tiff"), plan.Entries[0].DestinationPath);

        var result = await service.CopyAsync(plan);
        Assert.AreEqual(1, result.Copied);
        Assert.AreEqual(0, result.Issues.Count);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(plan.Entries[0].DestinationPath));
    }

    [TestMethod]
    public async Task CreatePlan_OptionallyOmitsRenamedIdenticalCopies()
    {
        await File.WriteAllBytesAsync(Path.Combine(_source, "a.tiff"), TestImages.Tiff());
        await File.WriteAllBytesAsync(Path.Combine(_source, "b.tiff"), TestImages.Tiff());
        var scan = await new ImageScanner().ScanAsync(_source);
        var service = new CopyService();
        Assert.AreEqual(1, service.CreatePlan(scan, _destination).Entries.Count);
        Assert.AreEqual(2, service.CreatePlan(scan, _destination, omitIdenticalCopies: false).Entries.Count);
    }

    [TestMethod]
    public async Task CreatePlan_HandlesExistingAndPlannedNameCollisions()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_source, "nested")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(_source, "image.tiff"), TestImages.Tiff());
        await File.WriteAllBytesAsync(Path.Combine(nested, "image.tiff"), TestImages.Tiff());
        var existingDirectory = Directory.CreateDirectory(Path.Combine(_destination, "2024", "07", "18")).FullName;
        var existing = Path.Combine(existingDirectory, "image.tiff");
        await File.WriteAllTextAsync(existing, "existing file");
        var scan = await new ImageScanner().ScanAsync(_source);
        var service = new CopyService();
        var plan = service.CreatePlan(scan, _destination, omitIdenticalCopies: false);
        Assert.AreEqual(2, plan.Entries.Select(e => e.DestinationPath).Distinct().Count());
        Assert.IsTrue(plan.Entries.All(e => e.DestinationPath != existing));
        var result = await service.CopyAsync(plan);
        Assert.AreEqual(2, result.Copied);
        Assert.AreEqual("existing file", await File.ReadAllTextAsync(existing));
    }

    [TestMethod]
    public async Task CopyAsync_RejectsSourceChangedAfterPreview()
    {
        var source = Path.Combine(_source, "image.tiff");
        await File.WriteAllBytesAsync(source, TestImages.Tiff());
        var service = new CopyService();
        var plan = service.CreatePlan(await new ImageScanner().ScanAsync(_source), _destination);
        await File.WriteAllBytesAsync(source, TestImages.Tiff("2025:07:18 12:34:56"));
        var result = await service.CopyAsync(plan);
        Assert.AreEqual(0, result.Copied);
        Assert.AreEqual(1, result.Issues.Count);
        Assert.IsTrue(result.Issues[0].Message.Contains("changed"));
        Assert.IsFalse(File.Exists(plan.Entries[0].DestinationPath));
    }

    [TestMethod]
    public async Task CopyAsync_DoesNotOverwriteFileCreatedAfterPreview()
    {
        await File.WriteAllBytesAsync(Path.Combine(_source, "image.tiff"), TestImages.Tiff());
        var service = new CopyService();
        var plan = service.CreatePlan(await new ImageScanner().ScanAsync(_source), _destination);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Entries[0].DestinationPath)!);
        await File.WriteAllTextAsync(plan.Entries[0].DestinationPath, "keep me");
        var result = await service.CopyAsync(plan);
        Assert.AreEqual(0, result.Copied);
        Assert.AreEqual(1, result.Issues.Count);
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(plan.Entries[0].DestinationPath));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("nested")]
    public async Task CreatePlan_RejectsDestinationsInsideSource(string suffix)
    {
        var scan = await new ImageScanner().ScanAsync(_source);
        Assert.ThrowsException<ArgumentException>(() =>
            new CopyService().CreatePlan(scan, Path.Combine(_source, suffix)));
    }

    [TestMethod]
    public async Task CopyAsync_RejectsDestinationEscapingPlanRoot()
    {
        await File.WriteAllBytesAsync(Path.Combine(_source, "image.tiff"), TestImages.Tiff());
        var service = new CopyService();
        var plan = service.CreatePlan(await new ImageScanner().ScanAsync(_source), _destination);
        var outside = Path.Combine(_root, "outside.tiff");
        plan = plan with { Entries = [plan.Entries[0] with { DestinationPath = outside }] };
        var result = await service.CopyAsync(plan);
        Assert.AreEqual(0, result.Copied);
        Assert.AreEqual(1, result.Issues.Count);
        Assert.IsFalse(File.Exists(outside));
    }

    [TestMethod]
    public async Task CopyAsync_CancellationRetainsCompletedCountsAndPriorIssues()
    {
        await File.WriteAllBytesAsync(Path.Combine(_source, "a.tiff"), TestImages.Tiff());
        await File.WriteAllBytesAsync(Path.Combine(_source, "b.tiff"), TestImages.Tiff());
        await File.WriteAllBytesAsync(Path.Combine(_source, "c.tiff"), TestImages.Tiff());
        var service = new CopyService();
        var plan = service.CreatePlan(await new ImageScanner().ScanAsync(_source), _destination, false);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Entries[0].DestinationPath)!);
        await File.WriteAllTextAsync(plan.Entries[0].DestinationPath, "collision");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(p =>
        {
            if (p.Copied == 1) cancellation.Cancel();
        });
        var result = await service.CopyAsync(plan, progress, cancellation.Token);
        Assert.IsTrue(result.IsCanceled);
        Assert.AreEqual(1, result.Copied);
        Assert.AreEqual(1, result.Issues.Count);
        Assert.IsTrue(File.Exists(plan.Entries[1].DestinationPath));
        Assert.IsFalse(File.Exists(plan.Entries[2].DestinationPath));
        Assert.AreEqual(3, Directory.GetFiles(_source).Length);
    }

    [TestMethod]
    public async Task CopyAsync_RejectsJunctionInsertedAfterPreview()
    {
        await File.WriteAllBytesAsync(Path.Combine(_source, "image.tiff"), TestImages.Tiff());
        var service = new CopyService();
        var plan = service.CreatePlan(await new ImageScanner().ScanAsync(_source), _destination);
        var redirected = Directory.CreateDirectory(Path.Combine(_root, "redirected")).FullName;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")!,
            Arguments = $"/c mklink /J \"{_destination}\" \"{redirected}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
        try
        {
            var result = await service.CopyAsync(plan);
            Assert.AreEqual(0, result.Copied);
            Assert.AreEqual(1, result.Issues.Count);
            Assert.AreEqual(0, Directory.GetFiles(redirected, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Directory.Delete(_destination);
        }
    }

    private sealed class InlineProgress(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }
}
