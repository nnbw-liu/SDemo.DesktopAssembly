namespace SDemo.Shell.Contracts;

/// <summary>宿主错误码全集（host-protocol §11 + host-endpoints §7）。</summary>
public static class ErrorCodes
{
    public const string PolicyDenied = "HOST_POLICY_DENIED";
    public const string VersionMismatch = "HOST_VERSION_MISMATCH";
    public const string TokenInvalid = "HOST_TOKEN_INVALID";
    public const string Unauthorized = "HOST_UNAUTHORIZED";
    public const string ScopeDenied = "HOST_SCOPE_DENIED";
    public const string BadRequest = "HOST_BAD_REQUEST";
    public const string TooLarge = "HOST_TOO_LARGE";
    public const string Busy = "HOST_BUSY";
    public const string InternalError = "HOST_INTERNAL_ERROR";

    public const string AppNotFound = "APP_NOT_FOUND";
    public const string AppDisabled = "APP_DISABLED";
    public const string AppInvalid = "APP_INVALID";
    public const string AppCrashed = "APP_CRASHED";
    public const string AppStartTimeout = "APP_START_TIMEOUT";
    public const string AppHealthFailed = "APP_HEALTH_FAILED";
    public const string WidgetWindowLost = "WIDGET_WINDOW_LOST";

    /// <summary>HTTP 状态码映射。</summary>
    public static int HttpStatus(string code) => code switch
    {
        Unauthorized or TokenInvalid => 401,
        ScopeDenied or PolicyDenied => 403,
        BadRequest => 400,
        TooLarge => 413,
        AppNotFound => 404,
        AppDisabled or AppInvalid or VersionMismatch => 409,
        Busy or AppCrashed => 503,
        AppStartTimeout => 504,
        _ => 500,
    };

    public static string Describe(string code) => code switch
    {
        PolicyDenied => "宿主拒绝了策略请求",
        VersionMismatch => "宿主与应用版本不兼容",
        TokenInvalid => "token 校验失败",
        Unauthorized => "缺少或错误的 token",
        ScopeDenied => "token 用在了错误的面",
        BadRequest => "请求格式不合法",
        TooLarge => "请求体超过 64KB",
        Busy => "宿主繁忙，稍后重试",
        AppNotFound => "应用未安装",
        AppDisabled => "组件已被禁用",
        AppInvalid => "清单非法或可执行文件哈希不符",
        AppCrashed => "应用启动后异常退出，且已用尽重启次数",
        AppStartTimeout => "应用启动超时",
        AppHealthFailed => "应用健康检查失败",
        WidgetWindowLost => "组件窗口句柄失效",
        _ => "宿主内部错误",
    };
}

/// <summary>统一错误信封（仅失败时使用，host-endpoints §7）。</summary>
public sealed class ErrorEnvelope
{
    public ErrorInfo Error { get; set; } = new();

    public static ErrorEnvelope Create(string code, string? message, string? requestId, string? hint = null) =>
        new()
        {
            Error = new ErrorInfo
            {
                Code = code,
                Message = message ?? ErrorCodes.Describe(code),
                Hint = hint,
                RequestId = requestId,
            },
        };
}

public sealed class ErrorInfo
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Hint { get; set; }

    public string? RequestId { get; set; }
}
