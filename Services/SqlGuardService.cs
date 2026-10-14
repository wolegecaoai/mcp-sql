using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace SqlServerMcp.Services;

/// <summary>SQL 语句类型分类。</summary>
public enum SqlStatementType
{
    Select,
    Insert,
    Update,
    Delete,
    Ddl,
    Other
}

/// <summary>
/// SQL 安全防护服务：语句分类、单语句校验、危险关键词黑名单、无 WHERE 更新/删除识别。
/// 所有检查均基于「去除注释与字符串字面量内容后」的 SQL 文本，避免误伤字符串常量。
/// </summary>
public class SqlGuardService
{
    private static readonly Dictionary<string, SqlStatementType> KeywordTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = SqlStatementType.Select,
        ["WITH"] = SqlStatementType.Select,
        ["INSERT"] = SqlStatementType.Insert,
        ["UPDATE"] = SqlStatementType.Update,
        ["DELETE"] = SqlStatementType.Delete,
        ["MERGE"] = SqlStatementType.Insert, // MERGE 兼具增删改，按写操作处理
        ["CREATE"] = SqlStatementType.Ddl,
        ["ALTER"] = SqlStatementType.Ddl,
        ["DROP"] = SqlStatementType.Ddl,
        ["TRUNCATE"] = SqlStatementType.Ddl,
        ["GRANT"] = SqlStatementType.Ddl,
        ["REVOKE"] = SqlStatementType.Ddl,
        ["DENY"] = SqlStatementType.Ddl
    };

    /// <summary>
    /// 将 SQL 语句分类为查询/写入/DDL。WITH 开头的语句会穿透 CTE 定义识别主语句类型，
    /// 防止 "WITH cte AS (...) INSERT/DELETE/UPDATE" 被误判为查询从而绕过写操作确认流程。
    /// </summary>
    public SqlStatementType ClassifyStatement(string sql)
    {
        var cleaned = StripCommentsAndStrings(sql).TrimStart();
        var match = Regex.Match(cleaned, @"^[A-Za-z]+");
        if (!match.Success)
        {
            return SqlStatementType.Other;
        }

        if (string.Equals(match.Value, "WITH", StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyWithStatement(cleaned);
        }

        return KeywordTypes.TryGetValue(match.Value, out var type) ? type : SqlStatementType.Other;
    }

    /// <summary>
    /// 识别 WITH(CTE) 语句的主语句类型：按括号深度跳过 CTE 定义组，
    /// 取其后出现的第一个语句关键字。找不到则保守返回 Other。
    /// </summary>
    private static SqlStatementType ClassifyWithStatement(string cleaned)
    {
        var depth = 0;
        var seenGroup = false;
        var index = 0;
        while (index < cleaned.Length)
        {
            var current = cleaned[index];

            if (current == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (current == ')')
            {
                if (depth > 0)
                {
                    depth--;
                    if (depth == 0)
                    {
                        seenGroup = true;
                    }
                }

                index++;
                continue;
            }

            if (depth == 0 && char.IsLetter(current))
            {
                var start = index;
                while (index < cleaned.Length && char.IsLetter(cleaned[index]))
                {
                    index++;
                }

                var word = cleaned[start..index];
                if (seenGroup && KeywordTypes.TryGetValue(word, out var mainType))
                {
                    // CTE 定义之后的第一个语句关键字即主语句类型
                    return mainType;
                }

                continue;
            }

            index++;
        }

        return SqlStatementType.Other;
    }

    /// <summary>按顶层分号切分语句（字符串字面量内的分号不参与切分）。</summary>
    public List<string> SplitStatements(string sql)
    {
        var cleaned = StripCommentsAndStrings(sql);
        return cleaned
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => !string.IsNullOrWhiteSpace(statement))
            .ToList();
    }

    /// <summary>校验 SQL 必须且只能是一条语句。</summary>
    public void AssertSingleStatement(string sql, string toolName)
    {
        var statements = SplitStatements(sql);
        if (statements.Count == 0)
        {
            throw new McpException("SQL 语句为空，请提供要执行的语句。");
        }

        if (statements.Count > 1)
        {
            throw new McpException(
                $"{toolName} 每次只允许执行一条语句，当前检测到 {statements.Count} 条。请拆分后逐条调用，以降低批量误操作风险。");
        }
    }

    /// <summary>
    /// 校验 SQL 必须是单条只读查询（SELECT 或 WITH CTE 开头的 SELECT），且不包含危险关键词。
    /// </summary>
    /// <param name="sql">待校验语句。</param>
    /// <param name="dangerousKeywords">危险关键词黑名单（只读侧同样生效，防数据外发/文件读取类函数）。</param>
    public void AssertReadOnly(string sql, IEnumerable<string> dangerousKeywords)
    {
        AssertSingleStatement(sql, "execute_query");
        var type = ClassifyStatement(sql);
        if (type != SqlStatementType.Select)
        {
            throw new McpException(
                $"execute_query 仅支持 SELECT/WITH 查询语句（检测到 {DescribeType(type)}）。" +
                "数据修改请使用 execute_non_query，结构变更请使用 execute_ddl。");
        }

        // SELECT ... INTO 会在查询中创建表并复制数据，属于写操作，同样拒绝
        if (Regex.IsMatch(StripCommentsAndStrings(sql), @"\bINTO\b", RegexOptions.IgnoreCase))
        {
            throw new McpException(
                "execute_query 仅支持只读查询：检测到 SELECT ... INTO（会在查询过程中创建表并写入数据）。" +
                "数据修改请使用 execute_non_query，结构变更请使用 execute_ddl。");
        }

        // 只读路径同样过黑名单：SELECT 也可通过链接服务器/文件函数外发数据（OPENROWSET、dblink、pg_read_file 等）
        AssertNoDangerousKeywords(sql, dangerousKeywords);
    }

    /// <summary>校验 SQL 不包含危险关键词（命中黑名单即拒绝）。</summary>
    public void AssertNoDangerousKeywords(string sql, IEnumerable<string> dangerousKeywords)
    {
        var cleaned = StripCommentsAndStrings(sql);
        foreach (var keyword in dangerousKeywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            var trimmed = keyword.Trim();
            var pattern = @"\b" + string.Join(@"\s+", trimmed
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(Regex.Escape)) + @"\b";

            if (Regex.IsMatch(cleaned, pattern, RegexOptions.IgnoreCase))
            {
                throw new McpException(
                    $"语句包含被禁止的危险关键词「{trimmed}」，已拒绝执行。" +
                    "如确需放行，请调整 appsettings.json 中 SqlServer:DangerousKeywords 配置。");
            }
        }
    }

    /// <summary>判断是否为 TRUNCATE TABLE 语句（无法按行过滤、直接清空整表）。</summary>
    public bool IsTruncateStatement(string sql) =>
        Regex.IsMatch(StripCommentsAndStrings(sql).TrimStart(), @"^TRUNCATE\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// 判断 UPDATE/DELETE 语句是否缺少**顶层** WHERE 子句（将影响全表）。
    /// 按括号深度判定：子查询内的 WHERE 不算，避免 "SET c=(SELECT ... WHERE ...)" 假阴性。
    /// </summary>
    public bool IsUpdateOrDeleteWithoutWhere(string sql)
    {
        var cleaned = StripCommentsAndStrings(sql);
        return !HasTopLevelKeyword(cleaned, "WHERE");
    }

    /// <summary>在清洗后的 SQL 中查找括号深度 0 的关键字。</summary>
    private static bool HasTopLevelKeyword(string cleaned, string keyword)
    {
        var depth = 0;
        var index = 0;
        while (index < cleaned.Length)
        {
            var current = cleaned[index];
            if (current == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (current == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }

                index++;
                continue;
            }

            if (depth == 0 && char.IsLetter(current))
            {
                var start = index;
                while (index < cleaned.Length && char.IsLetter(cleaned[index]))
                {
                    index++;
                }

                if (string.Equals(cleaned[start..index], keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            index++;
        }

        return false;
    }

    /// <summary>
    /// 去除 SQL 中的注释内容与字符串字面量/括号标识符内容（统一替换为空格），
    /// 保留分隔符本身，使后续分号切分与关键词匹配不受字面量干扰。
    /// 方言覆盖：-- 与 # 行注释、/**/ 块注释、'' 字符串、[] 与 "" 与 `` 标识符。
    /// 安全策略（fail-closed）：MySQL 可执行注释 /*! ... */ 直接拒绝；
    /// 未终止的注释/字面量/标识符一律拒绝，避免"守卫看到的 SQL"与"实际执行的 SQL"不一致。
    /// </summary>
    public static string StripCommentsAndStrings(string sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(sql.Length);
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';

            // 行注释 --（SQL Server / PG / SQLite）
            if (current == '-' && next == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            // 行注释 #（MySQL）
            if (current == '#')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            // 块注释 /* */
            if (current == '/' && next == '*')
            {
                // MySQL 可执行注释 /*! ... */：内容会被服务器解析执行，必须拒绝而非清除
                if (index + 2 < sql.Length && sql[index + 2] == '!')
                {
                    throw new McpException(
                        "语句包含 MySQL 可执行注释 /*! ... */，其内容会被数据库服务器解析执行，" +
                        "会导致安全校验失效，已拒绝执行。");
                }

                index += 2;
                while (index < sql.Length && !(sql[index] == '*' && index + 1 < sql.Length && sql[index + 1] == '/'))
                {
                    index++;
                }

                if (index >= sql.Length)
                {
                    throw new McpException("语句包含未终止的块注释（/* 没有对应的 */），无法安全解析，已拒绝执行。");
                }

                index += 2;
                builder.Append(' ');
                continue;
            }

            // 字符串字面量 '...'（'' 为转义）
            if (current == '\'')
            {
                builder.Append('\'');
                index++;
                var terminated = false;
                while (index < sql.Length)
                {
                    if (sql[index] == '\'')
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == '\'')
                        {
                            builder.Append("''");
                            index += 2;
                            continue;
                        }

                        builder.Append('\'');
                        index++;
                        terminated = true;
                        break;
                    }

                    builder.Append(' ');
                    index++;
                }

                if (!terminated)
                {
                    throw new McpException("语句包含未终止的字符串字面量（' 没有成对出现），无法安全解析，已拒绝执行。");
                }

                continue;
            }

            // 方括号标识符 [...]（SQL Server）
            if (current == '[')
            {
                builder.Append('[');
                index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (sql[index] == ']')
                    {
                        builder.Append(']');
                        index++;
                        closed = true;
                        break;
                    }

                    builder.Append(' ');
                    index++;
                }

                if (!closed)
                {
                    throw new McpException("语句包含未终止的方括号标识符（[ 没有对应的 ]），无法安全解析，已拒绝执行。");
                }

                continue;
            }

            // 双引号标识符 "..."（PG / SQLite）
            if (current == '"')
            {
                builder.Append('"');
                index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (sql[index] == '"')
                    {
                        builder.Append('"');
                        index++;
                        closed = true;
                        break;
                    }

                    builder.Append(' ');
                    index++;
                }

                if (!closed)
                {
                    throw new McpException("语句包含未终止的引号标识符（\" 没有成对出现），无法安全解析，已拒绝执行。");
                }

                continue;
            }

            // 反引号标识符 `...`（MySQL）
            if (current == '`')
            {
                builder.Append('`');
                index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (sql[index] == '`')
                    {
                        builder.Append('`');
                        index++;
                        closed = true;
                        break;
                    }

                    builder.Append(' ');
                    index++;
                }

                if (!closed)
                {
                    throw new McpException("语句包含未终止的反引号标识符（` 没有成对出现），无法安全解析，已拒绝执行。");
                }

                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    private static string DescribeType(SqlStatementType type) => type switch
    {
        SqlStatementType.Select => "SELECT 查询",
        SqlStatementType.Insert => "INSERT 写入",
        SqlStatementType.Update => "UPDATE 写入",
        SqlStatementType.Delete => "DELETE 写入",
        SqlStatementType.Ddl => "DDL 结构变更",
        _ => "无法识别的语句"
    };
}
