using Badger.Trek.Conversion.Oracle;
using Badger.Trek.Core.Conversion;
using Xunit.Abstractions;

namespace Badger.Trek.Conversion.Tests;

/// <summary>PostgreSQL expressions and views written as Oracle 19c SQL, and SQL Server to Oracle by way of PostgreSQL.</summary>
public class PgToOracleTests(ITestOutputHelper output)
{
    private string Expr(string pg, string? targetType = null)
    {
        var result = new PgToOracleTranslator(Schemas.ToOracle()).Expression(pg, new ExpressionSite("public.orders", targetType));
        output.WriteLine(result);
        return result;
    }

    private string Predicate(string pg)
    {
        var result = new PgToOracleTranslator(Schemas.ToOracle()).Predicate(pg, new ExpressionSite("public.orders"));
        output.WriteLine(result);
        return result;
    }

    private TranslatedCode Translate(PortableCode code)
    {
        var result = new PgToOracleTranslator(Schemas.ToOracle()).Code(code, [code]);
        output.WriteLine(result.Script);
        return result;
    }

    private static PortableCode View(string name, string body) =>
        new(PortableCodeKind.View, new PortableName("public", name), $"CREATE VIEW public.{name} AS\n{body}");

    [Theory]
    [InlineData("now()", "SYSTIMESTAMP")]
    [InlineData("CURRENT_TIMESTAMP", "SYSTIMESTAMP")]
    [InlineData("LOCALTIMESTAMP", "LOCALTIMESTAMP")]
    [InlineData("CURRENT_DATE", "TRUNC(SYSDATE)")]
    [InlineData("'new'::public.order_status", "'new'")]
    [InlineData("'it''s'::text", "'it''s'")]
    [InlineData("false", "0")]
    [InlineData("true", "1")]
    [InlineData("42", "42")]
    [InlineData("(total >= (0)::numeric)", "(CASE WHEN TOTAL >= 0 THEN 1 ELSE 0 END)")]
    [InlineData("(customer_name || ' (vip)'::text)", "(CUSTOMER_NAME || ' (vip)')")]
    [InlineData("concat(customer_name, updated, '!'::text)", "(CUSTOMER_NAME || UPDATED || '!')")]
    [InlineData("COALESCE(updated, created)", "COALESCE(UPDATED, CREATED)")]
    [InlineData("NULLIF(total, (0)::numeric)", "NULLIF(TOTAL, 0)")]
    [InlineData("length(customer_name)", "LENGTH(CUSTOMER_NAME)")]
    [InlineData("\"substring\"(customer_name, 2, 3)", "SUBSTR(CUSTOMER_NAME, 2, 3)")]
    [InlineData("\"left\"(customer_name, 3)", "SUBSTR(CUSTOMER_NAME, 1, 3)")]
    [InlineData("strpos(customer_name, 'x'::text)", "INSTR(CUSTOMER_NAME, 'x')")]
    [InlineData("upper(TRIM(BOTH FROM customer_name))", "UPPER(TRIM(CUSTOMER_NAME))")]
    [InlineData("btrim(customer_name, 'xy'::text)", "LTRIM(RTRIM(CUSTOMER_NAME, 'xy'), 'xy')")]
    [InlineData("lpad((id)::text, 8, '0'::text)", "LPAD(CAST(ID AS VARCHAR2(4000)), 8, '0')")]
    [InlineData("replace(customer_name, 'a'::text, 'b'::text)", "REPLACE(CUSTOMER_NAME, 'a', 'b')")]
    [InlineData("md5(customer_name)", "LOWER(RAWTOHEX(STANDARD_HASH(CUSTOMER_NAME, 'MD5')))")]
    [InlineData("random()", "DBMS_RANDOM.VALUE")]
    [InlineData("(id % 3)", "(MOD(ID, 3))")]
    [InlineData("(2 ^ 3)", "(POWER(2, 3))")]
    [InlineData("(id / 2)", "(TRUNC(ID / 2))")]
    [InlineData("(total / (2)::numeric)", "(TOTAL / 2)")]
    [InlineData("round(total, 1)", "ROUND(TOTAL, 1)")]
    [InlineData("ceil(total)", "CEIL(TOTAL)")]
    [InlineData("log(total)", "LOG(10, TOTAL)")]
    [InlineData("(created + '1 day'::interval)", "(CREATED + INTERVAL '1' DAY)")]
    [InlineData("(created - '02:30:00'::interval)", "(CREATED - INTERVAL '2' HOUR - INTERVAL '30' MINUTE)")]
    [InlineData("(created + '200 days'::interval)", "(CREATED + NUMTODSINTERVAL(200, 'DAY'))")]
    [InlineData("(created + ((id)::double precision * '1 day'::interval))", "(CREATED + NUMTODSINTERVAL((CAST(ID AS BINARY_DOUBLE)), 'DAY'))")]
    [InlineData("'1 day'::interval", "INTERVAL '1' DAY")]
    [InlineData("EXTRACT(year FROM created)", "EXTRACT(YEAR FROM CAST((CREATED AT LOCAL) AS TIMESTAMP))")]
    [InlineData("EXTRACT(month FROM '2024-01-31'::date)", "EXTRACT(MONTH FROM DATE '2024-01-31')")]
    [InlineData("EXTRACT(minute FROM '2024-01-31'::date)", "EXTRACT(MINUTE FROM CAST(DATE '2024-01-31' AS TIMESTAMP))")]
    [InlineData("EXTRACT(hour FROM created)", "EXTRACT(HOUR FROM CAST((CREATED AT LOCAL) AS TIMESTAMP))")]
    [InlineData("EXTRACT(dow FROM created)", "MOD(TRUNC(CAST((CREATED AT LOCAL) AS TIMESTAMP)) - TRUNC(CAST((CREATED AT LOCAL) AS TIMESTAMP), 'IW') + 1, 7)")]
    [InlineData("EXTRACT(doy FROM created)", "TO_NUMBER(TO_CHAR(CAST((CREATED AT LOCAL) AS TIMESTAMP), 'DDD'))")]
    [InlineData("date_part('quarter'::text, created)", "TO_NUMBER(TO_CHAR(CAST((CREATED AT LOCAL) AS TIMESTAMP), 'Q'))")]
    [InlineData("date_trunc('month'::text, created)", "TRUNC(CREATED, 'MM')")]
    [InlineData("date_trunc('week'::text, created)", "TRUNC(CREATED, 'IW')")]
    [InlineData("to_char(created, 'YYYY-MM-DD HH24:MI:SS.MS'::text)", "TO_CHAR(CREATED, 'YYYY-MM-DD HH24:MI:SS.FF3')")]
    [InlineData("to_date('2024-01-31'::text, 'YYYY-MM-DD'::text)", "TO_DATE('2024-01-31', 'YYYY-MM-DD')")]
    [InlineData("(id)::text", "CAST(ID AS VARCHAR2(4000))")]
    [InlineData("(status)::text", "STATUS")]
    [InlineData("(customer_name)::character varying(20)", "CUSTOMER_NAME")]
    [InlineData("(total)::integer", "CAST(TOTAL AS NUMBER(10))")]
    [InlineData("(total)::bigint", "CAST(TOTAL AS NUMBER(19))")]
    [InlineData("(id)::numeric(10,2)", "CAST(ID AS NUMBER(10,2))")]
    [InlineData("(created)::date", "TRUNC(CREATED)")]
    [InlineData("'2024-01-31'::date", "DATE '2024-01-31'")]
    [InlineData("'2024-01-31 10:00:00'::timestamp without time zone", "TIMESTAMP '2024-01-31 10:00:00'")]
    [InlineData("'2024-01-31 10:00:00+00'::timestamp with time zone", "TIMESTAMP '2024-01-31 10:00:00+00:00'")]
    [InlineData("(created)::timestamp(3) without time zone", "CAST(CREATED AS TIMESTAMP(3))")]
    [InlineData("(customer_name ~~ 'a%'::text)", "(CASE WHEN CUSTOMER_NAME LIKE 'a%' THEN 1 ELSE 0 END)")]
    [InlineData("(updated IS NULL)", "(CASE WHEN UPDATED IS NULL THEN 1 ELSE 0 END)")]
    [InlineData("(doc ->> 'name'::text)", "(JSON_VALUE(DOC, '$.name'))")]
    [InlineData("((doc -> 'customer'::text) ->> 'email'::text)", "(JSON_VALUE(DOC, '$.customer.email'))")]
    [InlineData("(doc #>> '{lines,0,sku}'::text[])", "(JSON_VALUE(DOC, '$.lines[0].sku'))")]
    [InlineData("CASE WHEN paid THEN 'y'::text ELSE 'n'::text END", "CASE WHEN PAID = 1 THEN 'y' ELSE 'n' END")]
    [InlineData("CASE status WHEN 'new'::public.order_status THEN 1 ELSE 2 END", "CASE STATUS WHEN 'new' THEN 1 ELSE 2 END")]
    [InlineData("nextval('order_seq'::regclass)", "ORDER_SEQ.NEXTVAL")]
    [InlineData("CURRENT_USER", "USER")]
    [InlineData("json_build_object('a', id, 'b', total)", "JSON_OBJECT(KEY 'a' VALUE ID, KEY 'b' VALUE TOTAL)")]
    [InlineData("'\\x0a0b'::bytea", "HEXTORAW('0a0b')")]
    [InlineData("(id)::boolean", "SIGN(ABS(ID))")]
    [InlineData("'t'::boolean", "1")]
    public void Expressions(string pg, string expected) => Assert.Equal(expected, Expr(pg));

    [Theory]
    [InlineData("(total >= (0)::numeric)", "(TOTAL >= 0)")]
    [InlineData("paid", "PAID = 1")]
    [InlineData("(NOT paid)", "(NOT (PAID = 1))")]
    [InlineData("(paid = true)", "(PAID = 1)")]
    [InlineData("(paid IS TRUE)", "(PAID = 1)")]
    [InlineData("(paid IS NOT TRUE)", "(NVL(PAID, 0) <> 1)")]
    [InlineData("((total > (0)::numeric) IS NOT FALSE)", "(CASE WHEN NOT ((TOTAL > 0)) THEN 1 ELSE 0 END = 0)")]
    [InlineData("(paid AND (total > (100)::numeric))", "(PAID = 1 AND (TOTAL > 100))")]
    [InlineData("((status)::text = ANY ((ARRAY['new'::character varying, 'paid'::character varying])::text[]))", "(STATUS IN ('new', 'paid'))")]
    [InlineData("((status)::text = ANY (ARRAY['new'::text, 'paid'::text]))", "(STATUS IN ('new', 'paid'))")]
    [InlineData("(updated IS DISTINCT FROM created)", "(DECODE(UPDATED, CREATED, 0, 1) = 1)")]
    [InlineData("(updated IS NOT DISTINCT FROM created)", "(DECODE(UPDATED, CREATED, 1, 0) = 1)")]
    [InlineData("(customer_name ~~* 'a%'::text)", "(UPPER(CUSTOMER_NAME) LIKE UPPER('a%'))")]
    [InlineData("(customer_name ~~ 'a\\_%'::text)", "(CUSTOMER_NAME LIKE 'a\\_%' ESCAPE '\\')")]
    [InlineData("(customer_name ~ '^A'::text)", "(REGEXP_LIKE(CUSTOMER_NAME, '^A'))")]
    [InlineData("(customer_name !~* '^a'::text)", "(NOT REGEXP_LIKE(CUSTOMER_NAME, '^a', 'i'))")]
    [InlineData("(total BETWEEN (1)::numeric AND (10)::numeric)", "(TOTAL BETWEEN 1 AND 10)")]
    [InlineData("true", "1 = 1")]
    public void Predicates(string pg, string expected) => Assert.Equal(expected, Predicate(pg));

    [Fact]
    public void A_boolean_condition_used_as_a_value_keeps_null_when_it_can_be_unknown()
    {
        // updated is nullable: NULL > x is unknown in PostgreSQL, and stays NULL rather than becoming 0.
        Assert.Equal("(CASE WHEN UPDATED > CREATED THEN 1 WHEN NOT (UPDATED > CREATED) THEN 0 END)", Expr("(updated > created)"));
    }

    [Theory]
    [InlineData("varchar2(36)", "LOWER(REGEXP_REPLACE(RAWTOHEX(SYS_GUID()), '(.{8})(.{4})(.{4})(.{4})(.{12})', '\\1-\\2-\\3-\\4-\\5'))")]
    [InlineData("raw(16)", "SYS_GUID()")]
    public void Gen_random_uuid_follows_the_column_type(string type, string expected) =>
        Assert.StartsWith(expected, Expr("gen_random_uuid()", type), StringComparison.Ordinal);

    [Theory]
    [InlineData("(created + '1 mon'::interval)", "ADD_MONTHS")]
    [InlineData("''::text", "Oracle stores '' as NULL")]
    [InlineData("(customer_name || updated)", "Oracle's || treats NULL as an empty string")]
    [InlineData("concat_ws(', '::text, customer_name, status)", "leaves out NULL arguments")]
    [InlineData("greatest(total, (1)::numeric)", "NULL when any argument is NULL")]
    [InlineData("(doc -> 'lines'::text)", "JSON_QUERY")]
    [InlineData("string_agg(customer_name, ', '::text)", "4000 bytes")]
    [InlineData("tags[1]", "expression not translated")]
    [InlineData("ARRAY[1, 2]", "Oracle has no arrays")]
    [InlineData("(customer_name SIMILAR TO 'a%'::text)", "SIMILAR has no Oracle counterpart")]
    [InlineData("CURRENT_TIME", "Oracle has no time type")]
    [InlineData("EXTRACT(epoch FROM created)", "epoch is computed")]
    [InlineData("\"substring\"(customer_name, 0, 3)", "start position of 0")]
    [InlineData("age(created)", "age() was kept as written")]
    public void What_cannot_be_mapped_faithfully_is_flagged(string pg, string message)
    {
        var sql = Expr(pg);
        Assert.Contains("REVIEW trek convert", sql, StringComparison.Ordinal);
        Assert.Contains(message, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Concatenating_columns_that_are_never_null_is_not_flagged()
    {
        Assert.Equal("(CUSTOMER_NAME || ' ' || STATUS)", Expr("((customer_name || ' '::text) || (status)::text)"));
    }

    [Fact]
    public void An_unparsable_expression_is_kept_for_review()
    {
        var sql = Expr("total @@@ 1");
        Assert.StartsWith("NULL /* REVIEW trek convert: could not translate", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_becomes_an_oracle_view()
    {
        var result = Translate(View("big_orders", " SELECT o.id,\n    o.total\n   FROM orders o\n  WHERE (o.total > (100)::numeric);"));
        Assert.Equal("views", result.Folder);
        Assert.Equal("BIG_ORDERS.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from PostgreSQL view public.big_orders. Check it before deploying.", result.Script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW BIG_ORDERS AS\nSELECT O.ID, O.TOTAL FROM ORDERS O WHERE (O.TOTAL > 100);", result.Script, StringComparison.Ordinal);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Table_aliases_take_no_as_but_column_aliases_do()
    {
        var result = Translate(View("labels", " SELECT o.id AS order_id,\n    (o.customer_name || ' '::text) AS label\n   FROM (orders o\n     JOIN orders p ON ((p.id = o.id)));"));
        Assert.Contains("SELECT O.ID AS ORDER_ID, (O.CUSTOMER_NAME || ' ') AS LABEL FROM ORDERS O JOIN ORDERS P ON (P.ID = O.ID);", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_select_without_from_reads_from_dual()
    {
        var result = Translate(View("today", " SELECT CURRENT_DATE AS today,\n    true AS yes;"));
        Assert.Contains("SELECT TRUNC(SYSDATE) AS TODAY, 1 AS YES FROM DUAL;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Limit_and_offset_become_offset_fetch()
    {
        var result = Translate(View("page", " SELECT o.id\n   FROM orders o\n  ORDER BY o.created DESC NULLS LAST, o.id\n OFFSET 20\n LIMIT 10;"));
        Assert.Contains("SELECT O.ID FROM ORDERS O ORDER BY O.CREATED DESC NULLS LAST, O.ID OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY;", result.Script, StringComparison.Ordinal);

        var first = Translate(View("top", " SELECT o.id\n   FROM orders o\n  ORDER BY o.id\n LIMIT 5;"));
        Assert.Contains("ORDER BY O.ID FETCH FIRST 5 ROWS ONLY;", first.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Booleans_in_the_select_list_become_numbers_and_conditions_compare_to_one()
    {
        var result = Translate(View("flags", " SELECT o.id,\n    o.paid,\n    (o.total > (100)::numeric) AS big,\n    (NOT o.paid) AS unpaid\n   FROM orders o\n  WHERE (o.paid AND (o.status <> 'shipped'::public.order_status));"));
        Assert.Contains("SELECT O.ID, O.PAID, (CASE WHEN O.TOTAL > 100 THEN 1 ELSE 0 END) AS BIG, (CASE WHEN NOT (O.PAID = 1) THEN 1 ELSE 0 END) AS UNPAID FROM ORDERS O WHERE (O.PAID = 1 AND (O.STATUS <> 'shipped'));", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregates_with_filter_and_string_agg()
    {
        var result = Translate(View("summary", """
             SELECT o.customer_name,
                count(*) FILTER (WHERE o.paid) AS paid_orders,
                sum(o.total) FILTER (WHERE (o.total > (0)::numeric)) AS positive,
                string_agg((o.status)::text, ', '::text ORDER BY (o.status)::text) AS statuses,
                bool_or(o.paid) AS any_paid
               FROM orders o
              GROUP BY o.customer_name
             HAVING (count(*) > 1);
            """));
        Assert.Contains("COUNT(CASE WHEN O.PAID = 1 THEN 1 END) AS PAID_ORDERS", result.Script, StringComparison.Ordinal);
        Assert.Contains("SUM(CASE WHEN (O.TOTAL > 0) THEN O.TOTAL END) AS POSITIVE", result.Script, StringComparison.Ordinal);
        Assert.Contains("LISTAGG(O.STATUS, ', ') WITHIN GROUP (ORDER BY O.STATUS)", result.Script, StringComparison.Ordinal);
        Assert.Contains("MAX(O.PAID) AS ANY_PAID", result.Script, StringComparison.Ordinal);
        Assert.Contains("GROUP BY O.CUSTOMER_NAME HAVING (COUNT(*) > 1);", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Except_becomes_minus_and_values_become_dual_rows()
    {
        var result = Translate(View("diff", " SELECT orders.id\n   FROM orders\nEXCEPT\n SELECT v.id\n   FROM ( VALUES (1,'a'::text), (2,'b'::text)) v(id, name);"));
        Assert.Contains("SELECT ORDERS.ID FROM ORDERS MINUS SELECT V.ID FROM (SELECT 1 AS ID, 'a' AS NAME FROM DUAL UNION ALL SELECT 2 AS ID, 'b' AS NAME FROM DUAL) V;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_series_becomes_connect_by_level()
    {
        var result = Translate(View("numbers", " SELECT n.n\n   FROM generate_series(1, 10) n(n);"));
        Assert.Contains("SELECT N.N FROM (SELECT 1 + (LEVEL - 1) AS N FROM DUAL CONNECT BY LEVEL <= 10 - 1 + 1) N;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Derived_table_column_aliases_move_to_the_select_list()
    {
        var result = Translate(View("renamed", " SELECT t.a\n   FROM ( SELECT orders.id,\n            orders.total\n           FROM orders) t(a, b);"));
        Assert.Contains("SELECT T.A FROM (SELECT ORDERS.ID AS A, ORDERS.TOTAL AS B FROM ORDERS) T;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recursive_cte_loses_the_recursive_keyword()
    {
        var result = Translate(View("tree", " WITH RECURSIVE t(n) AS (\n         SELECT 1\n        UNION ALL\n         SELECT (t_1.n + 1)\n           FROM t t_1\n          WHERE (t_1.n < 5)\n        )\n SELECT t.n\n   FROM t;"));
        Assert.Contains("WITH T(N) AS (SELECT 1 FROM DUAL UNION ALL SELECT (T_1.N + 1) FROM T T_1 WHERE (T_1.N < 5)) SELECT T.N FROM T;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_with_a_column_list_and_check_option()
    {
        var code = new PortableCode(PortableCodeKind.View, new PortableName("public", "Paid Orders"),
            "CREATE VIEW public.\"Paid Orders\" (id, total) AS\n SELECT orders.id,\n    orders.total\n   FROM orders\n  WHERE orders.paid\n  WITH CASCADED CHECK OPTION;");
        var result = Translate(code);
        Assert.Equal("PAID ORDERS.sql", result.FileName);
        Assert.Contains("CREATE OR REPLACE VIEW \"PAID ORDERS\" (ID, TOTAL) AS\nSELECT ORDERS.ID, ORDERS.TOTAL FROM ORDERS WHERE ORDERS.PAID = 1\nWITH CHECK OPTION;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Reserved_words_are_quoted()
    {
        var result = Translate(View("reserved", " SELECT o.id AS \"number\",\n    o.created AS date\n   FROM orders o;"));
        Assert.Contains("SELECT O.ID AS \"NUMBER\", O.CREATED AS \"DATE\" FROM ORDERS O;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparsable_view_is_commented_out()
    {
        var result = Translate(View("odd", " SELECT o.id\n   FROM orders o\n  WINDOW w AS (ORDER BY o.id);"));
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for Oracle", result.Script, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.StartsWith("could not parse", StringComparison.Ordinal));
    }

    [Fact]
    public void A_routine_in_another_language_is_written_commented_out_for_review()
    {
        var code = new PortableCode(PortableCodeKind.Function, new PortableName("public", "touch"), "CREATE OR REPLACE FUNCTION public.touch()\n RETURNS void\n LANGUAGE plpython3u\nAS $function$\nplpy.execute('UPDATE orders SET updated = now()')\n$function$");
        var result = Translate(code);
        Assert.Equal("functions", result.Folder);
        Assert.Equal("TOUCH.sql", result.FileName);
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for Oracle", result.Script, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.Contains("LANGUAGE plpython3u", StringComparison.Ordinal));
    }

    [Fact]
    public void Objects_outside_the_default_schema_get_a_prefix()
    {
        var schema = Schemas.Postgres();
        schema.Schemas.Add("sales");
        var ctx = new ConversionContext(schema, new ConversionOptions { TargetProvider = "oracle" });
        var code = new PortableCode(PortableCodeKind.View, new PortableName("sales", "recent"), "CREATE VIEW sales.recent AS\n SELECT o.id\n   FROM sales.orders o;");
        var result = new PgToOracleTranslator(ctx).Code(code, [code]);
        output.WriteLine(result.Script);
        Assert.Equal("SALES_RECENT.sql", result.FileName);
        Assert.Contains("CREATE OR REPLACE VIEW SALES_RECENT AS\nSELECT O.ID FROM SALES_ORDERS O;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_server_reaches_oracle_by_way_of_postgresql()
    {
        var ctx = new ConversionContext(Schemas.SqlServer(), new ConversionOptions { TargetProvider = "oracle" });
        var translator = ConversionProject.TranslatorFor(ctx);
        Assert.IsType<ChainTranslator>(translator);

        var check = translator.Predicate("([Balance]>=(0))", new ExpressionSite("dbo.Customer"));
        output.WriteLine(check);
        Assert.Equal("BALANCE >= 0", check);

        var created = translator.Expression("(sysdatetime())", new ExpressionSite("dbo.Customer"));
        output.WriteLine(created);
        Assert.Equal("LOCALTIMESTAMP", created);

        var active = translator.Expression("((1))", new ExpressionSite("dbo.Customer", "NUMBER(1)"));
        output.WriteLine(active);
        Assert.Equal("1", active);

        var view = new PortableCode(PortableCodeKind.View, new PortableName("dbo", "ActiveCustomers"),
            "CREATE VIEW dbo.ActiveCustomers AS SELECT TOP (10) c.CustomerId, c.FirstName + N' ' + c.LastName AS FullName, CAST(c.Created AS date) AS CreatedOn FROM dbo.Customer AS c WHERE c.IsActive = 1 ORDER BY c.LastName");
        var result = translator.Code(view, [view]);
        output.WriteLine(result.Script);
        Assert.Equal("ACTIVECUSTOMERS.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from SQL Server view dbo.ActiveCustomers (by way of PostgreSQL).", result.Script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW ACTIVECUSTOMERS AS", result.Script, StringComparison.Ordinal);
        Assert.Contains("FROM CUSTOMER C WHERE C.ISACTIVE = 1 ORDER BY C.LASTNAME FETCH FIRST 10 ROWS ONLY;", result.Script, StringComparison.Ordinal);
    }
}
