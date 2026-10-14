using ModelContextProtocol;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>TableNameParser 引号标识符感知解析测试（M10 修复的回归防线）。</summary>
public class TableNameParserTests
{
    [Theory]
    [InlineData("Users", null, "Users")]
    [InlineData("dbo.Users", "dbo", "Users")]
    [InlineData(" dbo . Users ", "dbo", "Users")]
    [InlineData("[my.table]", null, "my.table")]              // 方括号包裹，点属于标识符
    [InlineData("\"my.table\"", null, "my.table")]            // 双引号包裹
    [InlineData("`my.table`", null, "my.table")]              // 反引号包裹
    [InlineData("dbo.[my.table]", "dbo", "my.table")]         // 混合
    [InlineData("\"dbo\".\"my.table\"", "dbo", "my.table")]
    [InlineData("`mydb`.`my.table`", "mydb", "my.table")]
    [InlineData("[a]]b]", null, "a]b")]                       // ]] 翻倍转义为字面量 ]
    public void Parse_合法形式(string table, string? expectedSchema, string expectedTable)
    {
        var (schema, name) = TableNameParser.Parse(table);
        Assert.Equal(expectedSchema, schema);
        Assert.Equal(expectedTable, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a.b.c")]          // 三段
    [InlineData("dbo.")]           // 空片段
    [InlineData(".Users")]         // 空 schema 片段
    [InlineData("[unclosed")]      // 方括号未闭合
    [InlineData("\"unclosed")]     // 双引号未闭合
    [InlineData("`unclosed")]      // 反引号未闭合
    [InlineData("\"\"")]           // 空引号标识符
    [InlineData("[a]b")]           // 引号闭合后跟随非分隔符（M2：防静默拼接）
    [InlineData("\"a\"b")]
    [InlineData("`a`b")]
    [InlineData("[a].[b]c")]
    public void Parse_非法形式拒绝(string table)
    {
        Assert.Throws<McpException>(() => TableNameParser.Parse(table));
    }

    [Theory]
    [InlineData("[a] ")]
    [InlineData("[a]\t")]
    public void Parse_引号闭合后允许空白(string table)
    {
        var (schema, name) = TableNameParser.Parse(table);
        Assert.Null(schema);
        Assert.Equal("a", name);
    }
}
