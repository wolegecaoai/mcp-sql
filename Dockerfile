# 本地 stdio MCP Server 的容器镜像。
#
# 运行方式（必须 -i 保持 stdin 打开；不能用 -t，也不要以守护模式 -d 运行）：
#   docker build -t sqlserver-mcp .
#   docker run -i --rm \
#     -v /path/to/your/appsettings.json:/app/appsettings.json:ro \
#     -v sqlserver-mcp-logs:/app/logs \
#     sqlserver-mcp
#
# 说明：
#   * 配置挂载到 /app/appsettings.json —— 程序以 CWD（/app）为配置目录，挂载即生效；
#   * 真实 appsettings.json 由 .dockerignore 排除，**不会**被打进镜像；
#   * 连接宿主机的数据库时，Linux 下用 --network host（或 127.0.0.1 需改为 host.docker.internal）；
#   * 运行时镜像用 aspnet:10.0（含 ASP.NET Core 共享框架）：本项目自支持 HTTP 传输起，
#     产物同时依赖 Microsoft.NETCore.App 与 Microsoft.AspNetCore.App，故不能再用精简的 runtime:10.0。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先复制项目文件与包配置，最大化利用层缓存
COPY SqlServerMcp.csproj nuget.config ./
RUN dotnet restore SqlServerMcp.csproj

COPY Program.cs ./
COPY Configuration/ Configuration/
COPY Services/ Services/
COPY Tools/ Tools/
COPY appsettings.example.json ./

RUN dotnet publish SqlServerMcp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

# 审计日志写在 CWD/logs：预建目录并交给非 root 用户，避免运行期权限失败（写日志失败虽不中断主流程，但会丢审计）
RUN mkdir -p /app/logs && chown -R app:app /app
USER app

# stdio 传输：进程通过 stdin/stdout 与客户端通信（日志已全部走 stderr）
ENTRYPOINT ["dotnet", "SqlServerMcp.dll"]
