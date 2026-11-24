using System.Text.RegularExpressions;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Oracle;

/// <summary>
/// PL/SQL functions, procedures and triggers as PL/pgSQL. A trigger becomes a trigger function (named after it, with
/// _fn) and a CREATE TRIGGER; :NEW and :OLD become NEW and OLD, INSERTING / UPDATING / DELETING tests of TG_OP, and the
/// function returns NEW, OLD or NULL as PostgreSQL expects. Packages, pipelined functions and compound triggers are
/// left for review. The pieces also feed the second step from Oracle to SQL Server, MySQL and SQLite.
/// </summary>
internal sealed partial class OracleToPgTranslator
{
    /// <summary>
    /// The PL/SQL object as PostgreSQL definitions with target names (a routine, or a trigger's function and CREATE
    /// TRIGGER), or null with an issue when it is not translated.
    /// </summary>
    private List<PortableCode>? PlSql(PortableCode code, List<string> issues)
    {
        if (Regex.IsMatch(code.Definition, @"\A\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NON)?EDITIONABLE\s+)?PACKAGE\b", RegexOptions.IgnoreCase))
        {
            issues.Add($"{ctx.SourceDisplay} package not translated: rewrite its procedures and functions for PostgreSQL (one schema per package is the usual layout)");
            return null;
        }

        try
        {
            return code.Kind == PortableCodeKind.Trigger ? Trigger(code, issues) : Routine(code, issues);
        }
        catch (PgParseException ex)
        {
            issues.Add($"could not parse: {ex.Message}");
            return null;
        }
    }

    private List<PortableCode>? Routine(PortableCode code, List<string> issues)
    {
        var def = new PgParser(code.Definition, ParseMode.Oracle).OracleRoutine();
        if (def.Pipelined)
        {
            issues.Add("pipelined function not translated: rewrite it as a set-returning function (RETURNS TABLE ... RETURN NEXT)");
            return null;
        }

        if (!def.Procedure && def.Args.Any(a => a.Mode != "in"))
        {
            issues.Add("function with OUT parameters not translated: PostgreSQL returns OUT parameters as the function's result; make it a procedure, or return a record");
            return null;
        }

        var writer = new PlWriter(ctx, issues, toOracle: false)
        {
            Kind = def.Procedure ? "procedure" : "function",
            UsesRowCount = UsesRowCount(code.Definition),
        };
        var parameters = def.Args.Select(a =>
            $"{a.Mode switch { "out" => "OUT ", "inout" => "INOUT ", _ => string.Empty }}{ctx.Ident(a.Name!)} {writer.PgType(a.Type)}{(a.Default is null ? string.Empty : " DEFAULT " + writer.Printer.Expr(a.Default))}").ToList();
        var body = writer.Top(def.Body, []);
        var returns = def.Procedure ? string.Empty : $" RETURNS {writer.PgType(def.Returns!)}";
        var text = $"CREATE OR REPLACE {(def.Procedure ? "PROCEDURE" : "FUNCTION")} {ctx.Qualified(code.Name)}({string.Join(", ", parameters)}){returns}\nLANGUAGE plpgsql\n{Dollar(writer.Declarations, body)}";
        return [code with { Name = ctx.TargetName(code.Name), Definition = text, Language = "plpgsql", Table = null }];
    }

    private List<PortableCode>? Trigger(PortableCode code, List<string> issues)
    {
        var def = new PgParser(code.Definition, ParseMode.Oracle).OracleTrigger();
        var owner = code.Table ?? new PortableName(ctx.Schema.DefaultSchema, def.Table.Last);
        var target = ctx.TargetName(code.Name);
        var returnRow = def.Row && def.Timing is "BEFORE" or "INSTEAD OF";
        IReadOnlyList<string> epilogue = !returnRow ? ["RETURN NULL;"]
            : def.Events is ["DELETE"] ? ["RETURN OLD;"]
            : def.Events.Contains("DELETE") ? ["IF TG_OP = 'DELETE' THEN", "    RETURN OLD;", "END IF;", "RETURN NEW;"]
            : ["RETURN NEW;"];
        var writer = new PlWriter(ctx, issues, toOracle: false)
        {
            Kind = "trigger",
            TriggerReturn = epilogue,
            UsesRowCount = UsesRowCount(code.Definition),
        };
        var printer = writer.Printer;
        printer.NameReplacements["inserting"] = "(TG_OP = 'INSERT')";
        printer.NameReplacements["updating"] = "(TG_OP = 'UPDATE')";
        printer.NameReplacements["deleting"] = "(TG_OP = 'DELETE')";
        var table = ctx.Schema.Tables.FirstOrDefault(t => t.Name.Key == owner.Key);
        foreach (var (alias, which) in def.Referencing)
        {
            foreach (var column in table?.Columns ?? [])
            {
                printer.NameReplacements[$"{alias}.{column.Name}"] = $"{which.ToUpperInvariant()}.{ctx.Ident(column.Name)}";
            }
        }

        var when = def.When is null ? null : printer.Cond(def.When);
        var body = writer.Top(def.Body, epilogue);
        var function = new PortableName(target.Schema, target.Name + "_fn");
        var functionName = ctx.QualifiedTarget(function.Schema, function.Name);
        var functionText = $"CREATE OR REPLACE FUNCTION {functionName}() RETURNS trigger\nLANGUAGE plpgsql\n{Dollar(writer.Declarations, body)}";
        var events = string.Join(" OR ", def.Events.Select(e => e == "UPDATE" && def.UpdateOf.Count > 0 ? "UPDATE OF " + string.Join(", ", def.UpdateOf.Select(ctx.Ident)) : e));
        var triggerText = $"CREATE OR REPLACE TRIGGER {ctx.Quote(target.Name)} {def.Timing} {events} ON {ctx.Qualified(owner)}\n    FOR EACH {(def.Row ? "ROW" : "STATEMENT")}{(when is null ? string.Empty : $" WHEN ({when})")} EXECUTE FUNCTION {functionName}();\n";
        return
        [
            new PortableCode(PortableCodeKind.Function, function, functionText) { Language = "plpgsql" },
            new PortableCode(PortableCodeKind.Trigger, target, triggerText) { Language = "plpgsql", Table = ctx.TargetName(owner), TriggerFunction = function },
        ];
    }

    private static bool UsesRowCount(string plsql) => Regex.IsMatch(plsql, @"\bSQL\s*%\s*ROWCOUNT\b", RegexOptions.IgnoreCase);

    /// <summary>AS $$ DECLARE ... BEGIN ... END; $$; with a tag the body does not contain.</summary>
    private static string Dollar(string declarations, string body)
    {
        var tag = body.Contains("$$", StringComparison.Ordinal) || declarations.Contains("$$", StringComparison.Ordinal) ? "$body$" : "$$";
        return $"AS {tag}\n{(declarations.Length == 0 ? string.Empty : "DECLARE\n" + declarations)}BEGIN\n{body}END;\n{tag};\n";
    }
}
