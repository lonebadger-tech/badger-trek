using Badger.Trek.Cli;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Badger.Trek.Conversion.IntegrationTests;

/// <summary>
/// Conversions to and from Oracle: each other engine's sample database becomes an Oracle schema (names upper-cased), and
/// the Oracle sample becomes each of the others. The converted project deploys, the rows copy, and trek verify
/// certifies the target.
/// </summary>
[Collection("servers")]
public sealed class OracleConversionTests(Servers servers) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trek-convert-oracle-it", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        MySqlConnection.ClearAllPools();
        OracleConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalOptions G(string dir, string? connectionString = null) => new(dir, null, connectionString, [], false, new ConsoleOutput(false, false, true)) { AgentsFile = false };

    private static string Sample(string name) => TestFiles.Sample(name);

    private static string Quote(string s) => TestFiles.YamlQuote(s);

    private string SqliteDatabase(string? script = null)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N")[..8] + ".db");
        if (script is not null)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();
        }

        return path;
    }

    private static void Target(string project, string environmentYaml)
    {
        var yaml = Path.Combine(project, "trek.yaml");
        var text = File.ReadAllText(yaml);
        var cut = text.IndexOf("\nenvironments:", StringComparison.Ordinal);
        File.WriteAllText(yaml, text[..cut] + $"\nenvironments:\n  local:\n{environmentYaml}");
    }

    private async Task<string> ConvertAsync(string from, string to, string source, string environmentYaml)
    {
        var project = Path.Combine(root, "converted");
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(0, await Commands.ConvertSchema(G(empty, source), project, to, from, null, false, [], false, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(project, "CONVERSION.md")));
        Target(project, environmentYaml);
        Assert.Equal(0, Commands.PlanCheck(G(project)));
        Assert.Equal(0, await Commands.Deploy(G(project), null, false, null, null, true, CancellationToken.None));
        Assert.Equal(0, await Commands.ConvertData(G(empty, source), project, null, from, [], false, CancellationToken.None));
        Assert.Equal(0, await Commands.Verify(G(project), CancellationToken.None));
        return project;
    }

    private static async Task<OracleConnection> OpenOracleAsync(string connectionString)
    {
        var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> Scalar(OracleConnection db, string sql)
    {
        await using var command = new OracleCommand(sql, db) { InitialLOBFetchSize = -1 };
        return await command.ExecuteScalarAsync();
    }

    private static async Task Execute(OracleConnection db, string sql)
    {
        await using var command = new OracleCommand(sql, db);
        await command.ExecuteNonQueryAsync();
    }

    private static long Long(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

    [SkippableFact]
    public async Task PostgreSql_to_Oracle()
    {
        Skip.If(servers.OracleUnavailableReason is not null, servers.OracleUnavailableReason);
        var source = await servers.CreatePostgresAsync(Sample("postgresql.sql"));
        var target = await servers.CreateOracleAsync();
        await ConvertAsync("postgresql", "oracle", source, $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenOracleAsync(target);
        Assert.Equal("Chloé \"Tab\there\"", await Scalar(db, "SELECT name FROM app_customers WHERE id = 3"));
        Assert.Equal("line1\nline2", await Scalar(db, "SELECT note FROM app_orders WHERE id = 3"));
        Assert.Equal(35.96m, await Scalar(db, "SELECT total_with_tax FROM app_orders WHERE id = 1"));
        Assert.Equal(2L, Long(await Scalar(db, "SELECT JSON_VALUE(tags, '$.size()') FROM app_customers WHERE id = 1")));

        // The identity continues after the copied rows; the enum's check and the domain's check carried over.
        await Execute(db, "INSERT INTO app_orders (customer_id, total) VALUES (2, 7.50)");
        Assert.Equal(4L, Long(await Scalar(db, "SELECT MAX(id) FROM app_orders")));
        await Assert.ThrowsAsync<OracleException>(() => Execute(db, "INSERT INTO app_orders (customer_id, total, status) VALUES (1, 1, 'bogus')"));
        await Assert.ThrowsAsync<OracleException>(() => Execute(db, "INSERT INTO app_orders (customer_id, total) VALUES (1, -1)"));
        Assert.Equal(2L, Long(await Scalar(db, "SELECT orders FROM app_customer_totals WHERE id = 1")));
    }

    [SkippableFact]
    public async Task SqlServer_to_Oracle()
    {
        Skip.If(servers.OracleUnavailableReason is not null, servers.OracleUnavailableReason);
        var source = await servers.CreateSqlServerAsync(Sample("sqlserver.sql"));
        var target = await servers.CreateOracleAsync();
        await ConvertAsync("sqlserver", "oracle", source, $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenOracleAsync(target);
        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar(db, "SELECT notes FROM sales_customer WHERE customerid = 1"));
        Assert.Equal("unicode ✓", await Scalar(db, "SELECT notes FROM sales_customer WHERE customerid = 3"));
        Assert.Equal(1L, Long(await Scalar(db, "SELECT COUNT(*) FROM sales_customertotals WHERE customerid = 1")));
    }

    [SkippableFact]
    public async Task Sqlite_to_Oracle()
    {
        Skip.If(servers.OracleUnavailableReason is not null, servers.OracleUnavailableReason);
        var source = SqliteDatabase(Sample("sqlite.sql"));
        var target = await servers.CreateOracleAsync();
        await ConvertAsync("sqlite", "oracle", $"Data Source={source}", $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenOracleAsync(target);
        Assert.Equal(1L, Long(await Scalar(db, "SELECT COUNT(*) FROM authors WHERE name = 'Ada Lovelace'")));
        await Execute(db, "INSERT INTO posts (author_id, title, words) VALUES (2, 'New', 5)");
        Assert.Equal(4L, Long(await Scalar(db, "SELECT MAX(id) FROM posts")));
        await Assert.ThrowsAsync<OracleException>(() => Execute(db, "INSERT INTO posts (author_id, title, words) VALUES (2, 'Bad', -1)"));
    }

    [SkippableFact]
    public async Task MySql_to_Oracle()
    {
        Skip.If(servers.OracleUnavailableReason is not null, servers.OracleUnavailableReason);
        var source = await servers.CreateMySqlAsync(Sample("mysql.sql"));
        var target = await servers.CreateOracleAsync();
        await ConvertAsync("mysql", "oracle", source, $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenOracleAsync(target);
        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar(db, "SELECT notes FROM customers WHERE id = 1"));
        Assert.Equal(1L, Long(await Scalar(db, "SELECT active FROM customers WHERE id = 1")));
        Assert.Equal(35.96m, await Scalar(db, "SELECT total_with_tax FROM orders WHERE id = 1"));
        await Assert.ThrowsAsync<OracleException>(() => Execute(db, "INSERT INTO customers (name, tier) VALUES ('x', 'gold')"));
        Assert.Equal(2L, Long(await Scalar(db, "SELECT orders FROM customer_totals WHERE id = 1")));
    }

    [SkippableFact]
    public async Task Oracle_to_PostgreSql()
    {
        Skip.If(servers.OracleUnavailableReason is not null, servers.OracleUnavailableReason);
        var source = await servers.CreateOracleAsync(Sample("oracle.sql"));
        var target = await servers.CreatePostgresAsync();
        await ConvertAsync("oracle", "postgresql", source, $"    connectionString: {Quote(target)}\n");

        await using var pg = new NpgsqlConnection(target);
        await pg.OpenAsync();
        async Task<object?> Scalar(string sql) => await new NpgsqlCommand(sql, pg).ExecuteScalarAsync();

        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT notes FROM customers WHERE id = 1"));
        Assert.Equal("Chloé \"Tab\"", await Scalar("SELECT name FROM customers WHERE id = 3"));
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x10 }, await Scalar("SELECT receipt FROM orders WHERE id = 1"));
        Assert.Equal(35.96m, await Scalar("SELECT total_with_tax FROM orders WHERE id = 1"));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 9, 4, 5, TimeSpan.Zero).AddTicks(1234560), ((DateTime)(await Scalar("SELECT shipped_at FROM orders WHERE id = 2"))!).ToUniversalTime() is var at ? new DateTimeOffset(at, TimeSpan.Zero) : default);

        // The identity and the sequence continue after the copied rows; the checks carried over.
        Assert.Equal(4L, Convert.ToInt64(await Scalar("INSERT INTO orders (customer_id, total) VALUES (2, 1) RETURNING id")));
        Assert.True(Convert.ToInt64(await Scalar("SELECT ticket FROM orders WHERE id = 4")) > 1002);
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("INSERT INTO customers (name, tier) VALUES ('x', 'gold')", pg).ExecuteNonQueryAsync());
        Assert.Equal("yes", await Scalar("SELECT paying FROM customer_totals WHERE id = 1"));
    }

    [SkippableFact]
    public async Task Oracle_to_SqlServer()
    {
        Skip.If(servers.OracleUnavailableReason is not null, servers.OracleUnavailableReason);
        var source = await servers.CreateOracleAsync(Sample("oracle.sql"));
        var target = await servers.CreateSqlServerAsync();
        await ConvertAsync("oracle", "sqlserver", source, $"    connectionString: {Quote(target)}\n");

        await using var ss = new SqlConnection(target);
        await ss.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqlCommand(sql, ss).ExecuteScalarAsync();

        Assert.Equal("unicode ✓", await Scalar("SELECT NOTES FROM dbo.CUSTOMERS WHERE ID = 3"));
        Assert.Equal(35.96m, await Scalar("SELECT TOTAL_WITH_TAX FROM dbo.ORDERS WHERE ID = 1"));
        Assert.Equal(4m, await Scalar("INSERT INTO dbo.CUSTOMERS (NAME) VALUES (N'New'); SELECT SCOPE_IDENTITY();"));
        await Assert.ThrowsAsync<SqlException>(() => new SqlCommand("INSERT INTO dbo.CUSTOMERS (NAME, TIER) VALUES (N'x', N'gold')", ss).ExecuteNonQueryAsync());
        Assert.Equal(2, Convert.ToInt32(await Scalar("SELECT ORDERS FROM dbo.CUSTOMER_TOTALS WHERE ID = 1")));
    }

    [SkippableFact]
    public async Task Oracle_to_Sqlite()
    {
        Skip.If(servers.OracleUnavailableReason is not null, servers.OracleUnavailableReason);
        var source = await servers.CreateOracleAsync(Sample("oracle.sql"));
        var target = SqliteDatabase();
        await ConvertAsync("oracle", "sqlite", source, $"    database: {Quote(target)}\n");

        await using var lite = new SqliteConnection($"Data Source={target};Pooling=False;Foreign Keys=True");
        await lite.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqliteCommand(sql, lite).ExecuteScalarAsync();

        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT NOTES FROM CUSTOMERS WHERE ID = 1"));
        Assert.Equal(4L, await Scalar("INSERT INTO CUSTOMERS (NAME) VALUES ('New') RETURNING ID"));
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteCommand("INSERT INTO CUSTOMERS (NAME, TIER) VALUES ('x', 'gold')", lite).ExecuteNonQueryAsync());
        Assert.Equal(2L, await Scalar("SELECT ORDERS FROM CUSTOMER_TOTALS WHERE ID = 1"));
    }

    [SkippableFact]
    public async Task Oracle_to_MySql()
    {
        Skip.If(servers.OracleUnavailableReason is not null, servers.OracleUnavailableReason);
        var source = await servers.CreateOracleAsync(Sample("oracle.sql"));
        var target = await servers.CreateMySqlAsync();
        await ConvertAsync("oracle", "mysql", source, $"    connectionString: {Quote(target)}\n");

        await using var my = new MySqlConnection(target);
        await my.OpenAsync();
        async Task<object?> Scalar(string sql) => await new MySqlCommand(sql, my).ExecuteScalarAsync();

        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT NOTES FROM CUSTOMERS WHERE ID = 1"));
        Assert.Equal(35.96m, await Scalar("SELECT TOTAL_WITH_TAX FROM ORDERS WHERE ID = 1"));
        await Assert.ThrowsAsync<MySqlException>(() => new MySqlCommand("INSERT INTO CUSTOMERS (NAME, TIER) VALUES ('x', 'gold')", my).ExecuteNonQueryAsync());
        Assert.Equal(2L, Convert.ToInt64(await Scalar("SELECT ORDERS FROM CUSTOMER_TOTALS WHERE ID = 1")));
    }
}
