using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Import;

namespace Badger.Trek.Cli.Tests;

public sealed class ImportCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trek-import-cli", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalOptions G(string dir, params string[] vars) => new(dir, null, null, vars, false, new ConsoleOutput(false, false, true));

    [Fact]
    public async Task Imported_flyway_project_has_a_trek_yaml_that_resolves()
    {
        var source = Path.Combine(root, "flyway");
        Directory.CreateDirectory(Path.Combine(source, "sql"));
        File.WriteAllText(Path.Combine(source, "flyway.toml"), """
            [flyway]
            environment = "prod"
            [flyway.placeholders]
            appSchema = "sales"
            quote = "say \"hi\" {x}"
            [environments.test]
            url = "jdbc:sqlserver://test-sql:1433;databaseName=AppTest;encrypt=true;trustServerCertificate=true;applicationIntent=ReadWrite;sendTimeAsDatetime=false"
            user = "deploy"
            password = "${env.TEST_DB_PASSWORD}"
            [environments.prod]
            url = "jdbc:sqlserver://prod.database.windows.net;databaseName=App;authentication=ActiveDirectoryDefault;encrypt=strict"
            """);
        File.WriteAllText(Path.Combine(source, "sql", "V1__Init.sql"), "CREATE SCHEMA ${appSchema}; SELECT '${quote}';");
        var target = Path.Combine(root, "trek");

        Assert.Equal(0, Commands.ImportFlyway(G(target), source, null, [], dryRun: false));

        var config = TrekConfig.Load(target);
        Assert.Equal("prod", config.DefaultEnvironment);
        Assert.Equal("sales", config.Variables["appSchema"]);
        Assert.Equal("say \"hi\" {{x}}", config.Variables["quote"]);

        Environment.SetEnvironmentVariable("TEST_DB_PASSWORD", "from-the-environment");
        var test = config.ResolveEnvironment("test", null);
        Assert.Equal("from-the-environment", test.Password);
        Assert.Equal(("test-sql", 1433, "AppTest", AuthenticationKind.Password, "deploy"), (test.Host, test.Port!.Value, test.Database, test.Authentication!.Value, test.User));
        Assert.Equal(TlsMode.Mandatory, test.Tls.Mode);
        Assert.True(test.Tls.TrustServerCertificate);
        Assert.Equal("ReadWrite", test.ConnectionProperties["Application Intent"]);

        var prod = config.ResolveEnvironment("prod", null);
        Assert.Equal((AuthenticationKind.EntraDefault, TlsMode.Strict), (prod.Authentication!.Value, prod.Tls.Mode));
        Assert.Contains("password: \"${env:TEST_DB_PASSWORD}\"", File.ReadAllText(Path.Combine(target, "trek.yaml")), StringComparison.Ordinal);

        Assert.Equal(0, Commands.PlanCheck(G(target)));
        Assert.Equal(1, await Commands.Run(G(target), g => Commands.ImportFlyway(g, source, null, [], dryRun: false)));
    }

    [Fact]
    public async Task Imported_dbup_project_without_values_fails_plan_check_until_defined()
    {
        var source = Path.Combine(root, "dbup");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Script0001.sql"), "CREATE SCHEMA [$schema$]; SELECT '$(literal)';");
        var target = Path.Combine(root, "trek");

        Assert.Equal(0, Commands.ImportDbUp(G(target), source, false, [], false, dryRun: false));
        var yaml = File.ReadAllText(Path.Combine(target, "trek.yaml"));
        Assert.Contains("# schema: \"...\"", yaml, StringComparison.Ordinal);
        Assert.Contains("sqlcmdSyntax: false", yaml, StringComparison.Ordinal);
        Assert.Equal(1, Commands.PlanCheck(G(target)));

        var withValues = Path.Combine(root, "trek2");
        Assert.Equal(0, await Commands.Run(G(withValues, "schema=app"), g => Commands.ImportDbUp(g, source, false, [], false, dryRun: false)));
        Assert.Equal(0, Commands.PlanCheck(G(withValues)));
        Assert.Equal("app", TrekConfig.Load(withValues).Variables["schema"]);
    }

    [Fact]
    public void Dry_run_writes_nothing()
    {
        var source = Path.Combine(root, "dbup");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.sql"), "SELECT 1;");
        var target = Path.Combine(root, "trek");

        Assert.Equal(0, Commands.ImportDbUp(G(target), source, false, [], false, dryRun: true));
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData("jdbc:sqlserver://sql01\\INST;databaseName=App;integratedSecurity=true", "sql01\\INST", null, "integrated")]
    [InlineData("jdbc:sqlserver://;serverName=h;portNumber=1500;databaseName=App", "h", 1500, "password")]
    [InlineData("jdbc:sqlserver://h:1433;databaseName=App;authentication=ActiveDirectoryServicePrincipal", "h", 1433, "entraServicePrincipal")]
    [InlineData("jdbc:sqlserver://h;databaseName=App;authentication=ActiveDirectoryMSI;msiClientId=abc", "h", null, "entraManagedIdentity")]
    public void Jdbc_urls_map_to_environments(string url, string host, int? port, string authentication)
    {
        var warnings = new List<string>();
        var settings = JdbcSqlServer.Map(new ConnectionToImport("x", url, "u", "plain-secret"), warnings)!;

        Assert.Equal(host, (string?)settings["host"]);
        Assert.Equal(port, (int?)settings["port"]);
        Assert.Equal("App", (string?)settings["database"]);
        Assert.Equal(authentication, (string?)settings["authentication"]);
        Assert.DoesNotContain("plain-secret", settings.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Non_sql_server_urls_are_reported()
    {
        var warnings = new List<string>();
        Assert.Null(JdbcSqlServer.Map(new ConnectionToImport("pg", "jdbc:postgresql://h/db?password=x", null, null), warnings));
        Assert.Contains(warnings, w => w.Contains("not a SQL Server JDBC URL", StringComparison.Ordinal));
    }

    [Fact]
    public void Imported_scripts_that_cannot_run_in_a_transaction_are_marked_notx()
    {
        var source = Path.Combine(root, "grate");
        Directory.CreateDirectory(Path.Combine(source, "up"));
        File.WriteAllText(Path.Combine(source, "up", "0001_db.sql"), "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON;");
        File.WriteAllText(Path.Combine(source, "up", "0002_table.sql"), "CREATE TABLE dbo.T (Id int);");
        var target = Path.Combine(root, "trek");

        Assert.Equal(0, Commands.ImportGrate(G(target), source, [], [], false, false, dryRun: false));

        var plan = Badger.Trek.Core.Plan.PlanFile.Load(Path.Combine(target, "trek.plan"));
        Assert.True(plan.Changes[0].Flags.HasFlag(Badger.Trek.Core.Plan.ChangeFlags.NoTransaction));
        Assert.False(plan.Changes[1].Flags.HasFlag(Badger.Trek.Core.Plan.ChangeFlags.NoTransaction));
        Assert.Equal(0, Commands.PlanCheck(G(target)));
    }

    [Theory]
    [InlineData("jdbc:postgresql://pg.example.com:6432/shop?sslmode=verify-full&currentSchema=app&user=deployer&password=plain", "pg.example.com", 6432, "shop", "password", "strict")]
    [InlineData("jdbc:postgresql://localhost/shop?ssl=true", "localhost", null, "shop", "integrated", "mandatory")]
    [InlineData("jdbc:postgresql://x.postgres.database.azure.com:5432/shop?authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin&sslmode=require", "x.postgres.database.azure.com", 5432, "shop", "entraDefault", "mandatory")]
    public void Postgres_jdbc_urls_map_to_environments(string url, string host, int? port, string database, string authentication, string tls)
    {
        var warnings = new List<string>();
        var settings = JdbcPostgreSql.Map(new ConnectionToImport("x", url, null, null), warnings)!;

        Assert.Equal((host, port, database, authentication, tls), ((string?)settings["host"], (int?)settings["port"], (string?)settings["database"], (string?)settings["authentication"], (string?)settings["tls"]!["mode"]));
        Assert.DoesNotContain("plain", settings.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_jdbc_options_become_npgsql_keywords()
    {
        var warnings = new List<string>();
        var settings = JdbcPostgreSql.Map(new ConnectionToImport("prod", "jdbc:postgresql://h/db?currentSchema=app&ApplicationName=flyway&prepareThreshold=0", "${env.DB_USER}", "${env.DB_PASSWORD}"), warnings)!;
        Assert.Equal("app", (string?)settings["connectionProperties"]!["Search Path"]);
        Assert.Equal("${env:DB_USER}", (string?)settings["user"]);
        Assert.Equal("${env:DB_PASSWORD}", (string?)settings["password"]);
        Assert.Contains(warnings, w => w.Contains("prepareThreshold", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("jdbc:oracle:thin:@db.example.com:1522/MYAPP_PROD", "db.example.com", 1522, "MYAPP_PROD", null)]
    [InlineData("jdbc:oracle:thin:@//localhost/FREEPDB1", "localhost", null, "FREEPDB1", null)]
    [InlineData("jdbc:oracle:thin:@tcps://adb.eu-frankfurt-1.oraclecloud.com:1522/abc_high.adb.oraclecloud.com?wallet_location=/w&ssl_server_dn_match=true", "adb.eu-frankfurt-1.oraclecloud.com", 1522, "abc_high.adb.oraclecloud.com", "strict")]
    [InlineData("jdbc:oracle:thin:scott/plain@legacy:1521:ORCL", "legacy", 1521, "ORCL", null)]
    [InlineData("jdbc:oracle:thin:@[::1]:1521/FREEPDB1", "::1", 1521, "FREEPDB1", null)]
    public void Oracle_jdbc_urls_map_to_environments(string url, string host, int? port, string database, string? tls)
    {
        var warnings = new List<string>();
        var settings = JdbcOracle.Map(new ConnectionToImport("prod", url, null, null), warnings)!;

        Assert.Equal((host, port, database, "password", tls), ((string?)settings["host"], (int?)settings["port"], (string?)settings["database"], (string?)settings["authentication"], (string?)settings["tls"]?["mode"]));
        Assert.DoesNotContain("plain", settings.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Contains("plain", StringComparison.Ordinal));
    }

    [Fact]
    public void Oracle_jdbc_sid_descriptor_and_alias_urls()
    {
        var warnings = new List<string>();
        var sid = JdbcOracle.Map(new ConnectionToImport("default", "jdbc:oracle:thin:scott/tiger@legacy:1521:ORCL", null, null), warnings)!;
        Assert.Equal(("scott", "${env:FLYWAY_PASSWORD}"), ((string?)sid["user"], (string?)sid["password"]));
        Assert.Contains(warnings, w => w.Contains("SID ORCL", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("FLYWAY_PASSWORD", StringComparison.Ordinal));

        warnings.Clear();
        const string descriptor = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=h)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=s)))";
        var full = JdbcOracle.Map(new ConnectionToImport("x", "jdbc:oracle:thin:@" + descriptor, "${env.DB_USER}", "${env.DB_PASSWORD}"), warnings)!;
        Assert.Equal(("Data Source=" + descriptor, "mandatory", "${env:DB_USER}", "${env:DB_PASSWORD}"), ((string?)full["connectionString"], (string?)full["tls"]!["mode"], (string?)full["user"], (string?)full["password"]));
        Assert.Null(full["host"]);

        warnings.Clear();
        var alias = JdbcOracle.Map(new ConnectionToImport("x", "jdbc:oracle:thin:@mydb_high?TNS_ADMIN=/wallets/mydb&oracle.jdbc.fanEnabled=false", "app", null), warnings)!;
        Assert.Equal(("Data Source=mydb_high", "/wallets/mydb"), ((string?)alias["connectionString"], (string?)alias["connectionProperties"]!["TNS_Admin"]));
        Assert.Contains(warnings, w => w.Contains("oracle.jdbc.fanEnabled", StringComparison.Ordinal));

        warnings.Clear();
        Assert.Null(JdbcOracle.Map(new ConnectionToImport("x", "jdbc:postgresql://h/db", null, null), warnings));
        Assert.Contains(warnings, w => w.Contains("is not an Oracle JDBC URL", StringComparison.Ordinal));
    }

    [Fact]
    public void Imports_can_target_oracle()
    {
        var flyway = Path.Combine(root, "flyway-ora");
        Directory.CreateDirectory(Path.Combine(flyway, "sql"));
        File.WriteAllText(Path.Combine(flyway, "flyway.toml"), """
            [flyway]
            environment = "dev"
            [environments.dev]
            url = "jdbc:oracle:thin:@localhost:1521/FREEPDB1"
            user = "app"
            password = "${env.DEV_DB_PASSWORD}"
            """);
        File.WriteAllText(Path.Combine(flyway, "sql", "V1__Init.sql"), "CREATE TABLE t (id NUMBER(10));\n\nBEGIN\n  INSERT INTO t VALUES (1);\nEND;\n/\n");
        var target = Path.Combine(root, "trek-ora-flyway");

        Assert.Equal(0, Commands.ImportFlyway(G(target), flyway, null, [], dryRun: false, providerId: "oracle"));
        var config = TrekConfig.Load(target);
        Assert.Equal("oracle", config.Provider);
        Environment.SetEnvironmentVariable("DEV_DB_PASSWORD", "from-the-environment");
        var dev = config.ResolveEnvironment("dev", null);
        Assert.Equal(("localhost", 1521, "FREEPDB1", "app"), (dev.Host, dev.Port!.Value, dev.Database, dev.User));
        Assert.Equal(0, Commands.PlanCheck(G(target)));

        // Sqitch's Oracle engine moves to the Oracle provider by itself.
        var sqitch = Path.Combine(root, "sqitch-ora");
        Directory.CreateDirectory(Path.Combine(sqitch, "deploy"));
        File.WriteAllText(Path.Combine(sqitch, "sqitch.conf"), "[core]\n\tengine = oracle\n");
        File.WriteAllText(Path.Combine(sqitch, "sqitch.plan"), "%project=flipr\nusers 2026-11-03T20:56:05Z A <a@b>\n");
        File.WriteAllText(Path.Combine(sqitch, "deploy", "users.sql"), "CREATE TABLE users (nick VARCHAR2(100));\n");
        target = Path.Combine(root, "trek-ora-sqitch");

        Assert.Equal(0, Commands.ImportSqitch(G(target), sqitch, null, dryRun: false));
        Assert.Equal("oracle", TrekConfig.Load(target).Provider);
        Assert.Equal(0, Commands.PlanCheck(G(target)));

        var dbup = Path.Combine(root, "dbup-ora");
        Directory.CreateDirectory(dbup);
        File.WriteAllText(Path.Combine(dbup, "001_create.sql"), "CREATE TABLE widget (id NUMBER(10))\n/\nCREATE OR REPLACE PROCEDURE touch_widget AS\nBEGIN\n  NULL;\nEND;\n/\n");
        target = Path.Combine(root, "trek-ora-dbup");
        Assert.Equal(0, Commands.ImportDbUp(G(target), dbup, fileNames: true, [], noVariables: false, dryRun: false, providerId: "oracle"));
        Assert.Equal("oracle", TrekConfig.Load(target).Provider);
        Assert.Equal(0, Commands.PlanCheck(G(target)));

        var grate = Path.Combine(root, "grate-ora");
        Directory.CreateDirectory(Path.Combine(grate, "up"));
        Directory.CreateDirectory(Path.Combine(grate, "views"));
        File.WriteAllText(Path.Combine(grate, "up", "0001_create.sql"), "CREATE TABLE gadget (id NUMBER(10) PRIMARY KEY, name VARCHAR2(50));\n");
        File.WriteAllText(Path.Combine(grate, "views", "gadget_v.sql"), "CREATE OR REPLACE VIEW gadget_v AS SELECT id, name FROM gadget\n");
        target = Path.Combine(root, "trek-ora-grate");
        Assert.Equal(0, Commands.ImportGrate(G(target), grate, [], [], ignoreDirectoryNames: false, noTokens: false, dryRun: false, providerId: "oracle"));
        Assert.Equal("oracle", TrekConfig.Load(target).Provider);
        Assert.Equal(0, Commands.PlanCheck(G(target)));
    }

    [Fact]
    public void Ef_scripts_for_oracle_import_to_an_oracle_project()
    {
        var oracle = Path.Combine(AppContext.BaseDirectory, "EfScripts", "oracle");
        var target = Path.Combine(root, "trek-ora-ef");

        Assert.Equal(0, Commands.ImportEf(G(target), Path.Combine(oracle, "up.sql"), Path.Combine(oracle, "down.sql"), null, null, dryRun: false));

        Assert.Equal("oracle", TrekConfig.Load(target).Provider);
        Assert.Equal(0, Commands.PlanCheck(G(target)));
    }

    [Fact]
    public void Init_for_postgresql_writes_a_postgres_configuration()
    {
        var target = Path.Combine(root, "pg");
        Assert.Equal(0, Commands.Init(G(target), "postgres"));
        var config = TrekConfig.Load(target);
        Assert.Equal("postgresql", config.Provider);
        Environment.SetEnvironmentVariable("TREK_LOCAL_PASSWORD", "x");
        var local = config.ResolveEnvironment("local", null);
        Assert.Equal(("localhost", 5432, "postgres"), (local.Host, local.Port!.Value, local.User));
        Assert.Empty(Commands.Providers.Resolve(config.Provider).ValidateEnvironment(local));
    }

    [Fact]
    public void Imports_can_target_postgresql()
    {
        var source = Path.Combine(root, "dbup-pg");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "0001.sql"), "CREATE TABLE t (id int);");
        File.WriteAllText(Path.Combine(source, "0002.sql"), "CREATE INDEX CONCURRENTLY t_id ON t (id);");
        var target = Path.Combine(root, "trek-pg");

        Assert.Equal(0, Commands.ImportDbUp(G(target), source, false, [], false, dryRun: false, providerId: "postgresql"));

        Assert.Equal("postgresql", TrekConfig.Load(target).Provider);
        var plan = Badger.Trek.Core.Plan.PlanFile.Load(Path.Combine(target, "trek.plan"));
        Assert.True(plan.Changes[1].Flags.HasFlag(Badger.Trek.Core.Plan.ChangeFlags.NoTransaction));
        Assert.Contains("port: 5432", File.ReadAllText(Path.Combine(target, "trek.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Imported_sqlproj_passes_plan_check()
    {
        var source = Path.Combine(root, "ssdt");
        Directory.CreateDirectory(source);
        var sqlproj = Badger.Trek.Providers.SqlServer.Tests.SqlProjSample.WriteClassic(source);
        var target = Path.Combine(root, "trek");

        Assert.Equal(0, Commands.ImportSqlProj(G(target), sqlproj, dryRun: false));

        Assert.Equal(0, Commands.PlanCheck(G(target)));
        Assert.Equal("dev", TrekConfig.Load(target).Variables["Environment"]);
        var plan = File.ReadAllText(Path.Combine(target, "trek.plan"));
        Assert.Contains("create-dbo-customer [create-type-dbo-phone create-dbo-country]", plan, StringComparison.Ordinal);
        Assert.Contains("# sqlproj Tables/Customer.sql: table dbo.Customer", plan, StringComparison.Ordinal);
        Assert.Contains("'trek baseline --from sqlproj' uses them", plan, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(target, "changes", "create-dbo-customer", "revert.sql")));
        Assert.True(File.Exists(Path.Combine(target, "repeatable", "views", "dbo.vBigCustomers.sql")));
        Assert.True(File.Exists(Path.Combine(target, "repeatable", "post-deploy", "Script.PostDeployment.sql")));

        // The folder holding the .sqlproj works too; a dry run writes nothing.
        var again = Path.Combine(root, "trek2");
        Assert.Equal(0, Commands.ImportSqlProj(G(again), source, dryRun: true));
        Assert.False(Directory.Exists(again));
    }
}
