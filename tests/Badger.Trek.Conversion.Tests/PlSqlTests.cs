using Badger.Trek.Conversion.Oracle;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;
using Xunit.Abstractions;

namespace Badger.Trek.Conversion.Tests;

/// <summary>PL/pgSQL functions, procedures and triggers written as PL/SQL, and PL/SQL written as PL/pgSQL.</summary>
public class PlSqlTests(ITestOutputHelper output)
{
    private TranslatedCode ToOracle(PortableCode code, params PortableCode[] others)
    {
        var result = new PgToOracleTranslator(Schemas.ToOracle()).Code(code, [code, .. others]);
        output.WriteLine(result.Script);
        return result;
    }

    private TranslatedCode ToPg(PortableCode code)
    {
        var result = new OracleToPgTranslator(Schemas.OracleToPg()).Code(code, [code]);
        output.WriteLine(result.Script);
        return result;
    }

    private static PortableCode Pg(PortableCodeKind kind, string name, string definition) => new(kind, new PortableName("public", name), definition) { Language = "plpgsql" };

    private static PortableCode Ora(PortableCodeKind kind, string name, string definition) => new(kind, new PortableName("SHOP", name), definition) { Language = "plsql" };

    private static void Lines(string script, params string[] expected)
    {
        var lines = script.Split('\n').Select(l => l.Trim()).ToList();
        foreach (var line in expected)
        {
            Assert.Contains(line, lines);
        }
    }

    // ------------------------------------------------------------------ PostgreSQL to Oracle

    [Fact]
    public void A_plpgsql_function_becomes_a_plsql_function()
    {
        var result = ToOracle(Pg(PortableCodeKind.Function, "order_count", """
            CREATE OR REPLACE FUNCTION public.order_count(p_customer text)
             RETURNS integer
             LANGUAGE plpgsql
             STABLE
            AS $function$
            DECLARE
              n integer;
            BEGIN
              SELECT count(*) INTO n FROM public.orders WHERE customer_name = p_customer;
              IF n IS NULL THEN
                RETURN 0;
              END IF;
              RETURN n;
            END;
            $function$
            """));
        Assert.Equal("functions", result.Folder);
        Assert.Equal("ORDER_COUNT.sql", result.FileName);
        Assert.Empty(result.Issues);
        Assert.Contains("CREATE OR REPLACE FUNCTION ORDER_COUNT(\n    P_CUSTOMER IN VARCHAR2)\nRETURN NUMBER\nIS\n    N NUMBER(10);\nBEGIN\n", result.Script, StringComparison.Ordinal);
        Lines(result.Script, "SELECT COUNT(*) INTO N FROM ORDERS WHERE CUSTOMER_NAME = P_CUSTOMER;", "IF N IS NULL THEN", "RETURN 0;", "END IF;", "RETURN N;", "END;", "/");
    }

    [Fact]
    public void Plpgsql_statements_become_their_plsql_forms()
    {
        var result = ToOracle(Pg(PortableCodeKind.Function, "mark_paid", """
            CREATE OR REPLACE FUNCTION public.mark_paid(p_order bigint)
             RETURNS integer
             LANGUAGE plpgsql
            AS $function$
            DECLARE
              v_rows integer;
              v_total numeric;
              ok boolean := false;
            BEGIN
              UPDATE public.orders SET status = 'paid', paid = true WHERE id = p_order AND status = 'new';
              GET DIAGNOSTICS v_rows = ROW_COUNT;
              IF v_rows = 0 THEN
                RAISE EXCEPTION 'order % is not open (%%)', p_order;
              END IF;
              SELECT total INTO v_total FROM public.orders WHERE id = p_order;
              IF NOT FOUND THEN
                RAISE NOTICE 'gone';
              END IF;
              ok := v_total > 100;
              IF ok THEN
                PERFORM public.order_count('x');
              END IF;
              IF EXISTS (SELECT 1 FROM public.orders WHERE total < 0) THEN
                RAISE unique_violation;
              END IF;
              v_total := (SELECT max(total) FROM public.orders);
              FOR i IN REVERSE 10..1 LOOP
                EXIT WHEN i < 5;
              END LOOP;
              CASE v_rows WHEN 1 THEN NULL; ELSE RAISE NOTICE 'many'; END CASE;
              RETURN v_rows;
            EXCEPTION
              WHEN unique_violation THEN
                RETURN -1;
              WHEN others THEN
                RAISE;
            END;
            $function$
            """));
        Lines(result.Script,
            "OK BOOLEAN := FALSE;",
            "TREK_FOUND BOOLEAN := FALSE;",
            "UPDATE ORDERS SET STATUS = 'paid', PAID = 1 WHERE ID = P_ORDER AND STATUS = 'new';",
            "TREK_FOUND := SQL%FOUND;",
            "V_ROWS := SQL%ROWCOUNT;",
            "RAISE_APPLICATION_ERROR(-20000, 'order ' || P_ORDER || ' is not open (%)');",
            "WHEN NO_DATA_FOUND THEN",
            "V_TOTAL := NULL;",
            "IF NOT (TREK_FOUND) THEN",
            "DBMS_OUTPUT.PUT_LINE('gone');",
            "OK := V_TOTAL > 100;",
            "IF OK THEN",
            "TREK_DISCARD := ORDER_COUNT('x');",
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM ORDERS WHERE TOTAL < 0) THEN 1 ELSE 0 END INTO TREK_COND1 FROM DUAL;",
            "IF TREK_COND1 = 1 THEN",
            "RAISE DUP_VAL_ON_INDEX;",
            "SELECT (SELECT MAX(TOTAL) FROM ORDERS) INTO V_TOTAL FROM DUAL;",
            "FOR I IN REVERSE 1..10 LOOP",
            "EXIT WHEN I < 5;",
            "CASE V_ROWS",
            "END CASE;",
            "WHEN DUP_VAL_ON_INDEX THEN",
            "WHEN OTHERS THEN",
            "RAISE;");
    }

    [Fact]
    public void A_procedure_loops_over_a_query_and_commits()
    {
        var result = ToOracle(Pg(PortableCodeKind.Procedure, "ship_all", """
            CREATE OR REPLACE PROCEDURE public.ship_all()
             LANGUAGE plpgsql
            AS $procedure$
            DECLARE
              r record;
            BEGIN
              FOR r IN SELECT id FROM public.orders WHERE status = 'paid' LOOP
                UPDATE public.orders SET status = 'shipped' WHERE id = r.id;
              END LOOP;
              COMMIT;
            END;
            $procedure$
            """));
        Assert.Equal("procedures", result.Folder);
        Assert.Contains("CREATE OR REPLACE PROCEDURE SHIP_ALL\nIS\nBEGIN\n", result.Script, StringComparison.Ordinal);
        Lines(result.Script, "FOR R IN (SELECT ID FROM ORDERS WHERE STATUS = 'paid') LOOP", "UPDATE ORDERS SET STATUS = 'shipped' WHERE ID = R.ID;", "END LOOP;", "COMMIT;");
    }

    [Fact]
    public void A_sql_function_returns_its_query()
    {
        var single = ToOracle(Pg(PortableCodeKind.Function, "label", """
            CREATE OR REPLACE FUNCTION public.label(p_name text) RETURNS text LANGUAGE sql IMMUTABLE AS $function$
              SELECT upper(p_name)
            $function$
            """));
        Lines(single.Script, "RETURN UPPER(P_NAME);");

        var query = ToOracle(Pg(PortableCodeKind.Function, "biggest", """
            CREATE OR REPLACE FUNCTION public.biggest() RETURNS numeric LANGUAGE sql STABLE AS $function$
              SELECT total FROM public.orders ORDER BY total DESC LIMIT 1
            $function$
            """));
        Assert.Contains("CREATE OR REPLACE FUNCTION BIGGEST\nRETURN NUMBER\nIS\n    TREK_RESULT NUMBER;\n", query.Script, StringComparison.Ordinal);
        Lines(query.Script, "SELECT TOTAL INTO TREK_RESULT FROM ORDERS ORDER BY TOTAL DESC FETCH FIRST 1 ROWS ONLY;", "TREK_RESULT := NULL;", "RETURN TREK_RESULT;");
    }

    [Fact]
    public void A_set_returning_function_is_left_for_review()
    {
        var result = ToOracle(Pg(PortableCodeKind.Function, "since", """
            CREATE OR REPLACE FUNCTION public.since(t timestamp with time zone) RETURNS TABLE(id bigint) LANGUAGE sql STABLE AS $function$
              SELECT id FROM public.orders WHERE created >= t
            $function$
            """));
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for Oracle", result.Script, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.Contains("pipelined", StringComparison.Ordinal));
    }

    [Fact]
    public void A_function_with_out_parameters_becomes_a_procedure()
    {
        var result = ToOracle(Pg(PortableCodeKind.Function, "split", """
            CREATE OR REPLACE FUNCTION public.split(p_total numeric, OUT net numeric, OUT tax numeric) RETURNS record LANGUAGE plpgsql AS $function$
            BEGIN
              net := p_total / 1.2;
              tax := p_total - net;
            END;
            $function$
            """));
        Assert.Equal("procedures", result.Folder);
        Assert.Contains("CREATE OR REPLACE PROCEDURE SPLIT(\n    P_TOTAL IN NUMBER,\n    NET OUT NUMBER,\n    TAX OUT NUMBER)\nIS\n", result.Script, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.Contains("became a procedure", StringComparison.Ordinal));
    }

    [Fact]
    public void A_trigger_and_its_function_become_one_oracle_trigger()
    {
        var touch = Pg(PortableCodeKind.Function, "touch", """
            CREATE OR REPLACE FUNCTION public.touch()
             RETURNS trigger
             LANGUAGE plpgsql
            AS $function$
            BEGIN
              IF TG_OP = 'UPDATE' AND NEW.total <> OLD.total THEN
                NEW.updated := now();
              END IF;
              RETURN NEW;
            END;
            $function$
            """);
        var trigger = new PortableCode(PortableCodeKind.Trigger, new PortableName("public", "orders_touch"),
            "CREATE TRIGGER orders_touch BEFORE INSERT OR UPDATE ON public.orders FOR EACH ROW WHEN ((new.total > (0)::numeric)) EXECUTE FUNCTION touch()")
        { Table = new PortableName("public", "orders"), TriggerFunction = new PortableName("public", "touch") };

        var result = ToOracle(trigger, touch);
        Assert.Equal("triggers", result.Folder);
        Assert.Contains("CREATE OR REPLACE TRIGGER ORDERS_TOUCH\nBEFORE INSERT OR UPDATE ON ORDERS\nFOR EACH ROW\nWHEN ((NEW.TOTAL > 0))\nBEGIN\n", result.Script, StringComparison.Ordinal);
        Lines(result.Script, "IF (CASE WHEN INSERTING THEN 'INSERT' WHEN UPDATING THEN 'UPDATE' ELSE 'DELETE' END) = 'UPDATE' AND :NEW.TOTAL <> :OLD.TOTAL THEN", ":NEW.UPDATED := SYSTIMESTAMP;", "END;", "/");
        Assert.DoesNotContain("RETURN", result.Script, StringComparison.Ordinal);

        // The function itself is written into the trigger, not on its own.
        Assert.Equal(string.Empty, ToOracle(touch, trigger).Script);
    }

    [Fact]
    public void A_statement_trigger_over_a_transition_table_becomes_a_row_trigger()
    {
        var log = Pg(PortableCodeKind.Function, "log_new", """
            CREATE OR REPLACE FUNCTION public.log_new() RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN
              INSERT INTO public.orders (customer_name, total) SELECT n.customer_name, n.total FROM new_rows n;
              RETURN NULL;
            END;
            $function$
            """);
        var created = new PortableCode(PortableCodeKind.Trigger, new PortableName("public", "orders_created"),
            "CREATE TRIGGER orders_created AFTER INSERT ON public.orders REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION log_new()")
        { Table = new PortableName("public", "orders"), TriggerFunction = new PortableName("public", "log_new") };

        var result = ToOracle(created, log);
        Assert.Contains("AFTER INSERT ON ORDERS\nFOR EACH ROW\nBEGIN\n", result.Script, StringComparison.Ordinal);
        Assert.Contains("FROM (SELECT :NEW.ID AS ID, :NEW.CUSTOMER_NAME AS CUSTOMER_NAME,", result.Script, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.Contains("became a row-level trigger", StringComparison.Ordinal));
    }

    [Fact]
    public void Sql_server_procedures_reach_oracle_by_way_of_postgresql()
    {
        var ctx = new ConversionContext(Schemas.SqlServer(), new ConversionOptions { TargetProvider = "oracle" });
        var place = new PortableCode(PortableCodeKind.Procedure, new PortableName("dbo", "PlaceOrder"), """
            CREATE PROCEDURE dbo.PlaceOrder @CustomerId int, @Total decimal(12,2), @Rows bigint OUTPUT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT EXISTS (SELECT 1 FROM dbo.Customer WHERE CustomerId = @CustomerId AND IsActive = 1)
                    THROW 50001, N'Unknown or inactive customer', 1;
                UPDATE dbo.Customer SET Balance = Balance + @Total WHERE CustomerId = @CustomerId;
                SET @Rows = @@ROWCOUNT;
            END
            """);
        var result = ConversionProject.TranslatorFor(ctx).Code(place, [place]);
        output.WriteLine(result.Script);
        Assert.Equal("procedures", result.Folder);
        Assert.StartsWith("-- Converted by trek convert from SQL Server procedure dbo.PlaceOrder (by way of PostgreSQL).", result.Script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE PROCEDURE PLACEORDER(", result.Script, StringComparison.Ordinal);
        Assert.Contains("IN OUT NUMBER)", result.Script, StringComparison.Ordinal);
        Assert.Contains("RAISE_APPLICATION_ERROR(-20000, 'Unknown or inactive customer');", result.Script, StringComparison.Ordinal);
        Assert.Contains(":= SQL%ROWCOUNT;", result.Script, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Oracle to PostgreSQL

    [Fact]
    public void A_plsql_procedure_becomes_a_plpgsql_procedure()
    {
        var result = ToPg(Ora(PortableCodeKind.Procedure, "CLOSE_ORDER", """
            CREATE OR REPLACE PROCEDURE close_order(p_id NUMBER) AS
            BEGIN
              UPDATE orders SET status = 'closed' WHERE id = p_id;
            END close_order;
            """));
        Assert.Equal("procedures", result.Folder);
        Assert.Equal("public.close_order.sql", result.FileName);
        Assert.Empty(result.Issues);
        Assert.Contains("CREATE OR REPLACE PROCEDURE public.close_order(p_id numeric)\nLANGUAGE plpgsql\nAS $$\nBEGIN\n    UPDATE public.orders SET status = 'closed' WHERE id = p_id;\nEND;\n$$;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Plsql_statements_become_their_plpgsql_forms()
    {
        var result = ToPg(Ora(PortableCodeKind.Function, "ORDER_TOTAL", """
            CREATE OR REPLACE FUNCTION order_total(p_id IN orders.id%TYPE, p_tax NUMBER DEFAULT 0.2) RETURN NUMBER
            IS
              v_total orders.price%TYPE;
              v_count PLS_INTEGER := 0;
              not_paid EXCEPTION;
              PRAGMA EXCEPTION_INIT(not_paid, -20001);
              CURSOR c_lines IS SELECT qty, price FROM orders WHERE id = p_id;
            BEGIN
              SELECT price * qty INTO v_total FROM orders WHERE id = p_id;
              FOR r IN (SELECT id FROM orders WHERE paid = 0) LOOP
                v_count := v_count + 1;
                DBMS_OUTPUT.PUT_LINE('order ' || r.id);
              END LOOP;
              FOR l IN c_lines LOOP
                v_count := v_count + l.qty;
              END LOOP;
              UPDATE orders SET note = 'seen' WHERE id = p_id;
              IF SQL%ROWCOUNT = 0 THEN
                RAISE_APPLICATION_ERROR(-20001, 'order missing');
              END IF;
              CASE WHEN v_count > 10 THEN v_count := 10; ELSE NULL; END CASE;
              FOR i IN REVERSE 1..3 LOOP
                NULL;
              END LOOP;
              close_order(p_id);
              RETURN v_total * (1 + p_tax);
            EXCEPTION
              WHEN NO_DATA_FOUND THEN
                RETURN NULL;
              WHEN not_paid THEN
                RAISE;
            END order_total;
            """));
        Assert.Contains("CREATE OR REPLACE FUNCTION public.order_total(p_id public.orders.id%TYPE, p_tax numeric DEFAULT 0.2) RETURNS numeric\nLANGUAGE plpgsql\nAS $$\nDECLARE\n", result.Script, StringComparison.Ordinal);
        Lines(result.Script,
            "v_total public.orders.price%TYPE;",
            "v_count integer := 0;",
            "c_lines CURSOR FOR SELECT qty, price FROM public.orders WHERE id = p_id;",
            "r record;",
            "trek_sql_rowcount bigint := 0;",
            "SELECT price * qty INTO STRICT v_total FROM public.orders WHERE id = p_id;",
            "FOR r IN SELECT id FROM public.orders WHERE paid = 0 LOOP",
            "RAISE NOTICE '%', 'order ' || CAST(r.id AS text);",
            "FOR l IN c_lines LOOP",
            "GET DIAGNOSTICS trek_sql_rowcount = ROW_COUNT;",
            "IF trek_sql_rowcount = 0 THEN",
            "RAISE EXCEPTION USING MESSAGE = 'order missing', ERRCODE = 'TK001';",
            "CASE",
            "WHEN v_count > 10 THEN",
            "END CASE;",
            "FOR i IN REVERSE 3..1 LOOP",
            "CALL public.close_order(p_id);",
            "WHEN no_data_found THEN",
            "WHEN SQLSTATE 'TK001' THEN",
            "RAISE;");
        Assert.DoesNotContain(result.Issues, i => i.Contains("not translated", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plsql_trigger_becomes_a_trigger_function_and_a_trigger()
    {
        var code = Ora(PortableCodeKind.Trigger, "ORDERS_BIU", """
            CREATE OR REPLACE TRIGGER orders_biu
            BEFORE INSERT OR UPDATE OF price, qty ON orders
            REFERENCING NEW AS n
            FOR EACH ROW
            WHEN (n.qty > 0)
            DECLARE
              v_now DATE := SYSDATE;
            BEGIN
              IF INSERTING THEN
                :n.created := v_now;
              ELSIF UPDATING('PRICE') THEN
                :n.updated := SYSTIMESTAMP;
              END IF;
              IF :n.price < 0 THEN
                RAISE_APPLICATION_ERROR(-20002, 'negative price');
              END IF;
            END;
            """) with { Table = new PortableName("SHOP", "ORDERS") };
        var translator = new OracleToPgTranslator(Schemas.OracleToPg());
        var result = translator.Code(code, [code]);
        output.WriteLine(result.Script);
        Assert.Equal("triggers", result.Folder);
        Assert.Contains("CREATE OR REPLACE FUNCTION public.orders_biu_fn() RETURNS trigger\nLANGUAGE plpgsql\nAS $$\nDECLARE\n    v_now timestamp(0) := LOCALTIMESTAMP(0);\nBEGIN\n", result.Script, StringComparison.Ordinal);
        Lines(result.Script,
            "IF (TG_OP = 'INSERT') THEN",
            "NEW.created := v_now;",
            "NEW.updated := now();",
            "IF NEW.price < 0 THEN",
            "RAISE EXCEPTION USING MESSAGE = 'negative price', ERRCODE = 'TK002';",
            "RETURN NEW;",
            "CREATE OR REPLACE TRIGGER orders_biu BEFORE INSERT OR UPDATE OF price, qty ON public.orders",
            "FOR EACH ROW WHEN (NEW.qty > 0) EXECUTE FUNCTION public.orders_biu_fn();");

        // The next step of a chain gets the function and the trigger as PostgreSQL would list them.
        var parts = translator.Intermediate(code, [code]);
        Assert.Equal([PortableCodeKind.Function, PortableCodeKind.Trigger], parts.Select(p => p.Kind));
        Assert.Equal(new PortableName("public", "orders_biu_fn"), parts[1].TriggerFunction);
        Assert.NotNull(PgTriggerDefinition.Parse(parts[1].Definition));
        Assert.Equal("plpgsql", new PgParser(parts[0].Definition).RoutineHeader().Language);
    }

    [Fact]
    public void Packages_and_pipelined_functions_are_left_for_review()
    {
        var package = ToPg(Ora(PortableCodeKind.Function, "BILLING", "CREATE OR REPLACE PACKAGE billing AS\n  PROCEDURE run;\nEND billing;\n/\nCREATE OR REPLACE PACKAGE BODY billing AS\n  PROCEDURE run IS BEGIN NULL; END;\nEND billing;\n/"));
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for PostgreSQL", package.Script, StringComparison.Ordinal);
        Assert.Contains(package.Issues, i => i.Contains("package not translated", StringComparison.Ordinal));

        var pipelined = ToPg(Ora(PortableCodeKind.Function, "NUMBERS", "CREATE OR REPLACE FUNCTION numbers RETURN num_list PIPELINED IS\nBEGIN\n  PIPE ROW (1);\n  RETURN;\nEND;"));
        Assert.Contains(pipelined.Issues, i => i.Contains("pipelined", StringComparison.Ordinal));
    }

    [Fact]
    public void Oracle_procedures_reach_sql_server_by_way_of_postgresql()
    {
        var ctx = new ConversionContext(Schemas.Oracle(), new ConversionOptions { TargetProvider = "sqlserver" });
        var close = Ora(PortableCodeKind.Procedure, "CLOSE_ORDER", """
            CREATE OR REPLACE PROCEDURE close_order(p_id NUMBER) AS
              v_n NUMBER;
            BEGIN
              SELECT COUNT(*) INTO v_n FROM orders WHERE id = p_id;
              IF v_n = 0 THEN
                RAISE_APPLICATION_ERROR(-20001, 'no order');
              END IF;
              UPDATE orders SET status = 'closed' WHERE id = p_id;
            END;
            """);
        var result = ConversionProject.TranslatorFor(ctx).Code(close, [close]);
        output.WriteLine(result.Script);
        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.CLOSE_ORDER", result.Script, StringComparison.Ordinal);
        Assert.Contains("SELECT @V_N = COUNT(*) FROM dbo.ORDERS WHERE ID = @P_ID;", result.Script, StringComparison.Ordinal);
        Assert.Contains("UPDATE dbo.ORDERS SET STATUS = N'closed' WHERE ID = @P_ID;", result.Script, StringComparison.Ordinal);
    }
}
