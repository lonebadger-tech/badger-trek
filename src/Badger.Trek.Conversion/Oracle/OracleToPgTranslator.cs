using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Oracle;

/// <summary>
/// Oracle to PostgreSQL, and the first step of Oracle to every other target. Placeholder: expressions are kept for review
/// and code objects are written commented out, until the Oracle parser mode exists.
/// </summary>
internal sealed class OracleToPgTranslator(ConversionContext ctx) : IIntermediateTranslator
{
    public string Expression(string expression, ExpressionSite site) => Untranslated(expression, site, "NULL");

    public string Predicate(string predicate, ExpressionSite site) => Untranslated(predicate, site, "true");

    private string Untranslated(string text, ExpressionSite site, string placeholder)
    {
        ctx.Issue(site.Owner, $"Oracle expression not translated: {text}");
        return placeholder + " " + Review.Comment("not translated from Oracle", text);
    }

    public IReadOnlyList<PortableCode> Intermediate(PortableCode code, IReadOnlyList<PortableCode> all) => [];

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var target = ctx.TargetName(code.Name);
        var message = $"Oracle {code.Kind.ToString().ToLowerInvariant()} not translated; rewrite it";
        ctx.Issue(code.Name.ToString(), message);
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        var file = ctx.FlatTarget ? $"{target.Name}.sql" : $"{target.Schema}.{target.Name}.sql";
        return new TranslatedCode(code, folder, file, Review.CommentedOut(code.Definition, Review.Comment(message)) + "\n", [message]);
    }
}
