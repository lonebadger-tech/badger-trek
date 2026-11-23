using System.Globalization;
using Badger.Trek.Core;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Oracle.Tests;

[Collection("oracle")]
public sealed class ConversionExportTests(OracleFixture fixture)
{
    private const string Schema = """
        CREATE TABLE legacy_note (id NUMBER PRIMARY KEY, body LONG);
        CREATE TABLE customer (
          id NUMBER(10) GENERATED ALWAYS AS IDENTITY (START WITH 100 INCREMENT BY 5) PRIMARY KEY,
          email VARCHAR2(200 CHAR) NOT NULL,
          code CHAR(1 CHAR) DEFAULT 'A' NOT NULL,
          legacy VARCHAR2(10 BYTE),
          nname NVARCHAR2(50),
          nc NCHAR(2),
          qty NUMBER(10),
          total NUMBER(12,2) DEFAULT 0,
          anynum NUMBER,
          whole INTEGER,
          fl FLOAT,
          bf BINARY_FLOAT,
          bd BINARY_DOUBLE,
          born DATE,
          created TIMESTAMP(6) DEFAULT SYSTIMESTAMP NOT NULL,
          tz TIMESTAMP(6) WITH TIME ZONE,
          ltz TIMESTAMP(6) WITH LOCAL TIME ZONE,
          ids INTERVAL DAY(2) TO SECOND(6),
          iym INTERVAL YEAR(2) TO MONTH,
          notes CLOB,
          nnotes NCLOB,
          photo BLOB,
          token RAW(16),
          doc JSON,
          active BOOLEAN,
          xml XMLTYPE,
          rid ROWID,
          email_lower VARCHAR2(200 CHAR) GENERATED ALWAYS AS (LOWER(email)) VIRTUAL,
          CONSTRAINT uq_customer_email UNIQUE (email),
          CONSTRAINT ck_customer_total CHECK (total >= 0)
        );
        COMMENT ON TABLE customer IS 'people who buy';
        COMMENT ON COLUMN customer.email IS 'login name';
        CREATE TABLE orders (
          id NUMBER(10) NOT NULL,
          customer_id NUMBER(10) NOT NULL,
          note_id NUMBER,
          CONSTRAINT pk_orders PRIMARY KEY (id),
          CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id) ON DELETE CASCADE,
          FOREIGN KEY (note_id) REFERENCES legacy_note (id) ON DELETE SET NULL,
          CHECK (id > 0)
        );
        CREATE TABLE order_line (
          order_id NUMBER(10) NOT NULL,
          line_no NUMBER(5) NOT NULL,
          CONSTRAINT pk_order_line PRIMARY KEY (order_id, line_no),
          CONSTRAINT fk_line_order FOREIGN KEY (order_id) REFERENCES orders (id) DEFERRABLE INITIALLY IMMEDIATE
        );
        CREATE TABLE scratch_tmp (id NUMBER PRIMARY KEY);
        CREATE TABLE events (id NUMBER, at DATE) PARTITION BY RANGE (at) (PARTITION p1 VALUES LESS THAN (DATE '2027-01-01'), PARTITION pmax VALUES LESS THAN (MAXVALUE));
        CREATE INDEX ix_orders_customer ON orders (customer_id, id DESC);
        CREATE INDEX ix_customer_lower ON customer (LOWER(email));
        CREATE UNIQUE INDEX ux_customer_legacy ON customer (legacy);
        CREATE BITMAP INDEX bx_customer_code ON customer (code);
        CREATE SEQUENCE invoice_no START WITH 5000 INCREMENT BY 10 MAXVALUE 99999 CYCLE;
        CREATE OR REPLACE FUNCTION order_tax(amount NUMBER) RETURN NUMBER DETERMINISTIC IS
        BEGIN
          RETURN amount * 0.08;
        END;
        /
        CREATE OR REPLACE VIEW v_customer AS SELECT id, email FROM customer WHERE total > 0;
        CREATE OR REPLACE VIEW big_spenders AS SELECT c.email, order_tax(o.id) AS tax FROM v_customer c JOIN orders o ON o.customer_id = c.id;
        CREATE OR REPLACE PACKAGE shop AS
          FUNCTION order_count(p_customer NUMBER) RETURN NUMBER;
        END shop;
        /
        CREATE OR REPLACE PACKAGE BODY shop AS
          FUNCTION order_count(p_customer NUMBER) RETURN NUMBER IS
            v NUMBER;
          BEGIN
            SELECT COUNT(*) INTO v FROM orders WHERE customer_id = p_customer;
            RETURN v;
          END;
        END shop;
        /
        CREATE OR REPLACE PROCEDURE close_customer(p_id NUMBER) IS
        BEGIN
          UPDATE customer SET total = 0 WHERE id = p_id;
        END;
        /
        CREATE OR REPLACE TRIGGER orders_bi BEFORE INSERT ON orders FOR EACH ROW
        BEGIN
          IF :NEW.note_id IS NULL THEN
            :NEW.note_id := NULL;
          END IF;
        END;
        /
        CREATE TYPE address_t AS OBJECT (street VARCHAR2(100));
        /
        CREATE SYNONYM purchases FOR orders;
        CREATE MATERIALIZED VIEW mv_totals AS SELECT customer_id, COUNT(*) AS n FROM orders GROUP BY customer_id;
        """;

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
    public async Task The_exporter_reads_every_feature_of_the_schema()
    {
        await using var db = await RequireSchemaAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, Schema);
        await provider.StateStore.EnsureCreatedAsync(session, "trek", CancellationToken.None);

        var schema = await provider.Exporter.ExportAsync(session, new ExportOptions { StateSchema = "trek", Excluded = n => n.Name == "SCRATCH_TMP" }, CancellationToken.None);
        var n = db.Name;

        Assert.Equal("oracle", schema.SourceProvider);
        Assert.Equal(n, schema.DefaultSchema);
        Assert.Equal([n], schema.Schemas);
        Assert.False(schema.CaseInsensitive);
        Assert.Null(schema.Collation);
        Assert.Equal(["CUSTOMER", "EVENTS", "LEGACY_NOTE", "ORDERS", "ORDER_LINE"], schema.Tables.Select(t => t.Name.Name));
        Assert.All(schema.Tables, t => Assert.Equal(n, t.Name.Schema));

        var customer = schema.Tables.Single(t => t.Name.Name == "CUSTOMER");
        Assert.Equal("people who buy", customer.Comment);
        Assert.Equal(
            ["ID", "EMAIL", "CODE", "LEGACY", "NNAME", "NC", "QTY", "TOTAL", "ANYNUM", "WHOLE", "FL", "BF", "BD", "BORN", "CREATED", "TZ", "LTZ", "IDS", "IYM", "NOTES", "NNOTES", "PHOTO", "TOKEN", "DOC", "ACTIVE", "XML", "RID", "EMAIL_LOWER"],
            customer.Columns.Select(c => c.Name));
        PortableColumn C(string name) => customer.Columns.Single(c => c.Name == name);
        Assert.Equal(
            ["NUMBER(10)", "VARCHAR2(200 CHAR)", "CHAR(1 CHAR)", "VARCHAR2(10 BYTE)", "NVARCHAR2(50)", "NCHAR(2)", "NUMBER(10)", "NUMBER(12,2)", "NUMBER", "INTEGER", "FLOAT(126)",
             "BINARY_FLOAT", "BINARY_DOUBLE", "DATE", "TIMESTAMP(6)", "TIMESTAMP(6) WITH TIME ZONE", "TIMESTAMP(6) WITH LOCAL TIME ZONE", "INTERVAL DAY(2) TO SECOND(6)",
             "INTERVAL YEAR(2) TO MONTH", "CLOB", "NCLOB", "BLOB", "RAW(16)", "JSON", "BOOLEAN", "XMLTYPE", "ROWID", "VARCHAR2(200 CHAR)"],
            customer.Columns.Select(c => c.Type));

        Assert.Equal(new PortableIdentity(100, 5, true), C("ID").Identity);
        Assert.Null(C("ID").Default);
        Assert.False(C("ID").Nullable);
        Assert.Equal("login name", C("EMAIL").Comment);
        Assert.False(C("EMAIL").Nullable);
        Assert.Equal("'A'", C("CODE").Default);
        Assert.Equal("0", C("TOTAL").Default);
        Assert.True(C("TOTAL").Nullable);
        Assert.Equal("SYSTIMESTAMP", C("CREATED").Default);
        Assert.Null(C("NOTES").Default);
        Assert.Equal("LOWER(\"EMAIL\")", C("EMAIL_LOWER").Computed);
        Assert.False(C("EMAIL_LOWER").ComputedStored);
        Assert.Null(C("EMAIL_LOWER").Default);
        Assert.Null(C("XML").Computed);
        Assert.Equal("BODY", schema.Tables.Single(t => t.Name.Name == "LEGACY_NOTE").Columns[1].Name);
        Assert.Equal("LONG", schema.Tables.Single(t => t.Name.Name == "LEGACY_NOTE").Columns[1].Type);

        Assert.Null(customer.PrimaryKey!.Name);
        Assert.Equal(["ID"], customer.PrimaryKey.Columns);
        var unique = Assert.Single(customer.Uniques);
        Assert.Equal("UQ_CUSTOMER_EMAIL", unique.Name);
        Assert.Equal(["EMAIL"], unique.Columns);
        var check = Assert.Single(customer.Checks);
        Assert.Equal("CK_CUSTOMER_TOTAL", check.Name);
        Assert.Equal("total >= 0", check.Expression);

        var indexes = customer.Indexes.ToDictionary(i => i.Name);
        Assert.Equal(["BX_CUSTOMER_CODE", "IX_CUSTOMER_LOWER", "UX_CUSTOMER_LEGACY"], indexes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("bitmap", indexes["BX_CUSTOMER_CODE"].Method);
        Assert.Equal([new PortableIndexColumn("CODE", false, false)], indexes["BX_CUSTOMER_CODE"].Columns);
        var expression = Assert.Single(indexes["IX_CUSTOMER_LOWER"].Columns);
        Assert.True(expression.IsExpression);
        Assert.Equal("LOWER(\"EMAIL\")", expression.Text);
        Assert.Null(indexes["IX_CUSTOMER_LOWER"].Method);
        Assert.True(indexes["UX_CUSTOMER_LEGACY"].Unique);
        Assert.False(indexes["IX_CUSTOMER_LOWER"].Unique);

        var orders = schema.Tables.Single(t => t.Name.Name == "ORDERS");
        Assert.Equal("PK_ORDERS", orders.PrimaryKey!.Name);
        Assert.Equal([new PortableIndexColumn("CUSTOMER_ID", false, false), new PortableIndexColumn("ID", false, true)], Assert.Single(orders.Indexes).Columns);
        var ordersCheck = Assert.Single(orders.Checks);
        Assert.Null(ordersCheck.Name);
        Assert.Equal("id > 0", ordersCheck.Expression);
        var fks = orders.ForeignKeys.OrderBy(f => f.Columns[0], StringComparer.Ordinal).ToList();
        Assert.Equal(2, fks.Count);
        Assert.Equal("FK_ORDERS_CUSTOMER", fks[0].Name);
        Assert.Equal(["CUSTOMER_ID"], fks[0].Columns);
        Assert.Equal(new PortableName(n, "CUSTOMER"), fks[0].Referenced);
        Assert.Equal(["ID"], fks[0].ReferencedColumns);
        Assert.Equal("CASCADE", fks[0].OnDelete);
        Assert.Equal("NO ACTION", fks[0].OnUpdate);
        Assert.Null(fks[1].Name);
        Assert.Equal(new PortableName(n, "LEGACY_NOTE"), fks[1].Referenced);
        Assert.Equal("SET NULL", fks[1].OnDelete);

        var line = schema.Tables.Single(t => t.Name.Name == "ORDER_LINE");
        Assert.Equal(["ORDER_ID", "LINE_NO"], line.PrimaryKey!.Columns);
        Assert.Equal("NO ACTION", Assert.Single(line.ForeignKeys).OnDelete);
        Assert.Empty(line.Checks);

        var sequence = Assert.Single(schema.Sequences);
        Assert.Equal(new PortableSequence(new PortableName(n, "INVOICE_NO"), "NUMBER", 5000, 10, 1, 99999, true), sequence);

        // Code: functions and packages, views after what they use, procedures, triggers.
        Assert.Equal(
            [(PortableCodeKind.Function, "ORDER_TAX"), (PortableCodeKind.Function, "SHOP"), (PortableCodeKind.View, "V_CUSTOMER"), (PortableCodeKind.View, "BIG_SPENDERS"), (PortableCodeKind.Procedure, "CLOSE_CUSTOMER"), (PortableCodeKind.Trigger, "ORDERS_BI")],
            schema.Code.Select(c => (c.Kind, c.Name.Name)));
        Assert.All(schema.Code, c =>
        {
            Assert.Equal(c.Kind == PortableCodeKind.View ? "sql" : "plsql", c.Language);
            Assert.Equal(n, c.Name.Schema);
            Assert.DoesNotContain(n, c.Definition, StringComparison.Ordinal);
            Assert.StartsWith("CREATE OR REPLACE ", c.Definition, StringComparison.Ordinal);
        });
        var bigSpenders = schema.Code.Single(c => c.Name.Name == "BIG_SPENDERS");
        Assert.StartsWith("CREATE OR REPLACE VIEW BIG_SPENDERS AS\nSELECT c.email", bigSpenders.Definition, StringComparison.Ordinal);
        Assert.Equal([$"{n}.order_tax".ToLowerInvariant(), $"{n}.v_customer".ToLowerInvariant()], bigSpenders.DependsOn);
        var shop = schema.Code.Single(c => c.Name.Name == "SHOP");
        Assert.StartsWith("CREATE OR REPLACE PACKAGE shop AS", shop.Definition, StringComparison.Ordinal);
        Assert.Contains("END shop;\n/\nCREATE OR REPLACE PACKAGE BODY shop AS", shop.Definition, StringComparison.Ordinal);
        Assert.EndsWith("END shop;\n/", shop.Definition, StringComparison.Ordinal);
        var trigger = schema.Code.Single(c => c.Kind == PortableCodeKind.Trigger);
        Assert.Equal(new PortableName(n, "ORDERS"), trigger.Table);
        Assert.StartsWith("CREATE OR REPLACE TRIGGER orders_bi BEFORE INSERT ON orders", trigger.Definition, StringComparison.Ordinal);

        Assert.Contains($"materialized view {n}.MV_TOTALS is not converted.", schema.Warnings);
        Assert.Contains($"{n}.EVENTS: partitioning is not converted.", schema.Warnings);
        Assert.Contains($"type {n}.ADDRESS_T is not converted.", schema.Warnings);
        Assert.Contains($"synonym {n}.PURCHASES is not converted.", schema.Warnings);
        Assert.Contains(schema.Warnings, w => w.StartsWith($"package {n}.SHOP", StringComparison.Ordinal));
        Assert.DoesNotContain(schema.Tables, t => t.Name.Name.StartsWith("TREK_", StringComparison.Ordinal) || t.Name.Name == "MV_TOTALS");
    }

    private static async IAsyncEnumerable<object?[]> Rows(IEnumerable<object?[]> rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    private static async Task<List<object?[]>> ReadAllAsync(IDataTransfer data, IDatabaseSession session, PortableName table)
    {
        var columns = await data.ColumnsAsync(session, table, CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in data.ReadAsync(session, table, columns, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<string> ScalarAsync(IDatabaseSession session, string sql) =>
        Convert.ToString((await session.QueryAsync(sql, null, CancellationToken.None))[0].Values.First(), CultureInfo.InvariantCulture)!;

    [SkippableFact]
    public async Task Rows_of_every_type_survive_a_write_and_a_read()
    {
        await using var db = await RequireSchemaAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, """
            CREATE TABLE parent (id NUMBER(10) GENERATED ALWAYS AS IDENTITY PRIMARY KEY, label VARCHAR2(20 CHAR) NOT NULL);
            CREATE SEQUENCE sample_seq;
            CREATE TABLE sample (
              id NUMBER(10) DEFAULT sample_seq.NEXTVAL NOT NULL PRIMARY KEY,
              parent_id NUMBER(10) CONSTRAINT fk_sample_parent REFERENCES parent (id) DEFERRABLE INITIALLY IMMEDIATE,
              s VARCHAR2(100 CHAR),
              c CHAR(3 CHAR),
              ns NVARCHAR2(50),
              t CLOB,
              nt NCLOB,
              n NUMBER(12,2),
              big NUMBER(19),
              whole INTEGER,
              anynum NUMBER,
              flag NUMBER(1),
              bf BINARY_FLOAT,
              bd BINARY_DOUBLE,
              d DATE,
              ts TIMESTAMP(6),
              tz TIMESTAMP(6) WITH TIME ZONE,
              ltz TIMESTAMP(6) WITH LOCAL TIME ZONE,
              ids INTERVAL DAY(3) TO SECOND(6),
              iym INTERVAL YEAR(2) TO MONTH,
              g VARCHAR2(36 CHAR),
              gr RAW(16),
              bin BLOB,
              j JSON,
              b BOOLEAN,
              x XMLTYPE,
              twice NUMBER GENERATED ALWAYS AS (parent_id * 2) VIRTUAL
            );
            CREATE OR REPLACE TRIGGER sample_bi BEFORE INSERT ON sample FOR EACH ROW
            BEGIN
              :NEW.s := 'trigger';
            END;
            /
            """);
        var data = provider.Data;
        var parent = new PortableName(db.Name, "PARENT");
        var sample = new PortableName(db.Name, "SAMPLE");
        var columns = await data.ColumnsAsync(session, sample, CancellationToken.None);
        Assert.Equal(["ID", "PARENT_ID", "S", "C", "NS", "T", "NT", "N", "BIG", "WHOLE", "ANYNUM", "FLAG", "BF", "BD", "D", "TS", "TZ", "LTZ", "IDS", "IYM", "G", "GR", "BIN", "J", "B", "X"], columns.Select(c => c.Name));
        Assert.Equal("NUMBER(1)", columns.Single(c => c.Name == "FLAG").Type);
        Assert.Equal("INTERVAL DAY(3) TO SECOND(6)", columns.Single(c => c.Name == "IDS").Type);

        var guid = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        var at = new DateTimeOffset(2026, 11, 20, 8, 30, 15, TimeSpan.FromHours(-6)).AddTicks(1234560);
        var longText = string.Concat(Enumerable.Repeat("0123456789ü", 5000));
        object?[] full =
        [
            7, 1, "héllo ✓ 日本 \U0001F600", "abc", "日本", longText, "ünï", 12.34m, long.MaxValue, "123456789012345678901234567890",
            "0.333333333333333333333333333333333333", true, 1.5f, 2.25, new DateTime(2026, 1, 2), new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Unspecified),
            at, at, new TimeSpan(3, 4, 5, 6, 500), "1-2", guid, guid, new byte[] { 0, 1, 127, 128, 255, 92, 39 }, """{"a":[1,2],"b":"x\\y"}""", true, "<a>1</a>",
        ];
        object?[] other =
        [
            8L, null, "x", null, null, null, null, 5, 1, 2L, 1.25, false, "2.5", 3.5f, new DateOnly(2025, 5, 6), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), "1 day 02:00:00", "-0-3", guid.ToString(), null, null, null, 0, null,
        ];
        object?[] empty = [9, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null];

        await data.BeginLoadAsync(session, [parent, sample], CancellationToken.None);
        Assert.Equal(3, await data.WriteAsync(session, sample, columns, Rows([full, other, empty]), CancellationToken.None));
        var parentColumns = await data.ColumnsAsync(session, parent, CancellationToken.None);
        Assert.Equal(1203, await data.WriteAsync(session, parent, parentColumns, Rows(Enumerable.Range(1, 1203).Select(i => new object?[] { i, "p" + i })), CancellationToken.None));
        await data.EndLoadAsync(session, [parent, sample], CancellationToken.None);
        Assert.False(session.InTransaction);

        Assert.Equal(1203, await data.CountAsync(session, parent, CancellationToken.None));
        var rows = await ReadAllAsync(data, session, sample);
        Assert.Equal(3, rows.Count);
        var r = rows.Single(x => Equals(x[0], 7L));
        Assert.Equal(1L, r[1]);
        Assert.Equal(full[2], r[2]);
        Assert.Equal("abc", r[3]);
        Assert.Equal("日本", r[4]);
        Assert.Equal(longText, r[5]);
        Assert.Equal("ünï", r[6]);
        Assert.Equal(12.34m, r[7]);
        Assert.Equal((decimal)long.MaxValue, r[8]);
        Assert.Equal("123456789012345678901234567890", r[9]);
        Assert.Equal("0.333333333333333333333333333333333333", ((string)r[10]!).TrimStart('0').Insert(0, "0"));
        Assert.Equal(1L, r[11]);
        Assert.Equal(1.5f, r[12]);
        Assert.Equal(2.25, r[13]);
        Assert.Equal(new DateTime(2026, 1, 2), r[14]);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, 678), r[15]);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)r[15]!).Kind);
        var tz = Assert.IsType<DateTimeOffset>(r[16]);
        Assert.Equal(at, tz);
        Assert.Equal(TimeSpan.FromHours(-6), tz.Offset);
        var ltz = Assert.IsType<DateTime>(r[17]);
        Assert.Equal(DateTimeKind.Utc, ltz.Kind);
        Assert.Equal(at.UtcDateTime, ltz);
        Assert.Equal(new TimeSpan(3, 4, 5, 6, 500), r[18]);
        Assert.Equal("1-2", r[19]);
        Assert.Equal(guid.ToString(), r[20]);
        Assert.Equal(guid.ToByteArray(bigEndian: true), r[21]);
        Assert.Equal(full[22], r[22]);
        Assert.Equal(full[23], r[23]);
        Assert.Equal(true, r[24]);
        Assert.Contains("<a>1</a>", (string)r[25]!, StringComparison.Ordinal);

        var o = rows.Single(x => Equals(x[0], 8L));
        Assert.Equal("x", o[2]);
        Assert.Equal(5m, o[7]);
        Assert.Equal(1m, o[8]);
        Assert.Equal(2L, o[9]);
        Assert.Equal(1.25m, o[10]);
        Assert.Equal(0L, o[11]);
        Assert.Equal(2.5f, o[12]);
        Assert.Equal(3.5, o[13]);
        Assert.Equal(new DateTime(2025, 5, 6), o[14]);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), o[15]);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), o[16]);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), o[17]);
        Assert.Equal(new TimeSpan(1, 2, 0, 0), o[18]);
        Assert.Equal("-0-3", o[19]);
        Assert.Equal(guid.ToString(), o[20]);
        Assert.Equal(false, o[24]);
        Assert.All(rows.Single(x => Equals(x[0], 9L)).Skip(1), Assert.Null);

        // Inside an open transaction the rows come back the same way.
        await session.BeginTransactionAsync(CancellationToken.None);
        var inTransaction = (await ReadAllAsync(data, session, sample)).Single(x => Equals(x[0], 7L));
        await session.RollbackAsync(CancellationToken.None);
        Assert.Equal(r, inTransaction);

        // The trigger did not fire during the load and is enabled again; the identity is GENERATED ALWAYS again and
        // continues after the loaded values, and so does the sequence behind the NEXTVAL default.
        Assert.Equal("ENABLED", await ScalarAsync(session, "SELECT status FROM user_triggers WHERE trigger_name = 'SAMPLE_BI'"));
        Assert.Equal("ALWAYS", await ScalarAsync(session, "SELECT generation_type FROM user_tab_identity_cols WHERE table_name = 'PARENT'"));
        await session.ExecuteAsync("INSERT INTO parent (label) VALUES ('next')", null, CancellationToken.None);
        Assert.Equal("1204", await ScalarAsync(session, "SELECT MAX(id) FROM parent"));
        await session.ExecuteAsync("INSERT INTO sample (parent_id) VALUES (1)", null, CancellationToken.None);
        Assert.Equal("10", await ScalarAsync(session, "SELECT MAX(id) FROM sample"));
        Assert.Equal("trigger", await ScalarAsync(session, "SELECT s FROM sample WHERE id = 10"));

        // Truncate empties tables that others reference, in any order, and leaves the foreign keys enabled and validated.
        await data.TruncateAsync(session, [parent, sample], CancellationToken.None);
        Assert.Equal(0, await data.CountAsync(session, parent, CancellationToken.None));
        Assert.Equal(0, await data.CountAsync(session, sample, CancellationToken.None));
        Assert.Equal("ENABLED VALIDATED", await ScalarAsync(session, "SELECT status || ' ' || validated FROM user_constraints WHERE constraint_name = 'FK_SAMPLE_PARENT'"));
    }

    [SkippableFact]
    public async Task Truncating_a_parent_whose_children_keep_rows_fails_and_names_the_foreign_key()
    {
        await using var db = await RequireSchemaAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, """
            CREATE TABLE parent (id NUMBER PRIMARY KEY);
            CREATE TABLE child (id NUMBER PRIMARY KEY, parent_id NUMBER CONSTRAINT fk_child_parent REFERENCES parent (id));
            INSERT INTO parent VALUES (1);
            INSERT INTO child VALUES (1, 1);
            COMMIT;
            """);
        var data = provider.Data;
        var error = await Assert.ThrowsAsync<MigrationException>(() => data.TruncateAsync(session, [new PortableName(db.Name, "PARENT")], CancellationToken.None));
        Assert.Contains("FK_CHILD_PARENT", error.Message, StringComparison.Ordinal);
        Assert.Contains("CHILD", error.Message, StringComparison.Ordinal);
        Assert.Equal("ENABLED", await ScalarAsync(session, "SELECT status FROM user_constraints WHERE constraint_name = 'FK_CHILD_PARENT'"));
    }

    [SkippableFact]
    public async Task An_empty_string_in_a_not_null_column_fails_the_load_and_restores_the_triggers()
    {
        await using var db = await RequireSchemaAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, """
            CREATE TABLE item (id NUMBER(10) GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name VARCHAR2(10 CHAR) NOT NULL);
            CREATE OR REPLACE TRIGGER item_bi BEFORE INSERT ON item FOR EACH ROW
            BEGIN
              NULL;
            END;
            /
            """);
        var data = provider.Data;
        var item = new PortableName(db.Name, "ITEM");
        var columns = await data.ColumnsAsync(session, item, CancellationToken.None);

        await data.BeginLoadAsync(session, [item], CancellationToken.None);
        Assert.Equal("DISABLED", await ScalarAsync(session, "SELECT status FROM user_triggers WHERE trigger_name = 'ITEM_BI'"));
        var error = await Assert.ThrowsAsync<MigrationException>(() => data.WriteAsync(session, item, columns, Rows([[1, "a"], [2, ""]]), CancellationToken.None));
        Assert.Contains("ITEM.NAME", error.Message, StringComparison.Ordinal);
        Assert.Contains("empty strings, which Oracle stores as NULL", error.Message, StringComparison.Ordinal);
        Assert.False(session.InTransaction);
        Assert.Equal(0, await data.CountAsync(session, item, CancellationToken.None));
        Assert.Equal("ENABLED", await ScalarAsync(session, "SELECT status FROM user_triggers WHERE trigger_name = 'ITEM_BI'"));
        Assert.Equal("ALWAYS", await ScalarAsync(session, "SELECT generation_type FROM user_tab_identity_cols WHERE table_name = 'ITEM'"));
    }

    [SkippableFact]
    public async Task Ending_the_load_fails_for_a_row_without_its_parent()
    {
        await using var db = await RequireSchemaAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, """
            CREATE TABLE parent (a NUMBER NOT NULL, b NUMBER NOT NULL, PRIMARY KEY (a, b));
            CREATE TABLE child (id NUMBER NOT NULL PRIMARY KEY, a NUMBER, b NUMBER, CONSTRAINT fk_child_parent FOREIGN KEY (a, b) REFERENCES parent (a, b) DEFERRABLE INITIALLY IMMEDIATE);
            CREATE OR REPLACE TRIGGER child_bi BEFORE INSERT ON child FOR EACH ROW
            BEGIN
              NULL;
            END;
            /
            """);
        var data = provider.Data;
        var parent = new PortableName(db.Name, "PARENT");
        var child = new PortableName(db.Name, "CHILD");
        var columns = await data.ColumnsAsync(session, child, CancellationToken.None);

        await data.BeginLoadAsync(session, [parent, child], CancellationToken.None);
        // Children before parents: the deferred foreign key is checked only at the commit.
        await data.WriteAsync(session, child, columns, Rows([[1, 1, 1], [2, null, 5], [3, 1, 2]]), CancellationToken.None);
        await data.WriteAsync(session, parent, await data.ColumnsAsync(session, parent, CancellationToken.None), Rows([[1, 1]]), CancellationToken.None);
        var error = await Assert.ThrowsAsync<MigrationException>(() => data.EndLoadAsync(session, [parent, child], CancellationToken.None));
        Assert.Contains("CHILD", error.Message, StringComparison.Ordinal);
        Assert.Contains("FK_CHILD_PARENT", error.Message, StringComparison.Ordinal);
        Assert.False(session.InTransaction);
        Assert.Equal(0, await data.CountAsync(session, child, CancellationToken.None));
        Assert.Equal(0, await data.CountAsync(session, parent, CancellationToken.None));
        Assert.Equal("ENABLED", await ScalarAsync(session, "SELECT status FROM user_triggers WHERE trigger_name = 'CHILD_BI'"));
    }
}
