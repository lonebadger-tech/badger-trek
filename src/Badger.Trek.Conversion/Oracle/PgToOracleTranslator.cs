using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Oracle;

/// <summary>
/// PostgreSQL to Oracle, and the last step of every other source to Oracle. Placeholder: expressions are kept for review
/// and code objects are written commented out, until the PostgreSQL-to-Oracle printer exists.
/// </summary>
internal sealed class PgToOracleTranslator(ConversionContext ctx) : ITranslator
{
    public string Expression(string expression, ExpressionSite site) => Untranslated(expression, site, "NULL");

    public string Predicate(string predicate, ExpressionSite site) => Untranslated(predicate, site, "1 = 1");

    private string Untranslated(string text, ExpressionSite site, string placeholder)
    {
        ctx.Issue(site.Owner, $"expression not translated to Oracle: {text}");
        return placeholder + " " + Review.Comment("not translated to Oracle", text);
    }

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var target = ctx.TargetName(code.Name);
        var message = $"{code.Kind.ToString().ToLowerInvariant()} not translated to Oracle; rewrite it";
        ctx.Issue(code.Name.ToString(), message);
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        return new TranslatedCode(code, folder, $"{target.Name}.sql", Review.CommentedOut(code.Definition, Review.Comment(message)) + "\n", [message]);
    }
}
