using System.Text.RegularExpressions;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Conversion.Sqlite;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Oracle;

/// <summary>
/// PostgreSQL to Oracle, and the last step of every other source to Oracle. Expressions (defaults, checks, virtual
/// columns, index expressions) and views are printed as Oracle 19c SQL by <see cref="SqlPrinter"/>: no BOOLEAN type,
/// FROM DUAL, OFFSET / FETCH, MINUS. Functions, procedures and triggers are written commented out for now: PL/pgSQL is
/// not translated to PL/SQL yet.
/// </summary>
internal sealed class PgToOracleTranslator(ConversionContext ctx) : ITranslator
{
    public string Expression(string expression, ExpressionSite site) =>
        Translate(expression, site, "NULL", (printer, e) => printer.Expr(e));

    public string Predicate(string predicate, ExpressionSite site) =>
        Translate(predicate, site, "1 = 1", (printer, e) => printer.Cond(e));

    private string Translate(string text, ExpressionSite site, string placeholder, Func<SqlPrinter, PgExpr, string> print)
    {
        var issues = new List<string>();
        string result;
        try
        {
            result = print(new SqlPrinter(ctx, issues) { TargetType = site.TargetType }, new PgParser(text).ExpressionOnly());
        }
        catch (PgParseException ex)
        {
            issues.Add($"could not parse {text}: {ex.Message}");
            result = placeholder + " " + Review.Comment("could not translate", text);
        }

        foreach (var issue in issues.Distinct())
        {
            ctx.Issue(site.Owner, issue);
        }

        return result;
    }

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var issues = new List<string>();
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        string script;
        if (code.Kind == PortableCodeKind.View)
        {
            try
            {
                script = View(code, issues);
            }
            catch (PgParseException ex)
            {
                issues.Add($"could not parse: {ex.Message}");
                script = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for Oracle"));
            }
        }
        else
        {
            // PL/pgSQL to PL/SQL is not translated yet.
            issues.Add($"{code.Kind.ToString().ToLowerInvariant()} not translated; rewrite it for Oracle");
            script = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for Oracle"));
        }

        issues = issues.Distinct().ToList();
        foreach (var issue in issues)
        {
            ctx.Issue(code.Name.ToString(), issue);
        }

        return new TranslatedCode(code, folder, $"{ctx.TargetName(code.Name).Name}.sql", SqliteToPgTranslator.Header("PostgreSQL", code, issues) + script.TrimEnd() + "\n", issues);
    }

    private string View(PortableCode code, List<string> issues)
    {
        var m = Regex.Match(code.Definition, @"\ACREATE\s+(?:OR\s+REPLACE\s+)?(?<mat>MATERIALIZED\s+)?VIEW\s+(?<name>(?:""[^""]*""|[^\s(])+)\s*(?:\((?<cols>[^)]*)\))?\s+AS\s*\n?", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var body = m.Success ? code.Definition[(m.Index + m.Length)..] : code.Definition;
        var check = Regex.Match(body, @"\s+WITH\s+(?:CASCADED\s+|LOCAL\s+)?CHECK\s+OPTION\s*;?\s*\z", RegexOptions.IgnoreCase);
        var query = new PgParser(check.Success ? body[..check.Index] : body).QueryOnly();
        if (m.Success && m.Groups["mat"].Success)
        {
            issues.Add("materialized view became a plain view (rewrite it as CREATE MATERIALIZED VIEW if it must stay materialized)");
        }

        var name = ctx.Qualified(code.Name);
        var columns = m.Success && m.Groups["cols"].Success
            ? " (" + string.Join(", ", m.Groups["cols"].Value.Split(',').Select(c => ctx.Ident(c.Trim().Trim('"')))) + ")"
            : string.Empty;
        return $"CREATE OR REPLACE VIEW {name}{columns} AS\n{new SqlPrinter(ctx, issues).Query(query)}{(check.Success ? "\nWITH CHECK OPTION" : string.Empty)};\n";
    }
}
