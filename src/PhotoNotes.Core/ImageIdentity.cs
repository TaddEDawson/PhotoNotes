using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoNotes.Core;

public static class ImageIdentity
{
    public static string Create(string fileName, IEnumerable<MetadataEntry> metadata)
    {
        var canonical = new
        {
            Version = 1,
            FileName = Path.GetFileName(fileName).Normalize(NormalizationForm.FormC).ToUpperInvariant(),
            Tags = metadata.OrderBy(tag => tag.Directory, StringComparer.Ordinal)
                .ThenBy(tag => tag.TagId)
                .ThenBy(tag => tag.Name, StringComparer.Ordinal)
                .ThenBy(tag => tag.Value, StringComparer.Ordinal)
                .Select(tag => new { tag.Directory, tag.TagId, tag.Name, tag.Value })
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    internal static string Hash(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
