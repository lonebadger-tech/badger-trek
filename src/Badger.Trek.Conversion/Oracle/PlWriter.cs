using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Conversion.Sqlite;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Oracle;

/// <summary>
/// Writes a routine or trigger body read by <see cref="PgParser"/> as PL/SQL (from PostgreSQL) or as PL/pgSQL (from
/// Oracle). PL/pgSQL was modelled on PL/SQL, so blocks, IF, loops, EXIT, assignments and exception handlers keep their
/// shape; the writer bridges what differs: SELECT INTO with no row (NULL in PostgreSQL, NO_DATA_FOUND in Oracle), FOUND
/// and SQL%ROWCOUNT, RAISE and RAISE_APPLICATION_ERROR, PERFORM, procedure calls (CALL p() and p();), cursor FOR loops,
/// exception names, and Oracle 19c's lack of booleans in SQL. Expressions, queries and DML go through
/// <see cref="SqlPrinter"/>. What cannot be carried over becomes a REVIEW comment and an issue.
/// </summary>
internal sealed partial class PlWriter
{
    private readonly ConversionContext ctx;
    private readonly List<string> issues;
    private readonly bool toOracle;
    private readonly List<string> lines = [];
    private readonly List<string> topDeclarations = [];
    private readonly HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> records = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> synthetic = new(StringComparer.Ordinal);

    /// <summary>Oracle exceptions (lowercase name) and the SQLSTATE that stands for each in PostgreSQL.</summary>
    private readonly Dictionary<string, string> exceptionStates = new(StringComparer.OrdinalIgnoreCase);
    private int depth;

    public PlWriter(ConversionContext ctx, List<string> issues, bool toOracle)
    {
        this.ctx = ctx;
        this.issues = issues;
        this.toOracle = toOracle;
        Printer = new SqlPrinter(ctx, issues) { PrintInto = true };
        if (toOracle)
        {
            Printer.NameReplacements["sqlstate"] = "SQLCODE";
            Printer.NameReplacements["found"] = Syn("trek_found");
            Printer.BooleanNames.Add("found");
        }
        else
        {
            Printer.NameReplacements["sqlcode"] = "SQLSTATE";
            Printer.NameReplacements["sql%rowcount"] = Syn("trek_sql_rowcount");
            Printer.NameReplacements["sql%found"] = "FOUND";
            Printer.NameReplacements["sql%notfound"] = "(NOT FOUND)";
            Printer.NameReplacements["sql%isopen"] = "false";
        }
    }

    public SqlPrinter Printer { get; }

    /// <summary>function, procedure or trigger.</summary>
    public string Kind { get; init; } = "function";

    /// <summary>To Oracle: a row-level BEFORE trigger, where RETURN NULL skips the row in PostgreSQL.</summary>
    public bool BeforeRowTrigger { get; init; }

    /// <summary>To PostgreSQL: what a RETURN in an Oracle trigger becomes (RETURN NEW, OLD or NULL).</summary>
    public IReadOnlyList<string> TriggerReturn { get; init; } = [];

    /// <summary>To Oracle: the body tests FOUND, so every statement that sets it records SQL%FOUND in TREK_FOUND.</summary>
    public bool UsesFound { get; init; }

    /// <summary>To PostgreSQL: the body reads SQL%ROWCOUNT, kept in trek_rowcount after every statement that sets it.</summary>
    public bool UsesRowCount { get; init; }

    /// <summary>To Oracle: the function's PostgreSQL return type, for a variable that holds a value SQL works out.</summary>
    public string? ReturnType { get; init; }

    private int conditionCount;

    /// <summary>The printed SQL holds a query (PL/SQL accepts none inside an expression).</summary>
    private static bool HasQuery(string sql) => Regex.IsMatch(sql, @"\bSELECT\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// A condition for IF, ELSIF or EXIT WHEN. To Oracle, one with a subquery (EXISTS, IN, a scalar query) is worked out
    /// by SQL first, into a number the condition then tests.
    /// </summary>
    private string PlCondition(PgExpr e)
    {
        var condition = Cond(e);
        if (!toOracle || !HasQuery(condition))
        {
            return condition;
        }

        var variable = Syn($"trek_cond{++conditionCount}");
        Synthetic($"{variable} NUMBER(1);");
        Line($"SELECT CASE WHEN {condition} THEN 1 ELSE 0 END INTO {variable} FROM DUAL;");
        return $"{variable} = 1";
    }

    private string Ident(string name) => ctx.Ident(name);

    /// <summary>A variable the writer adds (trek_found ...): upper case in Oracle, so it is never quoted.</summary>
    private string Syn(string name) => ctx.Ident(toOracle ? name.ToUpperInvariant() : name);

    private string Expr(PgExpr e) => Printer.Expr(e);

    private string Cond(PgExpr e) => Printer.Cond(e);

    /// <summary>A value for a PL/SQL BOOLEAN: TRUE and FALSE as they are, any other condition as written.</summary>
    private string BooleanValue(PgExpr e) => e switch
    {
        PgLiteral { Kind: LiteralKind.True } => "TRUE",
        PgLiteral { Kind: LiteralKind.False } => "FALSE",
        PgLiteral { Kind: LiteralKind.Null } => "NULL",
        _ => Cond(e),
    };

    private void Line(string text)
    {
        foreach (var l in text.Split('\n'))
        {
            lines.Add(new string(' ', 4 * depth) + l);
        }
    }

    private void Note(string message, string? original = null)
    {
        issues.Add(original is null ? message : $"{message}: {Collapse(original)}");
        Line(Review.Comment(message, original));
    }

    private static string Collapse(string text)
    {
        var c = Regex.Replace(text, @"\s+", " ").Trim();
        return c.Length > 120 ? c[..117] + "..." : c;
    }

    private void Synthetic(string declaration)
    {
        if (synthetic.Add(declaration))
        {
            topDeclarations.Add(declaration);
        }
    }

    // ------------------------------------------------------------------ the top block

    /// <summary>
    /// The top block's statements and handlers, indented one level, with <paramref name="epilogue"/> after the
    /// statements; then <see cref="Declarations"/> holds its declarations (including any the statements needed).
    /// </summary>
    public string Top(PgBlock block, IReadOnlyList<string> epilogue)
    {
        foreach (var d in block.Declarations)
        {
            if (Declaration(d) is { } text)
            {
                topDeclarations.Add(text);
            }
        }

        depth = 1;
        var before = lines.Count;

        // To Oracle, a trigger's closing RETURN NEW / RETURN NULL says nothing: the trigger just ends.
        var statements = toOracle && Kind == "trigger" && block.Body is [.., PgReturn] ? block.Body.Take(block.Body.Count - 1) : block.Body;
        foreach (var s in statements)
        {
            Statement(s);
        }

        foreach (var e in epilogue)
        {
            Line(e);
        }

        if (lines.Count == before)
        {
            Line("NULL;");
        }

        depth = 0;
        Handlers(block.Handlers, block.Body);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The top block's declarations, one per line, indented one level (empty when there are none).</summary>
    public string Declarations => string.Concat(topDeclarations.Select(d => "    " + d + "\n"));

    /// <summary>A routine's statements written one level in (a LANGUAGE sql body, which has no block).</summary>
    public string Statements(IEnumerable<PgStatement> statements, IReadOnlyList<string> epilogue)
    {
        depth = 1;
        foreach (var s in statements)
        {
            Statement(s);
        }

        foreach (var e in epilogue)
        {
            Line(e);
        }

        if (lines.Count == 0)
        {
            Line("NULL;");
        }

        depth = 0;
        return string.Join('\n', lines) + "\n";
    }

    // ------------------------------------------------------------------ declarations

    /// <summary>A declaration as text (no indentation), or null when it needs none in the target.</summary>
    private string? Declaration(PgDeclaration d)
    {
        declared.Add(d.Name);
        return toOracle ? OracleDeclaration(d) : PgDeclarationText(d);
    }

    private string? OracleDeclaration(PgDeclaration d)
    {
        var name = Ident(d.Name);
        string? type = null;
        switch (d.Special)
        {
            case { } alias when alias.StartsWith("alias ", StringComparison.Ordinal):
                var target = alias[6..];
                Printer.NameReplacements[d.Name] = target.StartsWith('$') ? Printer.NameReplacements.GetValueOrDefault(target, target) : Ident(target);
                return null;
            case "record":
                records.Add(d.Name);
                return null;
            case { } rowType when rowType.EndsWith("%rowtype", StringComparison.Ordinal):
                type = $"{Printer.Table(TypeSource(rowType[..^8]))}%ROWTYPE";
                break;
            case { } columnType when columnType.EndsWith("%type", StringComparison.Ordinal):
                type = TypeOf(columnType[..^5]);
                break;
            case { } other:
                issues.Add($"declaration of {d.Name} is not translated ({other})");
                return Review.Comment($"declaration of {d.Name} not translated ({other})");
        }

        var boolean = type is null && d.Type?.ToLowerInvariant() is "boolean" or "bool";
        if (boolean)
        {
            Printer.BooleanNames.Add(d.Name);
        }

        type ??= boolean ? "BOOLEAN" : OracleVariableType(d.Type ?? "text");
        var value = d.Default is null ? null : boolean ? BooleanValue(d.Default) : Expr(d.Default);
        if (d.Constant)
        {
            return $"{name} CONSTANT {type} := {value ?? "NULL"};";
        }

        if (d.NotNull && value is null)
        {
            issues.Add($"{d.Name} is NOT NULL without a value, which Oracle does not allow: NOT NULL was dropped");
        }

        return $"{name} {type}{(d.NotNull && value is not null ? " NOT NULL" : string.Empty)}{(value is null ? string.Empty : " := " + value)};";
    }

    private string? PgDeclarationText(PgDeclaration d)
    {
        var name = Ident(d.Name);
        switch (d.Special)
        {
            case { } pragma when pragma.StartsWith("pragma ", StringComparison.Ordinal):
                Pragma(pragma[7..]);
                return null;
            case "exception":
                exceptionStates.TryAdd(d.Name, $"TKU{exceptionStates.Count + 1:00}");
                return null;
            case "cursor" when d.Default is PgSubquery cursor:
                Printer.NameReplacements[d.Name + "%found"] = "FOUND";
                Printer.NameReplacements[d.Name + "%notfound"] = "(NOT FOUND)";
                return $"{name} CURSOR FOR {Printer.Query(cursor.Query)};";
            case { } rowType when rowType.EndsWith("%rowtype", StringComparison.Ordinal):
                return $"{name} {Printer.Table(TypeSource(rowType[..^8]))}%ROWTYPE;";
            case { } columnType when columnType.EndsWith("%type", StringComparison.Ordinal):
                return $"{name} {TypeOf(columnType[..^5])}{(d.Default is null ? string.Empty : " := " + Expr(d.Default))};";
            case { } other:
                issues.Add($"declaration of {d.Name} is not translated ({other})");
                return Review.Comment($"declaration of {d.Name} not translated ({other})");
        }

        var type = PgType(d.Type ?? "varchar2");
        var value = d.Default is null ? string.Empty : " := " + Expr(d.Default);
        return d.Constant ? $"{name} CONSTANT {type}{value};" : $"{name} {type}{(d.NotNull ? " NOT NULL" : string.Empty)}{value};";
    }

    /// <summary>PRAGMA EXCEPTION_INIT ties an exception to an error number; AUTONOMOUS_TRANSACTION has no counterpart.</summary>
    private void Pragma(string text)
    {
        var init = Regex.Match(text, @"EXCEPTION_INIT\s*\(\s*(?<name>[\w$#""]+)\s*,\s*(?<code>-?\d+)\s*\)", RegexOptions.IgnoreCase);
        if (init.Success)
        {
            var code = int.Parse(init.Groups["code"].Value, CultureInfo.InvariantCulture);
            exceptionStates[init.Groups["name"].Value.Trim('"')] = OracleErrorState(code) ?? $"TKU{exceptionStates.Count + 1:00}";
            if (OracleErrorState(code) is null)
            {
                issues.Add($"{Collapse(text)}: Oracle error {code} has no PostgreSQL counterpart; the exception is raised and caught by its own SQLSTATE");
            }
        }
        else if (Regex.IsMatch(text, @"AUTONOMOUS_TRANSACTION", RegexOptions.IgnoreCase))
        {
            issues.Add("PRAGMA AUTONOMOUS_TRANSACTION has no PostgreSQL counterpart: the routine now commits with its caller (use dblink for an independent transaction)");
        }
    }

    /// <summary>The SQLSTATE for an Oracle error number: RAISE_APPLICATION_ERROR's -20000..-20999 become TK000..TK999.</summary>
    private static string? OracleErrorState(int code) => code switch
    {
        <= -20000 and >= -20999 => $"TK{-code - 20000:000}",
        -1 => "23505",
        -1403 or 100 => "P0002",
        -1422 => "P0003",
        -1476 => "22012",
        _ => null,
    };

    /// <summary>A name in table%ROWTYPE: the table, as a name the printer maps.</summary>
    private static PgName TypeSource(string source) => new(source.Split('.').Select(p => p.Trim('"')).ToList());

    /// <summary>x%TYPE: table.column%TYPE with both mapped, or variable%TYPE.</summary>
    private string TypeOf(string source)
    {
        var parts = source.Split('.').Select(p => p.Trim('"')).ToList();
        return parts.Count == 1
            ? $"{Ident(parts[0])}%TYPE"
            : $"{Printer.Table(new PgName(parts[..^1]))}.{Ident(parts[^1])}%TYPE";
    }

    /// <summary>A PL/SQL variable type for a PostgreSQL type: the column mapping, but unbounded text as VARCHAR2(32767).</summary>
    private string OracleVariableType(string pgType)
    {
        var lower = pgType.Trim().ToLowerInvariant();
        if (lower is "text" or "varchar" or "character varying" or "citext" or "name")
        {
            return "VARCHAR2(32767)";
        }

        return TypeMapper.ToOracle(pgType, ctx).Type;
    }

    /// <summary>A PL/SQL parameter or RETURN type for a PostgreSQL type: Oracle takes no length, precision or scale there.</summary>
    public string OracleParameterType(string pgType) =>
        Regex.Replace(TypeMapper.ToOracle(pgType, ctx).Type, @"\s*\([^)]*\)", string.Empty);

    /// <summary>The PostgreSQL type of a PL/SQL variable, parameter or return type (table.column%TYPE kept, mapped).</summary>
    public string PgType(string oracleType)
    {
        var t = oracleType.Trim();
        if (t.EndsWith("%type", StringComparison.OrdinalIgnoreCase))
        {
            return TypeOf(t[..^5]);
        }

        if (t.EndsWith("%rowtype", StringComparison.OrdinalIgnoreCase))
        {
            return $"{Printer.Table(TypeSource(t[..^8]))}%ROWTYPE";
        }

        return t.ToLowerInvariant() switch
        {
            "pls_integer" or "binary_integer" or "simple_integer" or "natural" or "naturaln" or "positive" or "positiven" or "signtype" => "integer",
            "varchar2" or "nvarchar2" or "varchar" or "clob" or "nclob" or "long" or "string" => "text",
            "number" => "numeric",
            "boolean" => "boolean",
            "sys_refcursor" => "refcursor",
            _ => TypeMapper.OracleToPgType(t).Type,
        };
    }

    // ------------------------------------------------------------------ statements

    private void Body(IEnumerable<PgStatement> statements)
    {
        depth++;
        var before = lines.Count;
        foreach (var s in statements)
        {
            Statement(s);
        }

        if (lines.Count == before)
        {
            Line("NULL;");
        }

        depth--;
    }

    private void Label(string? label)
    {
        if (label is not null)
        {
            Line($"<<{Ident(label)}>>");
        }
    }

    private void Statement(PgStatement s)
    {
        switch (s)
        {
            case PgBlock b:
                Block(b);
                break;
            case PgAssign a:
                Assign(a);
                break;
            case PgSelectStatement sel:
                SelectInto(sel);
                break;
            case PgInsert or PgUpdate or PgDelete:
                Dml(s);
                break;
            case PgIf i:
                // PL/SQL takes no subquery in a condition: those are worked out first (all of them, before the IF).
                var conditions = i.Branches.Select(b => PlCondition(b.Condition)).ToList();
                for (var n = 0; n < i.Branches.Count; n++)
                {
                    Line($"{(n == 0 ? "IF" : "ELSIF")} {conditions[n]} THEN");
                    Body(i.Branches[n].Body);
                }

                if (i.Else is not null)
                {
                    Line("ELSE");
                    Body(i.Else);
                }

                Line("END IF;");
                break;
            case PgCaseStatement c:
                Line(c.Input is null ? "CASE" : $"CASE {Expr(c.Input)}");
                foreach (var (when, body) in c.Whens)
                {
                    Line($"WHEN {(c.Input is null ? Cond(when) : Expr(when))} THEN");
                    Body(body);
                }

                if (c.Else is not null)
                {
                    Line("ELSE");
                    Body(c.Else);
                }

                Line("END CASE;");
                break;
            case PgLoop l:
                Label(l.Label);
                Line("LOOP");
                Body(l.Body);
                Line("END LOOP;");
                break;
            case PgWhile w:
                Label(w.Label);
                if (toOracle && HasQuery(Cond(w.Condition)))
                {
                    // The condition holds a subquery: work it out at the top of every pass.
                    Line("LOOP");
                    depth++;
                    Line($"EXIT WHEN NOT ({PlCondition(w.Condition)});");
                    depth--;
                    Body(w.Body);
                    Line("END LOOP;");
                    break;
                }

                Line($"WHILE {Cond(w.Condition)} LOOP");
                Body(w.Body);
                Line("END LOOP;");
                break;
            case PgForRange f:
                ForRange(f);
                break;
            case PgForQuery f:
                ForQuery(f);
                break;
            case PgExit e:
                Line($"{(e.Continue ? "CONTINUE" : "EXIT")}{(e.Label is null ? string.Empty : " " + Ident(e.Label))}{(e.When is null ? string.Empty : " WHEN " + PlCondition(e.When))};");
                break;
            case PgReturn r:
                Return(r);
                break;
            case PgRaise r:
                Raise(r);
                break;
            case PgPerform p:
                Perform(p);
                break;
            case PgExecute e:
                Execute(e);
                break;
            case PgGetDiagnostics g:
                foreach (var (target, item) in g.Items)
                {
                    if (item.Equals("row_count", StringComparison.OrdinalIgnoreCase))
                    {
                        Line($"{Expr(target)} := SQL%ROWCOUNT;");
                    }
                    else
                    {
                        Note($"GET DIAGNOSTICS {item} is not translated", s.Source);
                    }
                }

                break;
            case PgCallStatement c:
                Call(c.Call, s.Source);
                break;
            case PgNull:
                Line("NULL;");
                break;
            case PgOpen o:
                Line($"OPEN {Ident(o.Cursor)};");
                break;
            case PgFetch f:
                Line($"FETCH {Ident(f.Cursor)} INTO {string.Join(", ", f.Into.Select(Expr))};");
                AfterRowCount();
                break;
            case PgClose c:
                Line($"CLOSE {Ident(c.Cursor)};");
                break;
            case PgTransaction t:
                if (Kind != "procedure")
                {
                    issues.Add($"{t.Command} in a {Kind} fails when it runs: only a procedure called on its own (not inside a transaction block or a query) can end the transaction");
                }

                Line(t.Command + ";");
                break;
            case PgRaw raw:
                Note($"not translated ({raw.Why})", s.Source);
                break;
            default:
                Note("not translated", s.Source);
                break;
        }
    }

    private void Block(PgBlock b)
    {
        if (b.Declarations.Count == 0 && b.Handlers.Count == 0 && b.Label is null)
        {
            foreach (var s in b.Body)
            {
                Statement(s);
            }

            return;
        }

        Label(b.Label);
        var declarations = b.Declarations.Select(Declaration).OfType<string>().ToList();
        if (declarations.Count > 0)
        {
            Line("DECLARE");
            depth++;
            foreach (var d in declarations)
            {
                Line(d);
            }

            depth--;
        }

        Line("BEGIN");
        Body(b.Body);
        Handlers(b.Handlers, b.Body);
        Line("END;");
    }

    private void Assign(PgAssign a)
    {
        var target = a.Target is PgName { Parts.Count: 1 } n && Printer.BooleanNames.Contains(n.Last) ? Ident(n.Last) : Expr(a.Target);
        var boolean = toOracle && a.Target is PgName { Parts.Count: 1 } b && Printer.BooleanNames.Contains(b.Last);
        if (toOracle && !boolean && HasQuery(Expr(a.Value)))
        {
            // PL/SQL takes no subquery in an expression: SQL works the value out.
            Line($"SELECT {Expr(a.Value)} INTO {target} FROM DUAL;");
            return;
        }

        Line($"{target} := {(boolean ? BooleanValue(a.Value) : Expr(a.Value))};");
    }

    // ------------------------------------------------------------------ SQL inside the body

    private void SelectInto(PgSelectStatement sel)
    {
        if (sel.Query.Body is not PgSelectCore { Into.Count: > 0 } core)
        {
            Note("a SELECT without INTO is not translated", sel.Source);
            return;
        }

        if (!toOracle)
        {
            // Oracle's SELECT INTO raises NO_DATA_FOUND / TOO_MANY_ROWS: PL/pgSQL's STRICT does the same.
            Line(Printer.Query(sel.Query with { Body = core with { IntoStrict = true } }) + ";");
            AfterRowCount();
            return;
        }

        if (core.Into is [PgName { Parts.Count: 1 } r] && records.Contains(r.Last))
        {
            issues.Add($"SELECT INTO the record {r.Last}: declare it as table%ROWTYPE, or list the fields, for Oracle");
        }

        var text = Printer.Query(sel.Query) + ";";
        if (core.IntoStrict || AlwaysOneRow(core))
        {
            Line(text);
            AfterFound();
            return;
        }

        // PL/pgSQL leaves the targets NULL when no row comes back; Oracle raises NO_DATA_FOUND.
        Line("BEGIN");
        depth++;
        Line(text);
        AfterFound();
        depth--;
        Line("EXCEPTION");
        depth++;
        Line("WHEN NO_DATA_FOUND THEN");
        depth++;
        foreach (var target in core.Into)
        {
            Line($"{Expr(target)} := NULL;");
        }

        if (UsesFound)
        {
            Line($"{Syn("trek_found")} := FALSE;");
        }

        depth -= 2;
        Line("END;");
    }

    /// <summary>A query that returns exactly one row: no FROM, or only aggregates without GROUP BY.</summary>
    private static bool AlwaysOneRow(PgSelectCore core) =>
        core.From.Count == 0
        || (core.GroupBy.Count == 0 && core.Having is null && core.Targets.All(t => t.Expr is PgCall { Over: null } c
            && c.Function.Last.ToLowerInvariant() is "count" or "sum" or "min" or "max" or "avg" or "string_agg" or "array_agg" or "bool_and" or "bool_or" or "every" or "listagg"));

    private void Dml(PgStatement s)
    {
        if (s is PgInsert { OnConflict: { } conflict } insert && toOracle)
        {
            Note("ON CONFLICT has no Oracle form and was dropped: use MERGE, or catch DUP_VAL_ON_INDEX", conflict);
            s = insert with { OnConflict = null };
        }

        var (text, into) = s switch
        {
            PgInsert i => (Printer.Insert(i), i.Into),
            PgUpdate u => (Printer.Update(u), u.Into),
            PgDelete d => (Printer.Delete(d), d.Into),
            _ => throw new InvalidOperationException(),
        };
        if (into.Count > 0)
        {
            text = text.TrimEnd().TrimEnd(';') + " INTO " + string.Join(", ", into.Select(Expr)) + ";";
        }

        Line(text);
        AfterFound();
        AfterRowCount();
    }

    /// <summary>To Oracle: keep FOUND (TREK_FOUND) as PostgreSQL sets it after the statement just written.</summary>
    private void AfterFound()
    {
        if (toOracle && UsesFound)
        {
            Synthetic($"{Syn("trek_found")} BOOLEAN := FALSE;");
            Line($"{Syn("trek_found")} := SQL%FOUND;");
        }
    }

    /// <summary>To PostgreSQL: keep SQL%ROWCOUNT (trek_rowcount) after the statement just written.</summary>
    private void AfterRowCount()
    {
        if (!toOracle && UsesRowCount)
        {
            Synthetic($"{Syn("trek_sql_rowcount")} bigint := 0;");
            Line($"GET DIAGNOSTICS {Syn("trek_sql_rowcount")} = ROW_COUNT;");
        }
    }

    private void Perform(PgPerform p)
    {
        // PERFORM f(x): call a function and drop its value. PL/SQL needs somewhere to put it.
        if (p.Query is { With.Count: 0, Body: PgSelectCore { From.Count: 0, Where: null, Targets: [{ Expr: PgCall call }] } })
        {
            Synthetic($"{Syn("trek_discard")} VARCHAR2(32767);");
            Line($"{Syn("trek_discard")} := {Expr(call)};");
            return;
        }

        // PERFORM ... FROM ...: run the query for its rows (FOUND tells whether there were any).
        Synthetic($"{Syn("trek_count")} NUMBER;");
        Line($"SELECT COUNT(*) INTO {Syn("trek_count")} FROM ({Printer.Query(p.Query)}) WHERE ROWNUM = 1;");
        if (UsesFound)
        {
            Synthetic($"{Syn("trek_found")} BOOLEAN := FALSE;");
            Line($"{Syn("trek_found")} := {Syn("trek_count")} > 0;");
        }
    }

    private void Execute(PgExecute e)
    {
        var into = e.Into.Count == 0 ? string.Empty : " INTO " + string.Join(", ", e.Into.Select(Expr));
        var args = e.Using.Count == 0 ? string.Empty : " USING " + string.Join(", ", e.Using.Select(Expr));
        if (toOracle)
        {
            issues.Add("dynamic SQL: the statement text is still PostgreSQL ($1 placeholders become :1 in Oracle)");
            Line($"EXECUTE IMMEDIATE {Expr(e.Sql)}{into}{args};");
            AfterFound();
        }
        else
        {
            issues.Add("dynamic SQL: the statement text is still Oracle SQL (:name placeholders become $1, $2 ... in PostgreSQL)");
            Line($"EXECUTE {Expr(e.Sql)}{into}{args};");
            AfterRowCount();
        }
    }

    // ------------------------------------------------------------------ loops

    private void ForRange(PgForRange f)
    {
        var variable = Ident(f.Variable);
        if (f.Step is not null)
        {
            // PL/SQL's FOR has no BY: a WHILE loop counts instead.
            issues.Add($"FOR {f.Variable} ... BY became a WHILE loop: a CONTINUE in it skips the step");
            Synthetic($"{variable} NUMBER;");
            Line($"{variable} := {Expr(f.From)};");
            Label(f.Label);
            Line($"WHILE {variable} {(f.Reverse ? ">=" : "<=")} {Expr(f.To)} LOOP");
            Body(f.Body);
            depth++;
            Line($"{variable} := {variable} {(f.Reverse ? "-" : "+")} {Expr(f.Step)};");
            depth--;
            Line("END LOOP;");
            return;
        }

        // The tree holds PL/pgSQL's order (REVERSE hi..lo); PL/SQL writes REVERSE lo..hi.
        var (low, high) = f.Reverse && toOracle ? (f.To, f.From) : (f.From, f.To);
        Label(f.Label);
        Line($"FOR {variable} IN {(f.Reverse ? "REVERSE " : string.Empty)}{Expr(low)}..{Expr(high)} LOOP");
        Body(f.Body);
        Line("END LOOP;");
    }

    private void ForQuery(PgForQuery f)
    {
        var variable = Ident(f.Variable);
        if (f.Dynamic is not null)
        {
            Note("FOR ... IN EXECUTE is not translated: OPEN a SYS_REFCURSOR FOR the statement and FETCH from it", f.Source);
            return;
        }

        if (toOracle && declared.Contains(f.Variable) && !records.Contains(f.Variable))
        {
            issues.Add($"FOR {f.Variable} IN query: Oracle's loop variable is a record (write {f.Variable}.column)");
        }

        if (!toOracle && f.Cursor is null && !declared.Contains(f.Variable))
        {
            // PL/SQL declares the loop record itself; PL/pgSQL needs it declared.
            declared.Add(f.Variable);
            topDeclarations.Add($"{variable} record;");
        }

        var source = f.Cursor is not null ? Ident(f.Cursor) : toOracle ? $"({Printer.Query(f.Query!)})" : Printer.Query(f.Query!);
        if (toOracle && UsesFound)
        {
            Synthetic($"{Syn("trek_found")} BOOLEAN := FALSE;");
            Line($"{Syn("trek_found")} := FALSE;");
        }

        Label(f.Label);
        Line($"FOR {variable} IN {source} LOOP");
        if (toOracle && UsesFound)
        {
            depth++;
            Line($"{Syn("trek_found")} := TRUE;");
            depth--;
        }

        Body(f.Body);
        Line("END LOOP;");
    }

    // ------------------------------------------------------------------ RETURN, RAISE, calls

    private void Return(PgReturn r)
    {
        if (Kind == "trigger")
        {
            if (!toOracle)
            {
                foreach (var l in TriggerReturn)
                {
                    Line(l);
                }

                return;
            }

            if (BeforeRowTrigger && r.Value is PgLiteral { Kind: LiteralKind.Null })
            {
                Note("RETURN NULL skips the row in PostgreSQL; an Oracle trigger cannot: raise an error instead, or move the test into the statement");
            }

            Line("RETURN;");
            return;
        }

        if (Kind == "procedure" || r.Value is null)
        {
            Line("RETURN;");
            return;
        }

        if (toOracle && HasQuery(Expr(r.Value)) && ReturnType is { } returnType)
        {
            Synthetic($"{Syn("trek_return")} {OracleVariableType(returnType)};");
            Line($"SELECT {Expr(r.Value)} INTO {Syn("trek_return")} FROM DUAL;");
            Line($"RETURN {Syn("trek_return")};");
            return;
        }

        Line($"RETURN {Expr(r.Value)};");
    }

    private void Raise(PgRaise r)
    {
        if (r.Format is null && r.Args.Count == 0 && r.Using.Count == 0)
        {
            Line("RAISE;");
            return;
        }

        if (!toOracle)
        {
            // PL/SQL RAISE name: a predefined exception, or one the routine declared.
            var name = r.Using.TryGetValue("errcode", out var e) ? SqlPrinter.StringArg(e) ?? "error" : "error";
            if (PgCondition(name) is { } condition && condition != "others")
            {
                Line($"RAISE {condition};");
                return;
            }

            if (!exceptionStates.TryGetValue(name, out var state))
            {
                issues.Add($"RAISE {name}: the exception is not declared here (a package's?); it is raised with SQLSTATE P0001");
                state = "P0001";
            }

            Line($"RAISE EXCEPTION USING MESSAGE = {ctx.Literal(name)}, ERRCODE = '{state}';");
            return;
        }

        string message;
        if (r.Using.TryGetValue("message", out var m))
        {
            message = Expr(m);
        }
        else if (r.Format is PgLiteral { Kind: LiteralKind.String } format)
        {
            message = Format(format.Value, r.Args);
        }
        else
        {
            // RAISE unique_violation / RAISE SQLSTATE '...'.
            var condition = r.Using.TryGetValue("errcode", out var c) ? SqlPrinter.StringArg(c) ?? "error" : "error";
            if (OracleCondition(condition) is { } predefined && predefined != "OTHERS" && r.Level == "exception")
            {
                Line($"RAISE {predefined};");
                return;
            }

            message = ctx.Literal(condition);
        }

        if (r.Level != "exception")
        {
            Line($"DBMS_OUTPUT.PUT_LINE({message});");
            return;
        }

        var code = -20000;
        if (r.Using.TryGetValue("errcode", out var errcode) && SqlPrinter.StringArg(errcode) is { } sqlstate)
        {
            var tk = Regex.Match(sqlstate, @"^TK(\d{3})$");
            if (tk.Success)
            {
                code = -20000 - int.Parse(tk.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if (sqlstate != "P0001")
            {
                issues.Add($"error code {sqlstate} became Oracle error -20000");
            }
        }

        Line($"RAISE_APPLICATION_ERROR({code}, {message});");
    }

    /// <summary>A RAISE message: the format's % placeholders replaced by the arguments, joined with ||.</summary>
    private string Format(string format, IReadOnlyList<PgExpr> args)
    {
        var parts = new List<string>();
        var literal = new StringBuilder();
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length && format[i + 1] == '%')
            {
                literal.Append('%');
                i++;
            }
            else if (format[i] == '%' && next < args.Count)
            {
                if (literal.Length > 0)
                {
                    parts.Add(ctx.Literal(literal.ToString()));
                    literal.Clear();
                }

                parts.Add(Expr(args[next++]));
            }
            else
            {
                literal.Append(format[i]);
            }
        }

        if (literal.Length > 0 || parts.Count == 0)
        {
            parts.Add(ctx.Literal(literal.ToString()));
        }

        return string.Join(" || ", parts);
    }

    private void Call(PgCall call, string source)
    {
        var args = call.Args.Select(Expr).ToList();
        var parts = call.Function.Parts;
        if (toOracle)
        {
            var schema = parts.Count > 1 ? parts[^2] : ctx.Schema.DefaultSchema;
            var name = ctx.Qualified(new PortableName(schema, parts[^1]));
            Line(args.Count == 0 ? $"{name};" : $"{name}({string.Join(", ", args)});");
            return;
        }

        var full = string.Join('.', parts).ToUpperInvariant();
        switch (full)
        {
            case "DBMS_OUTPUT.PUT_LINE" or "DBMS_OUTPUT.PUT" when args.Count == 1:
                Line($"RAISE NOTICE '%', {args[0]};");
                return;
            case "DBMS_OUTPUT.ENABLE" or "DBMS_OUTPUT.DISABLE" or "DBMS_OUTPUT.NEW_LINE":
                return;
            case "DBMS_LOCK.SLEEP" or "DBMS_SESSION.SLEEP" when args.Count == 1:
                Line($"PERFORM pg_sleep({args[0]});");
                return;
            case "RAISE_APPLICATION_ERROR" when args.Count >= 2:
            {
                var number = call.Args[0] is PgLiteral { Kind: LiteralKind.Number } n ? int.Parse(n.Value, CultureInfo.InvariantCulture)
                    : call.Args[0] is PgUnary { Op: "-", Operand: PgLiteral { Kind: LiteralKind.Number } u } ? -int.Parse(u.Value, CultureInfo.InvariantCulture)
                    : 0;
                var state = OracleErrorState(number);
                if (state is null)
                {
                    issues.Add($"RAISE_APPLICATION_ERROR({Show(call.Args[0])}, ...): the error number is not a literal; raised with SQLSTATE P0001");
                }

                Line($"RAISE EXCEPTION USING MESSAGE = {args[1]}, ERRCODE = '{state ?? "P0001"}';");
                return;
            }
        }

        var isSchema = parts.Count == 1 || parts[^2].Equals(ctx.Schema.DefaultSchema, StringComparison.OrdinalIgnoreCase) || ctx.Schema.Schemas.Contains(parts[^2], StringComparer.OrdinalIgnoreCase);
        if (!isSchema)
        {
            Note($"{full} is in an Oracle package, which is not translated: rewrite the call for PostgreSQL", source);
            return;
        }

        var target = ctx.Qualified(new PortableName(parts.Count > 1 ? parts[^2] : ctx.Schema.DefaultSchema, parts[^1]));
        Line($"CALL {target}({string.Join(", ", args)});");
        AfterRowCount();
    }

    private static string Show(PgExpr e) => e switch
    {
        PgLiteral l => l.Value,
        PgName n => string.Join('.', n.Parts),
        _ => "...",
    };

    // ------------------------------------------------------------------ exception handlers

    private void Handlers(IReadOnlyList<PgHandler> handlers, IReadOnlyList<PgStatement> body)
    {
        if (handlers.Count == 0)
        {
            return;
        }

        if (Writes(body))
        {
            issues.Add(toOracle
                ? "an EXCEPTION block that catches an error undoes the block's changes in PostgreSQL; Oracle keeps them: add SAVEPOINT and ROLLBACK TO SAVEPOINT if that matters"
                : "PostgreSQL undoes the block's changes when its EXCEPTION handler catches an error; Oracle kept them: move the changes out of the block if they must stay");
        }

        Line("EXCEPTION");
        depth++;
        var others = false;
        foreach (var handler in handlers)
        {
            var conditions = handler.Conditions.Select(c => toOracle ? OracleCondition(c) ?? Unknown(c, "OTHERS") : PgConditionOrState(c)).Distinct().ToList();
            if (conditions.Contains(toOracle ? "OTHERS" : "others"))
            {
                if (others)
                {
                    Note($"a second handler that catches every error was dropped (WHEN {string.Join(" OR ", handler.Conditions)})");
                    continue;
                }

                others = true;
                conditions = [toOracle ? "OTHERS" : "others"];
            }

            Line($"WHEN {string.Join(" OR ", conditions)} THEN");
            Body(handler.Body);
        }

        depth--;
    }

    private string Unknown(string condition, string fallback)
    {
        issues.Add($"exception {condition} has no {(toOracle ? "Oracle" : "PostgreSQL")} name: its handler now catches every error; test {(toOracle ? "SQLCODE" : "SQLSTATE")} in it");
        return fallback;
    }

    /// <summary>The Oracle exception for a PostgreSQL condition name, or null.</summary>
    private static string? OracleCondition(string condition) => condition.ToLowerInvariant() switch
    {
        "others" => "OTHERS",
        "unique_violation" => "DUP_VAL_ON_INDEX",
        "no_data_found" => "NO_DATA_FOUND",
        "too_many_rows" => "TOO_MANY_ROWS",
        "division_by_zero" => "ZERO_DIVIDE",
        "case_not_found" => "CASE_NOT_FOUND",
        "invalid_text_representation" => "INVALID_NUMBER",
        "numeric_value_out_of_range" or "data_exception" or "string_data_right_truncation" => "VALUE_ERROR",
        "invalid_cursor_state" => "INVALID_CURSOR",
        "duplicate_cursor" => "CURSOR_ALREADY_OPEN",
        _ => null,
    };

    /// <summary>The PostgreSQL condition for an Oracle predefined exception, or null.</summary>
    private static string? PgCondition(string exception) => exception.ToUpperInvariant() switch
    {
        "OTHERS" => "others",
        "NO_DATA_FOUND" => "no_data_found",
        "TOO_MANY_ROWS" => "too_many_rows",
        "DUP_VAL_ON_INDEX" => "unique_violation",
        "ZERO_DIVIDE" => "division_by_zero",
        "CASE_NOT_FOUND" => "case_not_found",
        "INVALID_NUMBER" => "invalid_text_representation",
        "VALUE_ERROR" => "data_exception",
        "INVALID_CURSOR" => "invalid_cursor_state",
        "CURSOR_ALREADY_OPEN" => "duplicate_cursor",
        "TIMEOUT_ON_RESOURCE" => "lock_not_available",
        _ => null,
    };

    private string PgConditionOrState(string exception) =>
        PgCondition(exception) ?? (exceptionStates.TryGetValue(exception, out var state) ? $"SQLSTATE '{state}'" : Unknown(exception, "others"));

    private static bool Writes(IEnumerable<PgStatement> statements) => statements.Any(s => s switch
    {
        PgInsert or PgUpdate or PgDelete or PgExecute or PgCallStatement => true,
        PgBlock b => Writes(b.Body),
        PgIf i => i.Branches.Any(b => Writes(b.Body)) || (i.Else is not null && Writes(i.Else)),
        PgCaseStatement c => c.Whens.Any(w => Writes(w.Body)) || (c.Else is not null && Writes(c.Else)),
        PgLoop l => Writes(l.Body),
        PgWhile w => Writes(w.Body),
        PgForRange f => Writes(f.Body),
        PgForQuery f => Writes(f.Body),
        _ => false,
    });
}
