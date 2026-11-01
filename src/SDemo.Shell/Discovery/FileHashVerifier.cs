using System.Security.Cryptography;
using SDemo.Shell.Contracts;

namespace SDemo.Shell.Discovery;

/// <summary>默认哈希校验：SHA-256 of file，返回小写十六进制。</summary>
public sealed class FileHashVerifier : IHashVerifier
{
    public string? ComputeSha256(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
