using Microsoft.AspNetCore.Http;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// HttpApiKeyAuth 鉴权测试：fail-closed（空 key 拒绝）、Bearer/X-Api-Key、错误密钥拒绝、仅保护 /mcp（B2/L7）。
/// </summary>
public class HttpApiKeyAuthTests
{
    private static HttpRequest NewRequest(string path, params (string Key, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        foreach (var (key, value) in headers)
        {
            context.Request.Headers[key] = value;
        }

        return context.Request;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空ApiKey_一律拒绝(string? apiKey)
    {
        Assert.False(HttpApiKeyAuth.IsAuthorized(NewRequest("/mcp"), apiKey));
        Assert.False(HttpApiKeyAuth.IsAuthorized(NewRequest("/mcp", ("Authorization", "Bearer anything")), apiKey));
    }

    [Fact]
    public void 正确Bearer与XApiKey_通过()
    {
        Assert.True(HttpApiKeyAuth.IsAuthorized(NewRequest("/mcp", ("Authorization", "Bearer secret")), "secret"));
        Assert.True(HttpApiKeyAuth.IsAuthorized(NewRequest("/mcp", ("X-Api-Key", "secret")), "secret"));
    }

    [Fact]
    public void 错误密钥_拒绝()
    {
        Assert.False(HttpApiKeyAuth.IsAuthorized(NewRequest("/mcp", ("X-Api-Key", "wrong")), "secret"));
        Assert.False(HttpApiKeyAuth.IsAuthorized(NewRequest("/mcp", ("Authorization", "Bearer wrong")), "secret"));
    }

    [Fact]
    public void 仅mcp路径受保护()
    {
        Assert.True(HttpApiKeyAuth.IsMcpPath("/mcp"));
        Assert.True(HttpApiKeyAuth.IsMcpPath("/mcp/messages"));
        Assert.False(HttpApiKeyAuth.IsMcpPath("/"));
        Assert.False(HttpApiKeyAuth.IsMcpPath("/healthz"));
    }
}
