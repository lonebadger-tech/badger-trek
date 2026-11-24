using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Oracle;

/// <summary>
/// PostgreSQL functions, procedures and triggers as PL/SQL. A function returning void becomes a procedure, and so does one
/// with OUT parameters; a set-returning function (RETURNS TABLE / SETOF) needs a pipelined function over an object type
/// and is left for review. A trigger is its CREATE TRIGGER and its function's body in one Oracle trigger; a statement
/// trigger over transition tables becomes a row trigger that reads :NEW / :OLD as a one-row table.
/// </summary>
internal sealed partial class PgToOracleTranslator
{
    private string Routine(PortableCode code, List<string> issues, ref string folder)
    {
        var header = new PgParser(code.Definition).RoutineHeader();
        if (header.ReturnsTrigger)
        {
            return string.Empty;
        }

        if (header.Language is not ("plpgsql" or "sql"))
        {
            issues.Add($"LANGUAGE {header.Language} is not translated");
            return Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for Oracle"));
        }

        if (header.ReturnsTable.Count > 0 || header.SetOf)
        {
            issues.Add("returns a set (RETURNS TABLE or SETOF): Oracle needs a pipelined function over an object type, or a SYS_REFCURSOR; rewrite it");
            return Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for Oracle"));
        }

        var outArgs = header.Args.Any(a => a.Mode is "out" or "inout");
        var procedure = header.Procedure || header.Returns is null or "void" || (outArgs && header.Returns is "record");
        if (procedure && !header.Procedure && header.Returns is not (null or "void"))
        {
            issues.Add("this function has OUT parameters and returns them as a record: it became a procedure; callers pass variables for the OUT parameters");
        }

        var writer = new PlWriter(ctx, issues, toOracle: true)
        {
            Kind = procedure ? "procedure" : "function",
            UsesFound = Regex.IsMatch(header.Body, @"\bFOUND\b", RegexOptions.IgnoreCase),
            ReturnType = procedure ? null : header.Returns,
        };
        var parameters = new List<string>();
        for (var i = 0; i < header.Args.Count; i++)
        {
            var arg = header.Args[i];
            var name = ctx.Ident(arg.Name ?? $"p{i + 1}");
            writer.Printer.NameReplacements["$" + (i + 1)] = name;
            var mode = arg.Mode switch { "out" => "OUT", "inout" => "IN OUT", _ => "IN" };
            // Oracle takes a default only for IN parameters.
            var value = arg.Default is null || arg.Mode is "out" or "inout" ? string.Empty : " DEFAULT " + writer.Printer.Expr(arg.Default);
            parameters.Add($"{name} {mode} {writer.OracleParameterType(arg.Type)}{value}");
        }

        string body;
        string? result = null;
        if (header.Language == "plpgsql")
        {
            body = writer.Top(new PgParser(header.Body).PlpgsqlBody(), []);
        }
        else
        {
            var statements = new PgParser(header.Body).SqlBody();
            var epilogue = new List<string>();
            if (!procedure && statements is [.., PgSelectStatement { Query.Body: PgSelectCore last } select])
            {
                // A LANGUAGE sql function returns its last query's first row: RETURN the value, or SELECT it INTO a variable first.
                if (last is { From.Count: 0, Where: null, Targets: [var only] } && select.Query.With.Count == 0)
                {
                    statements[^1] = new PgReturn(only.Expr);
                }
                else
                {
                    result = ctx.Ident("TREK_RESULT");
                    statements[^1] = select with { Query = select.Query with { Body = last with { Into = [new PgName(["TREK_RESULT"])] } } };
                    epilogue.Add($"RETURN {result};");
                }
            }

            body = writer.Statements(statements, epilogue);
        }

        folder = procedure ? "procedures" : "functions";
        var routine = ctx.Qualified(code.Name);
        var signature = parameters.Count == 0 ? routine : $"{routine}(\n    {string.Join(",\n    ", parameters)})";
        var declarations = writer.Declarations;
        if (result is not null)
        {
            declarations = $"    {result} {writer.OracleParameterType(header.Returns ?? "text") switch { "VARCHAR2" => "VARCHAR2(32767)", var t => t }};\n" + declarations;
        }

        var kind = procedure ? "PROCEDURE" : "FUNCTION";
        var returns = procedure ? string.Empty : $"\nRETURN {writer.OracleParameterType(header.Returns ?? "text")}";
        return $"CREATE OR REPLACE {kind} {signature}{returns}\nIS\n{declarations}BEGIN\n{body}END;\n/\n";
    }

    private string Trigger(PortableCode code, IReadOnlyList<PortableCode> all, List<string> issues)
    {
        var definition = PgTriggerDefinition.Parse(code.Definition);
        var function = code.TriggerFunction is { } fn ? all.FirstOrDefault(c => c.Kind == PortableCodeKind.Function && c.Name.Key == fn.Key) : null;
        if (definition is null || function is null || code.Table is not { } owner)
        {
            issues.Add("trigger not translated: its definition or function could not be read");
            return Review.CommentedOut(code.Definition, Review.Comment("trigger not translated"));
        }

        var header = new PgParser(function.Definition).RoutineHeader();
        if (header.Language != "plpgsql")
        {
            issues.Add($"trigger function in {header.Language} is not translated");
            return Review.CommentedOut(code.Definition + "\n\n" + function.Definition, Review.Comment("trigger not translated"));
        }

        if (definition.Arguments.Length > 0)
        {
            issues.Add($"trigger arguments ({definition.Arguments}) are not passed: TG_ARGV is not translated");
        }

        var events = definition.Events.Where(e => e is "INSERT" or "UPDATE" or "DELETE").ToList();
        if (events.Count < definition.Events.Count)
        {
            issues.Add("ON TRUNCATE has no Oracle counterpart and was left out");
        }

        if (events.Count == 0)
        {
            return Review.CommentedOut(code.Definition, Review.Comment("trigger not translated"));
        }

        var row = definition.Row;
        var writer = new PlWriter(ctx, issues, toOracle: true)
        {
            Kind = "trigger",
            BeforeRowTrigger = row && definition.When == "BEFORE",
            UsesFound = Regex.IsMatch(header.Body, @"\bFOUND\b", RegexOptions.IgnoreCase),
        };
        var printer = writer.Printer;
        var table = ctx.Schema.Tables.FirstOrDefault(t => t.Name.Key == owner.Key);
        var columns = table?.Columns.Select(c => c.Name).ToList() ?? [];
        if (!row && definition.TransitionTables.Count > 0)
        {
            // Oracle's statement triggers have no transition tables: the trigger runs per row, over that row.
            row = true;
            issues.Add("statement-level trigger over transition tables became a row-level trigger: it runs once per row, over that row (a query of the table itself in it fails with ORA-04091)");
            foreach (var (alias, which) in definition.TransitionTables)
            {
                var present = which == "new" ? events.Any(e => e is "INSERT" or "UPDATE") : events.Any(e => e is "UPDATE" or "DELETE");
                printer.TableReplacements[alias] = present && columns.Count > 0
                    ? $"(SELECT {string.Join(", ", columns.Select(c => $":{which.ToUpperInvariant()}.{ctx.Ident(c)} AS {ctx.Ident(c)}"))} FROM DUAL)"
                    : $"(SELECT {(columns.Count == 0 ? "NULL AS X" : string.Join(", ", columns.Select(c => "NULL AS " + ctx.Ident(c))))} FROM DUAL WHERE 1 = 0)";
            }
        }

        printer.NameReplacements["tg_op"] = "(CASE WHEN INSERTING THEN 'INSERT' WHEN UPDATING THEN 'UPDATE' ELSE 'DELETE' END)";
        printer.NameReplacements["tg_table_name"] = ctx.Literal(ctx.TargetName(owner).Name);
        printer.NameReplacements["tg_name"] = ctx.Literal(definition.Name);
        printer.NameReplacements["tg_when"] = ctx.Literal(definition.When);
        printer.NameReplacements["tg_level"] = row ? "'ROW'" : "'STATEMENT'";

        string? when = null;
        if (definition.Condition is { } condition)
        {
            // WHEN (...) names NEW and OLD without the colon the body uses.
            when = printer.Cond(new PgParser(condition).ExpressionOnly());
        }

        printer.NewOldPrefix = row ? ":" : string.Empty;
        var body = writer.Top(new PgParser(header.Body).PlpgsqlBody(), []);
        var eventList = string.Join(" OR ", events.Select(e => e == "UPDATE" && definition.UpdateOf.Count > 0 ? "UPDATE OF " + string.Join(", ", definition.UpdateOf.Select(ctx.Ident)) : e));
        var sb = new StringBuilder();
        sb.Append($"CREATE OR REPLACE TRIGGER {ctx.Quote(ctx.Names.Name(definition.Name))}\n");
        sb.Append($"{definition.When} {eventList} ON {ctx.Qualified(owner)}\n");
        if (row)
        {
            sb.Append("FOR EACH ROW\n");
        }

        if (when is not null && row)
        {
            sb.Append($"WHEN ({when})\n");
        }

        var declarations = writer.Declarations;
        if (declarations.Length > 0)
        {
            sb.Append("DECLARE\n").Append(declarations);
        }

        sb.Append("BEGIN\n").Append(body).Append("END;\n/\n");
        return sb.ToString();
    }
}
