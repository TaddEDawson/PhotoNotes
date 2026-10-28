using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhotoNotes.Core.Tests;

[TestClass]
public sealed class NoteStoreTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "PhotoNotes-notes-tests-" +
                                                                     Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task LoadAsync_MissingNoteIsEmptyAndDoesNotCreateStorage()
    {
        Assert.AreEqual(string.Empty, await new NoteStore(_root).LoadAsync(Hash));
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public async Task SaveAsync_PersistsFullLengthUnicodeAndMultilineDescription()
    {
        var text = "\u00e9\u4e2d\r\n" + new string('x', 2044);
        Assert.AreEqual(2048, text.Length);
        await new NoteStore(_root).SaveAsync(Hash.ToUpperInvariant(), text);
        Assert.AreEqual(text, await new NoteStore(_root).LoadAsync(Hash));
        Assert.AreEqual(1, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task SaveAsync_RejectsOverLimitWithoutReplacingSavedNote()
    {
        var store = new NoteStore(_root);
        await store.SaveAsync(Hash, "keep this");
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.SaveAsync(Hash, new string('x', 2049)));
        Assert.AreEqual("keep this", await store.LoadAsync(Hash));
    }

    [TestMethod]
    public async Task SaveAsync_EmptyDescriptionClearsExistingNote()
    {
        var store = new NoteStore(_root);
        await store.SaveAsync(Hash, "old");
        await store.SaveAsync(Hash, string.Empty);
        Assert.AreEqual(string.Empty, await new NoteStore(_root).LoadAsync(Hash));
        Assert.AreEqual(1, Directory.GetFiles(_root).Length);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not-a-hash")]
    [DataRow("................................................................")]
    [DataRow("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task Notes_RejectInvalidIdentifiers(string hash)
    {
        var store = new NoteStore(_root);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.SaveAsync(hash, "note"));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.LoadAsync(hash));
        Assert.IsFalse(Directory.Exists(_root));
    }

    [DataTestMethod]
    [DataRow("{broken json")]
    [DataRow("null")]
    [DataRow("{}")]
    public async Task LoadAsync_ReportsCorruptDataWithoutModifyingIt(string json)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Hash + ".json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => new NoteStore(_root).LoadAsync(Hash));
        Assert.AreEqual(json, await File.ReadAllTextAsync(path));
    }

    [DataTestMethod]
    [DataRow(2, Hash, "note")]
    [DataRow(1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "note")]
    public async Task LoadAsync_RejectsUnknownVersionAndMismatchedHash(int version, string hash, string text)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, Hash + ".json"),
            JsonSerializer.Serialize(new { Version = version, ContentHash = hash, Description = text }));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => new NoteStore(_root).LoadAsync(Hash));
    }

    [TestMethod]
    public async Task LoadAsync_RejectsOverLimitDescription()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, Hash + ".json"), JsonSerializer.Serialize(
            new { Version = 1, ContentHash = Hash, Description = new string('x', 2049) }));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => new NoteStore(_root).LoadAsync(Hash));
    }

    [TestMethod]
    public async Task SaveAsync_CancellationKeepsPreviousNote()
    {
        var store = new NoteStore(_root);
        await store.SaveAsync(Hash, "keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => store.SaveAsync(Hash, "discard", cancellation.Token));
        Assert.AreEqual("keep", await store.LoadAsync(Hash));
        Assert.AreEqual(1, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task SaveAsync_ReportsWriteFailureInsteadOfSuccess()
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "keep");
        await Assert.ThrowsExceptionAsync<IOException>(() => new NoteStore(blocked).SaveAsync(Hash, "note"));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(blocked));
    }

    [TestMethod]
    public async Task SaveAsync_FailedReplacementKeepsPriorNoteAndRemovesTemporaryFile()
    {
        var store = new NoteStore(_root);
        await store.SaveAsync(Hash, "keep");
        using (var locked = new FileStream(Path.Combine(_root, Hash + ".json"),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsExceptionAsync<IOException>(() => store.SaveAsync(Hash, "discard"));
        Assert.AreEqual("keep", await store.LoadAsync(Hash));
        Assert.AreEqual(1, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task Notes_DifferentContentHasIndependentDescriptions()
    {
        var store = new NoteStore(_root);
        const string otherHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await store.SaveAsync(Hash, "first");
        Assert.AreEqual(string.Empty, await store.LoadAsync(otherHash));
        await store.SaveAsync(otherHash, "second");
        Assert.AreEqual("first", await store.LoadAsync(Hash));
        Assert.AreEqual("second", await store.LoadAsync(otherHash));
    }

    [TestMethod]
    public async Task Notes_FollowRenamedAndCopiedImagesWithoutChangingSourceOrIdentity()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var bytes = TestImages.Tiff();
        var firstPath = Path.Combine(source, "first.tiff");
        await File.WriteAllBytesAsync(firstPath, bytes);
        var scan = await new ImageScanner().ScanAsync(source);
        var image = scan.Images[0];
        var store = new NoteStore(Path.Combine(_root, "notes"));
        await store.SaveAsync(image.ContentHash!, "Shared description");
        var copyService = new CopyService();
        var destination = Path.Combine(_root, "copies");
        var copied = await copyService.CopyAsync(copyService.CreatePlan(scan, destination));
        Assert.AreEqual(1, copied.Copied);
        File.Move(firstPath, Path.Combine(source, "renamed.tiff"));

        var renamed = (await new ImageScanner().ScanAsync(source)).Images[0];
        var copy = (await new ImageScanner().ScanAsync(destination)).Images[0];
        Assert.AreNotEqual(image.MetadataId, renamed.MetadataId);
        Assert.AreEqual(image.MetadataId, copy.MetadataId);
        Assert.AreEqual(image.ContentHash, renamed.ContentHash);
        Assert.AreEqual(image.ContentHash, copy.ContentHash);
        Assert.AreEqual("Shared description", await store.LoadAsync(renamed.ContentHash!));
        Assert.AreEqual("Shared description", await store.LoadAsync(copy.ContentHash!));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(renamed.SourcePath));
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), renamed.ContentHash);
    }
}
