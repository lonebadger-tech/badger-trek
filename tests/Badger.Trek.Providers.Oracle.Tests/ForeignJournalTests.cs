using Badger.Trek.Core.Import;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Oracle.Tests;

/// <summary>
/// 'trek baseline --from flyway|ef|sqitch' on Oracle: the journals as each tool creates them there (Flyway's and EF's
/// with quoted names, Sqitch's registry unquoted, all in the login's schema), and EF's Oracle scripts as imported.
/// </summary>
[Collection("oracle")]
public sealed class ForeignJournalTests(OracleFixture fixture)
{
    private readonly OracleProvider provider = new();

    private async Task<TestSchema> RequireSchemaAsync()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        return await fixture.CreateSchemaAsync();
    }

    private async Task RunAsync(IDatabaseSession session, string script)
    {
        foreach (var batch in provider.Parser.Split(script))
        {
            await session.ExecuteAsync(batch.Text, null, CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Flyway_history_is_read_from_the_quoted_table()
    {
        await using var db = await RequireSchemaAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);

        // Flyway's Oracle createMetaDataTable.sql.
        await RunAsync(session, """
            CREATE TABLE "flyway_schema_history" (
              "installed_rank" INT NOT NULL,
              "version" VARCHAR2(50),
              "description" VARCHAR2(200) NOT NULL,
              "type" VARCHAR2(20) NOT NULL,
              "script" VARCHAR2(1000) NOT NULL,
              "checksum" INT,
              "installed_by" VARCHAR2(100) NOT NULL,
              "installed_on" TIMESTAMP DEFAULT CURRENT_TIMESTAMP NOT NULL,
              "execution_time" INT NOT NULL,
              "success" NUMBER(1) NOT NULL,
              CONSTRAINT "flyway_schema_history_pk" PRIMARY KEY ("installed_rank")
            );
            INSERT INTO "flyway_schema_history" ("installed_rank", "version", "description", "type", "script", "checksum", "installed_by", "execution_time", "success") VALUES (1, '1', 'Init', 'SQL', 'V1__Init.sql', -1234567890, 'APP', 12, 1);
            INSERT INTO "flyway_schema_history" ("installed_rank", "version", "description", "type", "script", "checksum", "installed_by", "execution_time", "success") VALUES (2, NULL, 'views', 'SQL', 'R__views.sql', 42, 'APP', 3, 0);
            """);

        var rows = await ForeignJournal.ReadFlywayAsync(session, provider, string.Empty, ForeignJournal.FlywayDefaultTable, CancellationToken.None);

        Assert.Equal(
            [new FlywayHistoryRow(1, "1", "SQL", -1234567890, true, "V1__Init.sql"), new FlywayHistoryRow(2, null, "SQL", 42, false, "R__views.sql")],
            rows);
    }

    [SkippableFact]
    public async Task Sqitch_registry_is_read_from_upper_case_tables()
    {
        await using var db = await RequireSchemaAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);

        // The columns trek reads from Sqitch's oracle.sql (&registry. is the login's schema by default).
        await RunAsync(session, """
            CREATE TABLE changes (
              change_id CHAR(40) PRIMARY KEY,
              script_hash CHAR(40) NULL,
              change VARCHAR2(512 CHAR) NOT NULL,
              project VARCHAR2(512 CHAR) NOT NULL,
              committed_at TIMESTAMP WITH TIME ZONE DEFAULT current_timestamp NOT NULL
            );
            INSERT INTO changes (change_id, script_hash, change, project, committed_at) VALUES (RPAD('b', 40, 'b'), RPAD('2', 40, '2'), 'flips', 'flipr', TIMESTAMP '2026-11-03 20:56:06 +00:00');
            INSERT INTO changes (change_id, script_hash, change, project, committed_at) VALUES (RPAD('a', 40, 'a'), RPAD('1', 40, '1'), 'users', 'flipr', TIMESTAMP '2026-11-03 20:56:05 +00:00');
            """);

        var rows = await ForeignJournal.ReadSqitchAsync(session, provider, string.Empty, "CHANGES", CancellationToken.None);

        Assert.Equal([new SqitchChangeRow("users", "flipr", new string('1', 40)), new SqitchChangeRow("flips", "flipr", new string('2', 40))], rows);
    }

    [SkippableFact]
    public async Task Imported_ef_migrations_deploy_revert_and_match_the_history()
    {
        await using var db = await RequireSchemaAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        string Script(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EfScripts", "oracle", name + ".sql")).Replace("\r\n", "\n", StringComparison.Ordinal);

        // EF itself created the history table and recorded the first two migrations.
        var up = Script("up");
        await RunAsync(session, up[..(up.IndexOf("\n/\n", StringComparison.Ordinal) + 3)]);
        var imported = EfImporter.Import(new EfImportOptions { UpScript = up, DownScript = Script("down") });
        var ids = imported.Changes.Select(c => c.Note[3..]).ToList();
        foreach (var id in ids.Take(2))
        {
            await session.ExecuteAsync($"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{id}', '10.0.15')", null, CancellationToken.None);
        }

        Assert.Equal(ids.Take(2), await ForeignJournal.ReadEfAsync(session, provider, string.Empty, EfImporter.HistoryTable, CancellationToken.None));

        // Every imported change runs on Oracle as trek splits it, and its revert undoes it.
        foreach (var change in imported.Changes)
        {
            await RunAsync(session, change.Deploy);
        }

        var columns = await session.QueryAsync("SELECT column_name FROM user_tab_columns WHERE table_name = 'Blogs' ORDER BY column_id", null, CancellationToken.None);
        Assert.Equal(["Id", "Name", "Url", "Rating"], columns.Select(r => (string)r["column_name"]!));

        foreach (var change in Enumerable.Reverse(imported.Changes))
        {
            await RunAsync(session, change.Revert!);
        }

        var left = await session.QueryAsync("SELECT table_name FROM user_tables ORDER BY table_name", null, CancellationToken.None);
        Assert.Equal(["__EFMigrationsHistory"], left.Select(r => (string)r["table_name"]!));
    }
}
