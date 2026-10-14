using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// SecretRedactor 脱敏测试：密码类键值脱敏、不误伤用户名/无等号上下文、保留键名前缀（L1）。
/// </summary>
public class SecretRedactorTests
{
    [Theory]
    [InlineData("Server=x;Password=SuperSecret123;Database=db", "Password=****")]
    [InlineData("Pwd=abc;User Id=sa", "Pwd=****")]
    [InlineData("passwd=zzz", "passwd=****")]
    [InlineData("PASSWORD=Secret", "PASSWORD=****")]
    public void Redact_脱敏密码类键值(string input, string expectedContains)
    {
        var output = SecretRedactor.Redact(input);
        Assert.Contains(expectedContains, output);
        Assert.DoesNotContain("SuperSecret123", output);
    }

    [Fact]
    public void Redact_不误伤用户名与无等号上下文()
    {
        // MySQL 常见 "(using password: YES)" 无 "="，不应被改写；用户名 'sa' 保留
        var output = SecretRedactor.Redact("Access denied for user 'sa'@'h' (using password: YES)");
        Assert.Contains("(using password: YES)", output);
        Assert.Contains("'sa'", output);
    }

    [Fact]
    public void Redact_保留键名前缀而非退化为分号()
    {
        // L1 修复：修复前 Groups[1] 恒空会产出 ";=****"；修复后应保留 "Password=****"
        var output = SecretRedactor.Redact("Server=x;Password=abc;Database=db");
        Assert.Contains("Password=****", output);
        Assert.DoesNotContain(";=****", output);
    }

    [Fact]
    public void Redact_空输入安全()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
        Assert.Equal("abc", SecretRedactor.Redact("abc"));
    }

    [Fact]
    public void SanitizeException_无敏感信息时原样返回保留堆栈()
    {
        var original = new InvalidOperationException("boom");
        var result = SecretRedactor.SanitizeException(original);
        Assert.Same(original, result);
    }

    [Fact]
    public void SanitizeException_命中密码模式时脱敏且不泄露明文()
    {
        var original = new InvalidOperationException("连接失败：Server=x;Password=SuperSecret123;Database=db");
        var result = SecretRedactor.SanitizeException(original);

        Assert.NotSame(original, result);
        Assert.NotNull(result);
        Assert.DoesNotContain("SuperSecret123", result!.ToString());
        Assert.Contains("Password=****", result.ToString());
    }

    [Fact]
    public void SanitizeException_null安全()
    {
        Assert.Null(SecretRedactor.SanitizeException(null));
    }
}
