using System.Text;

namespace SDemo.Shell.Storage;

/// <summary>原子写：同目录 <c>.tmp</c> → <c>File.Replace</c>，保留一份 <c>.bak</c>（shell-storage §6.1）。</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        if (!TryWriteAllText(path, content, out var error))
        {
            throw new IOException(error ?? $"写入失败：{path}");
        }
    }

    public static bool TryWriteAllText(string path, string content, out string? error)
    {
        error = null;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            error = "路径缺少目录";
            return false;
        }

        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // 失败时保留 tmp，下次启动或人工可检查。
            return false;
        }
    }
}
