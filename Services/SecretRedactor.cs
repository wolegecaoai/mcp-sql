using System.Text.RegularExpressions;

namespace SqlServerMcp.Services;

/// <summary>
/// 敏感信息脱敏：把连接字符串中的密码类字段替换为 <c>****</c>，避免连接失败 / 启动自检的错误提示
/// 把凭据回显给调用方或审计日志。仅处理客户端可见的错误文案，不改实际连接串。
/// </summary>
public static class SecretRedactor
{
    /// <summary>
    /// 匹配 Password / Pwd / Passwd 等键值（大小写不敏感，且前面不是字母数字下划线，避免误伤如 userpassword）。
    /// 值部分取到分号 / 引号 / 行尾为止。
    /// </summary>
    private static readonly Regex PasswordRegex = new(
        @"(?i)(?<![A-Za-z0-9_])(password|pwd|passwd)\s*=\s*[^;""'\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>对输入脱敏；空输入原样返回。</summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        return PasswordRegex.Replace(input, match => $"{match.Groups[1].Value}=****");
    }

    /// <summary>
    /// 对异常做脱敏（M1）：MCP SDK 会把工具内未捕获异常的完整链条按 Error 级落到 stderr，
    /// 若驱动的原始异常消息含连接串则会泄露。这里检测整个异常的字符串形式：
    /// 无敏感信息时**原样返回**（保留类型与堆栈，不影响排障）；命中密码模式时才替换为脱敏后的包装异常。
    /// </summary>
    public static Exception? SanitizeException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        var full = exception.ToString();
        var redacted = Redact(full);
        if (string.Equals(full, redacted, StringComparison.Ordinal))
        {
            return exception;
        }

        return new InvalidOperationException(
            $"[已脱敏的异常，原始类型 {exception.GetType().FullName}]{Environment.NewLine}{redacted}");
    }
}
