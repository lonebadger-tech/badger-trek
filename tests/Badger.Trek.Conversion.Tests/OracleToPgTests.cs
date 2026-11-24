using Badger.Trek.Conversion.Oracle;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;
using Xunit.Abstractions;

namespace Badger.Trek.Conversion.Tests;

/// <summary>
/// Oracle expressions (DATA_DEFAULT, check conditions, virtual columns) and views (CREATE VIEW, or ALL_VIEWS.TEXT) in
/// PostgreSQL, and Oracle to SQLite and MySQL by way of PostgreSQL.
/// </summary>
public class OracleToPgTests(ITestOutputHelper output)
{
    private string Expr(string oracle, string? targetType = null, NameStyle? names = null)
    {
        var result = new OracleToPgTranslator(Schemas.OracleToPg(names)).Expression(oracle, new ExpressionSite("SHOP.ORDERS", targetType));
        output.WriteLine(result);
        return result;
    }

    private TranslatedCode Translate(PortableCode code, NameStyle? names = null)
    {
        var result = new OracleToPgTranslator(Schemas.OracleToPg(names)).Code(code, [code]);
        output.WriteLine(result.Script);
        return result;
    }

    private static PortableCode View(string name, string body) =>
        new(PortableCodeKind.View, new PortableName("SHOP", name), $"CREATE OR REPLACE FORCE EDITIONABLE VIEW \"SHOP\".\"{name}\" AS {body}");

    [Theory]
    [InlineData("SYSDATE ", "LOCALTIMESTAMP(0)")]
    [InlineData("SYSTIMESTAMP", "now()")]
    [InlineData("CURRENT_DATE", "LOCALTIMESTAMP(0)")]
    [InlineData("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP")]
    [InlineData("'new'", "'new'")]
    [InlineData("q'[it's]'", "'it''s'")]
    [InlineData("N'abc'", "'abc'")]
    [InlineData("0", "0")]
    [InlineData("\"PRICE\"*\"QTY\"", "price * qty")]
    [InlineData("\"QTY\" > 0", "qty > 0")]
    [InlineData("STATUS IN ('new','paid')", "status IN ('new', 'paid')")]
    [InlineData("NVL(UPDATED, CREATED)", "coalesce(updated, created)")]
    [InlineData("NVL2(NOTE, 'y', 'n')", "CASE WHEN note IS NOT NULL THEN 'y' ELSE 'n' END")]
    [InlineData("DECODE(STATUS, 'new', 1, 'paid', 2, 0)", "CASE status WHEN 'new' THEN 1 WHEN 'paid' THEN 2 ELSE 0 END")]
    [InlineData("DECODE(NOTE, NULL, 'none', NOTE)", "CASE WHEN note IS NULL THEN 'none' ELSE note END")]
    [InlineData("DECODE(NOTE, CUSTOMER_NAME, 1, 0)", "CASE WHEN note IS NOT DISTINCT FROM customer_name THEN 1 ELSE 0 END")]
    [InlineData("SUBSTR(CUSTOMER_NAME, 1, 3)", "substr(customer_name, 1, 3)")]
    [InlineData("SUBSTR(CUSTOMER_NAME, 0, 3)", "substr(customer_name, 1, 3)")]
    [InlineData("INSTR(CUSTOMER_NAME, 'x')", "strpos(customer_name, 'x')")]
    [InlineData("LENGTH(CUSTOMER_NAME)", "length(customer_name)")]
    [InlineData("UPPER(TRIM(CUSTOMER_NAME))", "upper(btrim(customer_name))")]
    [InlineData("REPLACE(CUSTOMER_NAME, 'x')", "replace(customer_name, 'x', '')")]
    [InlineData("CONCAT(CUSTOMER_NAME, NOTE)", "concat(customer_name, note)")]
    [InlineData("CUSTOMER_NAME || ' (' || STATUS || ')'", "customer_name || ' (' || status || ')'")]
    [InlineData("TRUNC(SYSDATE)", "date_trunc('day', LOCALTIMESTAMP(0))")]
    [InlineData("TRUNC(CREATED, 'MM')", "date_trunc('month', created)")]
    [InlineData("TRUNC(PRICE)", "trunc(price)")]
    [InlineData("TRUNC(PRICE, 1)", "trunc(price, 1)")]
    [InlineData("SYSDATE + 1", "LOCALTIMESTAMP(0) + interval '1 days'")]
    [InlineData("CREATED - 7", "created - interval '7 days'")]
    [InlineData("CREATED + 1/24", "created + (CAST(1 AS numeric) / 24) * interval '1 day'")]
    [InlineData("CREATED + QTY", "created + (qty) * interval '1 day'")]
    [InlineData("SYSDATE - CREATED", "(EXTRACT(epoch FROM (LOCALTIMESTAMP(0) - created)) / 86400)")]
    [InlineData("UPDATED - CREATED", "updated - created")]
    [InlineData("CREATED + INTERVAL '2' HOUR", "created + interval '2 hour'")]
    [InlineData("CREATED + INTERVAL '1-6' YEAR TO MONTH", "created + interval '1 years 6 months'")]
    [InlineData("CREATED + INTERVAL '1 02:30:00' DAY TO SECOND", "created + interval '1 days 02:30:00'")]
    [InlineData("QTY / 2", "CAST(qty AS numeric) / 2")]
    [InlineData("PRICE / 2", "price / 2")]
    [InlineData("MOD(QTY, 2)", "mod(qty, 2)")]
    [InlineData("TO_CHAR(CREATED, 'YYYY-MM-DD HH24:MI:SS')", "to_char(created, 'YYYY-MM-DD HH24:MI:SS')")]
    [InlineData("TO_CHAR(UPDATED, 'HH24:MI:SS.FF3')", "to_char(updated, 'HH24:MI:SS.MS')")]
    [InlineData("TO_DATE('2024-01-31', 'YYYY-MM-DD')", "CAST(to_timestamp('2024-01-31', 'YYYY-MM-DD') AS timestamp(0))")]
    [InlineData("TO_NUMBER(NOTE)", "CAST(note AS numeric)")]
    [InlineData("CAST(PRICE AS NUMBER(10,2))", "CAST(price AS numeric(10,2))")]
    [InlineData("CAST(QTY AS VARCHAR2(20 CHAR))", "CAST(qty AS varchar(20))")]
    [InlineData("CAST(UPDATED AS DATE)", "CAST(updated AS timestamp(0))")]
    [InlineData("CAST(CREATED AS TIMESTAMP WITH LOCAL TIME ZONE)", "CAST(created AS timestamp with time zone)")]
    [InlineData("DATE '2024-01-31'", "date '2024-01-31'")]
    [InlineData("TIMESTAMP '2024-01-31 10:00:00'", "timestamp '2024-01-31 10:00:00'")]
    [InlineData("TIMESTAMP '2024-01-31 10:00:00 +02:00'", "timestamptz '2024-01-31 10:00:00 +02:00'")]
    [InlineData("EXTRACT(YEAR FROM CREATED)", "EXTRACT(year FROM created)")]
    [InlineData("REGEXP_LIKE(NOTE, '^A', 'i')", "(note ~* '^A')")]
    [InlineData("REGEXP_REPLACE(NOTE, '[0-9]', '#')", "regexp_replace(note, '[0-9]', '#', 'g')")]
    [InlineData("REGEXP_REPLACE(NOTE, '[0-9]', '#', 1, 1)", "regexp_replace(note, '[0-9]', '#')")]
    [InlineData("JSON_VALUE(DOC, '$.customer.email')", "(CAST(doc AS jsonb) #>> '{customer,email}')")]
    [InlineData("JSON_VALUE(DOC, '$.qty' RETURNING NUMBER)", "CAST((CAST(doc AS jsonb) ->> 'qty') AS numeric)")]
    [InlineData("JSON_QUERY(DOC, '$.lines')", "(CAST(doc AS jsonb) -> 'lines')")]
    [InlineData("BITAND(QTY, 4)", "(qty & 4)")]
    [InlineData("\"SHOP\".\"ORDER_SEQ\".\"NEXTVAL\"", "nextval('public.order_seq')")]
    [InlineData("ORDER_SEQ.NEXTVAL", "nextval('public.order_seq')")]
    [InlineData("USER", "current_user")]
    [InlineData("SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')", "current_schema()")]
    [InlineData("DBMS_RANDOM.VALUE", "random()")]
    [InlineData("DBMS_RANDOM.VALUE(1, 10)", "(1 + random() * (10 - 1))")]
    [InlineData("CASE WHEN QTY > 10 THEN 'bulk' ELSE 'single' END", "CASE WHEN qty > 10 THEN 'bulk' ELSE 'single' END")]
    [InlineData("\"Mixed Case\" + 1", "\"mixed case\" + 1")]
    public void Expressions(string oracle, string expected) => Assert.Equal(expected, Expr(oracle));

    [Theory]
    [InlineData("1", "true")]
    [InlineData("0", "false")]
    public void A_number_default_for_a_boolean_column_becomes_true_or_false(string oracle, string expected) =>
        Assert.Equal(expected, Expr(oracle, "boolean"));

    [Fact]
    public void Kept_names_stay_upper_case_and_are_quoted()
    {
        Assert.Equal("\"PRICE\" * \"QTY\"", Expr("price * \"QTY\"", names: NameStyle.Keep));
    }

    [Theory]
    [InlineData("NOTE || CUSTOMER_NAME", "Oracle's || treats NULL as an empty string")]
    [InlineData("NVL(NOTE, '')", "'' is NULL in Oracle")]
    [InlineData("GREATEST(PRICE, QTY)", "skips NULLs")]
    [InlineData("SUBSTR(CUSTOMER_NAME, -3)", "negative start")]
    [InlineData("INSTR(CUSTOMER_NAME, 'x', 2)", "start position or occurrence")]
    [InlineData("ADD_MONTHS(CREATED, 1)", "month-end")]
    [InlineData("MONTHS_BETWEEN(SYSDATE, CREATED)", "whole months")]
    [InlineData("LAST_DAY(CREATED)", "keeps the time of day")]
    [InlineData("SYS_GUID()", "RAW(16)")]
    [InlineData("TRUNC(CREATED, 'D')", "TRUNC() to 'D' not translated")]
    [InlineData("TRUNC(NVL(PRICE, 0))", "taken as numeric")]
    [InlineData("ROWID", "ROWID has no PostgreSQL counterpart")]
    [InlineData("UTL_RAW.CAST_TO_RAW(NOTE)", "Oracle package function")]
    [InlineData("SOUNDEX(NOTE)", "soundex() was kept as written")]
    [InlineData("REGEXP_SUBSTR(NOTE, '([0-9]+)')", "first parenthesized group")]
    public void What_cannot_be_mapped_faithfully_is_flagged(string oracle, string message)
    {
        var sql = Expr(oracle);
        Assert.Contains("REVIEW trek convert", sql, StringComparison.Ordinal);
        Assert.Contains(message, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_string_inside_concatenation_stays_empty()
    {
        Assert.Equal("customer_name || '' || status", Expr("CUSTOMER_NAME || '' || STATUS"));
    }

    [Fact]
    public void An_unparsable_expression_is_kept_for_review()
    {
        var sql = Expr("XMLQUERY('/a' PASSING DOC RETURNING CONTENT) IS NULL OR @");
        Assert.StartsWith("NULL /* REVIEW trek convert: could not translate", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oracle_view_becomes_a_postgresql_view()
    {
        var result = Translate(View("BIG_ORDERS", "SELECT o.id, o.total FROM orders o WHERE o.total > 100"));
        Assert.Equal("views", result.Folder);
        Assert.Equal("public.big_orders.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from Oracle view SHOP.BIG_ORDERS. Check it before deploying.", result.Script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW public.big_orders AS\nSELECT o.id, o.total FROM public.orders AS o WHERE o.total > 100;", result.Script, StringComparison.Ordinal);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void All_views_text_alone_is_enough()
    {
        var code = new PortableCode(PortableCodeKind.View, new PortableName("SHOP", "NAMES"), "select \"CUSTOMER_NAME\" from \"SHOP\".\"ORDERS\"");
        var result = Translate(code);
        Assert.Contains("CREATE OR REPLACE VIEW public.names AS\nSELECT customer_name FROM public.orders;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_with_columns_check_option_and_read_only()
    {
        var code = new PortableCode(PortableCodeKind.View, new PortableName("SHOP", "PAID_ORDERS"),
            "CREATE OR REPLACE NOFORCE VIEW \"SHOP\".\"PAID_ORDERS\" (\"ID\", \"TOTAL\" CONSTRAINT paid_pk PRIMARY KEY (ID) RELY DISABLE NOVALIDATE) AS SELECT id, total FROM orders WHERE paid = 1 WITH CHECK OPTION");
        var result = Translate(code);
        Assert.Contains("CREATE OR REPLACE VIEW public.paid_orders (id, total) AS\nSELECT id, total FROM public.orders WHERE paid = 1\nWITH CHECK OPTION;", result.Script, StringComparison.Ordinal);

        var readOnly = Translate(View("RO", "SELECT id FROM orders WITH READ ONLY"));
        Assert.Contains("WITH READ ONLY was dropped", readOnly.Script, StringComparison.Ordinal);
        Assert.Contains("SELECT id FROM public.orders;", readOnly.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Dual_disappears_and_minus_becomes_except()
    {
        var result = Translate(View("DIFF", "SELECT SYSDATE AS now FROM DUAL UNION ALL SELECT created FROM orders MINUS SELECT created FROM orders WHERE paid = 0"));
        Assert.Contains("SELECT LOCALTIMESTAMP(0) AS now UNION ALL SELECT created FROM public.orders EXCEPT SELECT created FROM public.orders WHERE paid = 0;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Rownum_in_where_becomes_limit()
    {
        var result = Translate(View("FIRST_TEN", "SELECT * FROM (SELECT id, total FROM orders ORDER BY total DESC) WHERE ROWNUM <= 10"));
        Assert.Contains("SELECT * FROM (SELECT id, total FROM public.orders ORDER BY total DESC) AS subquery1 LIMIT 10;", result.Script, StringComparison.Ordinal);

        var less = Translate(View("FIRST_FIVE", "SELECT id FROM orders WHERE paid = 1 AND ROWNUM < 6"));
        Assert.Contains("SELECT id FROM public.orders WHERE paid = 1 LIMIT 5;", less.Script, StringComparison.Ordinal);

        var sorted = Translate(View("SORTED", "SELECT id FROM orders WHERE ROWNUM <= 3 ORDER BY id"));
        Assert.Contains("ORDER BY id LIMIT 3;", sorted.Script, StringComparison.Ordinal);
        Assert.Contains(sorted.Issues, i => i.Contains("ROWNUM before ORDER BY", StringComparison.Ordinal));
    }

    [Fact]
    public void Fetch_first_becomes_limit()
    {
        var result = Translate(View("PAGE", "SELECT id FROM orders ORDER BY created DESC NULLS LAST OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY"));
        Assert.Contains("SELECT id FROM public.orders ORDER BY created DESC NULLS LAST LIMIT 10 OFFSET 20;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Plus_outer_joins_become_left_joins()
    {
        var result = Translate(View("CUSTOMER_ORDERS", """
            SELECT c.name, o.id, o.total
              FROM customers c, orders o
             WHERE c.name = o.customer_name(+)
               AND o.status(+) = 'paid'
               AND c.email IS NOT NULL
            """));
        Assert.Contains("SELECT c.name, o.id, o.total FROM public.customers AS c LEFT JOIN public.orders AS o ON c.name = o.customer_name AND o.status = 'paid' WHERE c.email IS NOT NULL;", result.Script, StringComparison.Ordinal);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void A_plus_outer_join_that_cannot_be_rewritten_is_flagged()
    {
        var result = Translate(View("ODD_JOIN", "SELECT c.name FROM customers c, orders o WHERE c.name = customer_name(+)"));
        Assert.Contains(result.Issues, i => i.Contains("(+) outer join", StringComparison.Ordinal));
    }

    [Fact]
    public void Listagg_and_analytic_functions()
    {
        var result = Translate(View("SUMMARY", """
            SELECT customer_name,
                   LISTAGG(status, ', ') WITHIN GROUP (ORDER BY created) AS statuses,
                   COUNT(*) AS n,
                   ROW_NUMBER() OVER (PARTITION BY customer_name ORDER BY MAX(created) DESC) AS rn,
                   MEDIAN(total) AS mid
              FROM orders
             GROUP BY customer_name
            """));
        Assert.Contains("string_agg(status, ', ' ORDER BY created) AS statuses, count(*) AS n, row_number() OVER (PARTITION BY customer_name ORDER BY max(created) DESC) AS rn, percentile_cont(0.5) WITHIN GROUP (ORDER BY total) AS mid", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recursive_with_gets_the_recursive_keyword()
    {
        var result = Translate(View("NUMS", "WITH t (n) AS (SELECT 1 FROM dual UNION ALL SELECT n + 1 FROM t WHERE n < 5) SELECT n FROM t"));
        Assert.Contains("WITH RECURSIVE t(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM t WHERE n < 5) SELECT n FROM t;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hierarchical_query_is_commented_out()
    {
        var result = Translate(View("TREE", "SELECT id, LEVEL FROM orders START WITH id = 1 CONNECT BY PRIOR id = qty"));
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for PostgreSQL", result.Script, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.Contains("CONNECT BY", StringComparison.Ordinal));
    }

    [Fact]
    public void A_compound_trigger_is_written_commented_out_for_review()
    {
        var code = new PortableCode(PortableCodeKind.Trigger, new PortableName("SHOP", "AUDIT"), "CREATE OR REPLACE TRIGGER audit FOR UPDATE ON orders COMPOUND TRIGGER\n  AFTER EACH ROW IS BEGIN NULL; END AFTER EACH ROW;\nEND audit;") { Table = new PortableName("SHOP", "ORDERS") };
        var translator = new OracleToPgTranslator(Schemas.OracleToPg());
        var result = translator.Code(code, [code]);
        output.WriteLine(result.Script);
        Assert.Equal("triggers", result.Folder);
        Assert.Equal("public.audit.sql", result.FileName);
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for PostgreSQL", result.Script, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.Contains("compound trigger", StringComparison.Ordinal));
        Assert.Empty(translator.Intermediate(code, [code]));
    }

    [Fact]
    public void The_intermediate_view_has_postgresql_names_for_the_next_step()
    {
        var code = View("BIG_ORDERS", "SELECT t.id FROM orders t WHERE t.total > 100");
        var parts = new OracleToPgTranslator(Schemas.OracleToPg()).Intermediate(code, [code]);
        var view = Assert.Single(parts);
        Assert.Equal(new PortableName("public", "big_orders"), view.Name);
        Assert.Equal("CREATE VIEW public.big_orders AS\nSELECT t.id FROM public.orders AS t WHERE t.total > 100;", view.Definition);
        Assert.NotNull(new PgParser(view.Definition["CREATE VIEW public.big_orders AS\n".Length..]).QueryOnly());
    }

    [Fact]
    public void Oracle_reaches_sqlite_by_way_of_postgresql()
    {
        var ctx = new ConversionContext(Schemas.Oracle(), new ConversionOptions { TargetProvider = "sqlite" });
        var translator = ConversionProject.TranslatorFor(ctx);
        Assert.IsType<ChainTranslator>(translator);

        var check = translator.Predicate("\"QTY\" > 0", new ExpressionSite("SHOP.ORDERS"));
        output.WriteLine(check);
        Assert.Equal("QTY > 0", check);

        var value = translator.Expression("NVL(UPDATED, CREATED)", new ExpressionSite("SHOP.ORDERS"));
        output.WriteLine(value);
        Assert.Equal("coalesce(UPDATED, CREATED)", value);

        var view = View("RECENT", "SELECT o.id, o.created + 7 AS due FROM orders o WHERE ROWNUM <= 5");
        var result = translator.Code(view, [view]);
        output.WriteLine(result.Script);
        Assert.Equal("RECENT.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from Oracle view SHOP.RECENT (by way of PostgreSQL).", result.Script, StringComparison.Ordinal);
        Assert.Contains("SELECT O.ID, datetime(O.CREATED, '+7 days') AS DUE FROM ORDERS AS O LIMIT 5;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_reaches_mysql_by_way_of_postgresql()
    {
        var ctx = new ConversionContext(Schemas.Oracle(), new ConversionOptions { TargetProvider = "mysql" });
        var translator = ConversionProject.TranslatorFor(ctx);
        Assert.IsType<ChainTranslator>(translator);

        var total = translator.Expression("\"PRICE\"*\"QTY\"", new ExpressionSite("SHOP.ORDERS"));
        output.WriteLine(total);
        Assert.Equal("`PRICE` * `QTY`", total);

        var view = View("LABELS", "SELECT o.id, DECODE(o.status, 'new', 'N', 'paid', 'P', '?') AS code, NVL(o.note, '-') AS note FROM orders o");
        var result = translator.Code(view, [view]);
        output.WriteLine(result.Script);
        Assert.Equal("LABELS.sql", result.FileName);
        Assert.Contains("CREATE OR REPLACE VIEW `LABELS` AS", result.Script, StringComparison.Ordinal);
        Assert.Contains("SELECT `O`.`ID`, CASE `O`.`STATUS` WHEN 'new' THEN 'N' WHEN 'paid' THEN 'P' ELSE '?' END AS `CODE`, COALESCE(`O`.`NOTE`, '-') AS `NOTE` FROM `ORDERS` AS `O`;", result.Script, StringComparison.Ordinal);
    }
}
