using System.Text.Json;
using Microsoft.Extensions.AI;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 工具 schema 回归测试（B1）：MCP SDK 的 schema 生成会静默丢弃 <see cref="System.Collections.Generic.IEnumerable{T}"/>
/// 类型参数，因此参数化工具的参数必须用具体类型 <see cref="System.Collections.Generic.List{T}"/>。
/// 本测试直接对等价的委托生成 AIFunction，断言 schema 含 parameters，防止将来改回 IEnumerable 或 SDK 变更再次踩坑。
/// </summary>
public class ToolSchemaTests
{
    [Fact]
    public void 参数化参数用List类型_应出现在schema()
    {
        AIFunction function = AIFunctionFactory.Create(
            (string database, string sql, List<SqlParameterInput>? parameters) => "ok",
            "execute_query");

        var schema = function.JsonSchema;
        Assert.NotEqual(JsonValueKind.Undefined, schema.ValueKind);
        Assert.True(
            schema.GetProperty("properties").TryGetProperty("parameters", out _),
            "schema 应包含 parameters 属性（用 List<SqlParameterInput> 才会被生成；IEnumerable<T> 会被静默丢弃）");
    }
}
