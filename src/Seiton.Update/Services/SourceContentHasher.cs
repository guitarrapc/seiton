using System.Security.Cryptography;
using System.Text;

namespace Seiton.Update.Services;

/// UTF-8 string SHA-256 for committed source text (Stage 1 <c>rawFileHashes</c> contract).
internal static class SourceContentHasher
{
    public static string ComputeSha256(string utf8Text)
    {
        var bytes = Encoding.UTF8.GetBytes(utf8Text);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }
}
