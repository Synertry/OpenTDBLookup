using System.IO.Hashing;
using System.Text;

namespace OpenTDBLookup.Helpers;

/// <summary>
/// 64-bit XxHash3 wrapper used as the primary lookup key for cached questions.
/// 64 bits is wide enough that collisions are vanishingly unlikely at the
/// ~50k-question scale of OpenTDB while keeping the on-disk JSON compact.
/// </summary>
public static class HashHelper
{
    /// <summary>
    /// Returns the XxHash3-64 of <paramref name="normalizedText"/> as a 16-char
    /// lowercase hex string. The caller is responsible for normalizing the
    /// input via <see cref="TextNormalizer.Normalize"/> first.
    /// </summary>
    public static string ComputeHash(string normalizedText)
    {
        var bytes = Encoding.UTF8.GetBytes(normalizedText);
        var hash = XxHash3.HashToUInt64(bytes);
        return hash.ToString("x16");
    }
}
