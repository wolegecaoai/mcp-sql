using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace SqlServerMcp.Services;

/// <summary>
/// http 传输的鉴权判定（可单测的纯函数封装）。仅保护 /mcp 端点；
/// 未配置 ApiKey 时一律拒绝（fail-closed，与 SqlServerOptions.ApiKey 的文档承诺一致）。
/// </summary>
public static class HttpApiKeyAuth
{
    /// <summary>判断请求路径是否属于受保护的 MCP 端点（/mcp 及其子路径）。</summary>
    public static bool IsMcpPath(PathString path) =>
        path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 依据请求头判定是否已授权：支持 <c>Authorization: Bearer &lt;key&gt;</c> 或 <c>X-Api-Key</c>。
    /// apiKey 为空/空白时恒为 false（fail-closed）；比较采用固定时间以缓解时序侧信道。
    /// </summary>
    public static bool IsAuthorized(HttpRequest request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        var authorization = request.Headers["Authorization"].ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization["Bearer ".Length..].Trim();
            return FixedTimeEquals(token, apiKey!);
        }

        var headerKey = request.Headers["X-Api-Key"].ToString();
        return FixedTimeEquals(headerKey, apiKey!);
    }

    private static bool FixedTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
