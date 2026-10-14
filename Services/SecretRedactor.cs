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
        @"(?i)(?<![A-Za-z0-9_])(?:password|pwd|passwd)\s*=\s*[^;""'\r\n]+",
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
}
