using Microsoft.Data.Sqlite;
using MuAgents.Abstractions;

namespace MuAgents.Persistence;

/// <summary>SQLite 连接配置，并负责把文件型数据源约束到项目的 .muagent 目录。</summary>
public sealed class PersistenceOptions
{
    /// <summary>SQLite 连接字符串；默认数据库位于 data/muagents.db。</summary>
    public string ConnectionString { get; set; } = "Data Source=data/muagents.db";

    /// <summary>解析并验证数据库路径；内存数据库保留原连接语义。</summary>
    public string ResolveConnectionString()
    {
        var builder = new SqliteConnectionStringBuilder(ConnectionString);
        if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:")
            return builder.ToString();
        builder.DataSource = RuntimePaths.ResolveWritePath(
            builder.DataSource,
            "MuAgents:Persistence:ConnectionString Data Source");
        return builder.ToString();
    }
}
