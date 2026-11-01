using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;

namespace SDemo.Shell.Runtime;

/// <summary>
/// app token 的生成、登记与校验（host-endpoints §2 / §6）。
/// token 分两类来源：宿主拉起时下发、用户自行启动时应用生成并写 runtime.json。
/// </summary>
public sealed class AppTokenStore
{
    private readonly IRuntimeFileStore _runtimeFiles;
    private readonly IShellLog _log;
    private readonly Func<int, bool> _processProbe;
    private readonly ConcurrentDictionary<string, string> _launched = new(StringComparer.Ordinal);

    public AppTokenStore(IRuntimeFileStore runtimeFiles, IShellLog? log = null, Func<int, bool>? processProbe = null)
    {
        _runtimeFiles = runtimeFiles;
        _log = log ?? NullShellLog.Instance;
        _processProbe = processProbe ?? IsProcessAlive;
    }

    /// <summary>32 字节随机 token，base64url（无填充）。</summary>
    public static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public void SetLaunchedToken(string appId, string token) => _launched[appId] = token;

    public void Clear(string appId) => _launched.TryRemove(appId, out _);

    /// <summary>校验 app 面 token：优先宿主下发的，其次 runtime.json（且 pid 仍存活）。</summary>
    public bool TryValidate(string appId, string token, out string? failure)
    {
        failure = null;
        if (string.IsNullOrEmpty(token))
        {
            failure = "缺少 token";
            return false;
        }

        if (_launched.TryGetValue(appId, out var issued) && FixedTimeEquals(issued, token))
        {
            return true;
        }

        var runtime = _runtimeFiles.Read(appId);
        if (runtime is null)
        {
            failure = "runtime.json 不存在";
            return false;
        }

        if (!_processProbe(runtime.Pid))
        {
            failure = "runtime.json 的 pid 已退出";
            return false;
        }

        if (!FixedTimeEquals(runtime.Token, token))
        {
            failure = "token 与 runtime.json 不符";
            return false;
        }

        // 用户自行启动的应用：首次回调时登记，供后续调用。
        _launched[appId] = token;
        return true;
    }

    /// <summary>由 token 反查 appId（notify 这类没有 appId 路径的端点用）。</summary>
    public bool TryResolveAppId(string token, out string appId)
    {
        appId = string.Empty;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        foreach (var pair in _launched)
        {
            if (FixedTimeEquals(pair.Value, token))
            {
                appId = pair.Key;
                return true;
            }
        }

        foreach (var id in _runtimeFiles.EnumerateAppIds())
        {
            var runtime = _runtimeFiles.Read(id);
            if (runtime is null || !_processProbe(runtime.Pid))
            {
                continue;
            }

            if (FixedTimeEquals(runtime.Token, token))
            {
                appId = id;
                _launched[id] = token;
                return true;
            }
        }

        _log.Warn("收到无法识别的 app token（未记录 token 内容）。");
        return false;
    }

    /// <summary>宿主 → 应用调用时使用的 token。</summary>
    public string? ResolveToken(string appId)
    {
        if (_launched.TryGetValue(appId, out var issued))
        {
            return issued;
        }

        return _runtimeFiles.Read(appId)?.Token;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
