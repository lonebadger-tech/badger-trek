using System.Globalization;
using System.Text.RegularExpressions;

namespace Badger.Trek.Conversion;

/// <summary>
/// Column types to and from Oracle. To Oracle the project runs on 19c (oracle.minVersion 19): booleans are NUMBER(1)
/// with a check, JSON is a CLOB with an IS JSON check. From Oracle, a type is first mapped to its PostgreSQL counterpart,
/// and the other targets take that through their own, tested, PostgreSQL mappings.
/// </summary>
public static partial class TypeMapper
{
    private const string OracleBooleanCheck = "{0} IN (0, 1)";

    // ------------------------------------------------------------------ to Oracle

    /// <summary>A PostgreSQL, SQL Server, SQLite or MySQL column type as an Oracle 19c type.</summary>
    public static TypeMapping ToOracle(string type, ConversionContext ctx)
    {
        var t = type.Trim();
        if (t.EndsWith("[]", StringComparison.Ordinal))
        {
            return new("CLOB", "{0} IS JSON", $"array {t} is stored as a JSON array in a CLOB", IsText: true);
        }

        if (ctx.Source == Dialect.PostgreSql && UserTypeRegex().Match(t) is { Success: true } user)
        {
            var schema = Unquote(user.Groups["schema"].Value);
            var typeName = Unquote(user.Groups["name"].Value);
            if (ctx.Enum(schema, typeName) is { } e)
            {
                return OracleEnum(e.Labels);
            }

            if (ctx.Domain(schema, typeName) is { } domain)
            {
                return ToOracle(domain.BaseType, ctx);
            }

            return new("CLOB", null, typeName.Equals("citext", StringComparison.OrdinalIgnoreCase)
                ? "citext becomes CLOB; Oracle compares text case-sensitively"
                : $"type {t} has no Oracle counterpart; stored as text", IsText: true);
        }

        var m = MySqlTypeRegex().Match(t);
        var name = m.Groups["name"].Value.Trim().ToLowerInvariant();
        var args = m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToArray() : [];
        var suffix = m.Groups["suffix"].Value.ToLowerInvariant();
        int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], CultureInfo.InvariantCulture, out var v) ? v : fallback;
        bool Max() => args.Length > 0 && args[0].Equals("max", StringComparison.OrdinalIgnoreCase);
        var unsigned = suffix.Contains("unsigned", StringComparison.Ordinal);
        var mysql = ctx.Source == Dialect.MySql;
        var sqlite = ctx.Source == Dialect.Sqlite;

        switch (name)
        {
            case "tinyint" when mysql && args.Length > 0 && Arg(0, 4) == 1:
            case "bit" when ctx.Source == Dialect.SqlServer || args.Length == 0 || Arg(0, 1) == 1:
            case "boolean" or "bool":
                return new("NUMBER(1)", OracleBooleanCheck, null);
            case "tinyint":
                return new("NUMBER(3)");
            case "smallint" or "int2" or "smallserial" or "serial2" or "year":
                return new(unsigned || name == "year" ? "NUMBER(5)" : "NUMBER(5)");
            case "integer" when sqlite:
                return new("NUMBER(19)");
            case "mediumint":
                return new("NUMBER(8)");
            case "integer" or "int" or "int4" or "serial" or "serial4":
                return new("NUMBER(10)");
            case "bigint" or "int8" or "bigserial" or "serial8":
                return new(unsigned ? "NUMBER(20)" : "NUMBER(19)");
            case "numeric" or "decimal" or "number" when args.Length > 0:
                var precision = Arg(0, 38);
                var scale = Arg(1, 0);
                return precision > 38
                    ? new($"NUMBER(38,{Math.Min(scale, 38)})", null, $"{t}: Oracle numbers have at most 38 digits")
                    : new(scale == 0 ? $"NUMBER({precision})" : $"NUMBER({precision},{scale})");
            case "numeric" or "decimal" or "number":
                return new("NUMBER");
            case "money":
                return new("NUMBER(19,4)");
            case "smallmoney":
                return new("NUMBER(10,4)");
            case "real" or "float4":
                return new(sqlite ? "BINARY_DOUBLE" : "BINARY_FLOAT");
            case "float" when ctx.Source == Dialect.SqlServer && args.Length > 0 && Arg(0, 53) <= 24:
                return new("BINARY_FLOAT");
            case "float" when mysql && args.Length == 0:
                return new("BINARY_FLOAT");
            case "float" or "double" or "double precision" or "float8":
                return new("BINARY_DOUBLE");
            // PostgreSQL's and SQLite's everyday unbounded text: a CLOB could not be compared, grouped or sorted, which views
            // and queries do with names all the time. Explicitly large types (nvarchar(max), longtext, clob) stay CLOBs.
            case "text" or "citext" when ctx.Source is Dialect.PostgreSql or Dialect.Sqlite:
            case "character varying" or "varchar" when args.Length == 0 && ctx.Source is Dialect.PostgreSql or Dialect.Sqlite:
                return new("VARCHAR2(4000 CHAR)", null, $"{t} became VARCHAR2(4000 CHAR) so it can be compared and sorted; a value longer than 4000 bytes fails to load: use CLOB for columns with long text", IsText: true);
            case "character varying" or "varchar" or "nvarchar" or "varchar2" or "nvarchar2":
                return args.Length == 0 || Max() || Arg(0, 4000) > 4000
                    ? new("CLOB", null, args.Length > 0 && !Max() ? $"{t} is longer than an Oracle VARCHAR2 holds (4000); stored as CLOB" : null, IsText: true)
                    : new($"VARCHAR2({Arg(0, 1)} CHAR)", IsText: true);
            case "character" or "char" or "nchar" or "bpchar":
                return Arg(0, 1) > 2000 ? new($"VARCHAR2({Math.Min(Arg(0, 1), 4000)} CHAR)", IsText: true) : new($"CHAR({Arg(0, 1)} CHAR)", IsText: true);
            case "name" or "sysname":
                return new("VARCHAR2(128 CHAR)", IsText: true);
            case "text" or "ntext" or "citext" or "clob" or "tinytext" or "mediumtext" or "longtext" or "\"char\"":
                return new("CLOB", IsText: true);
            case "bytea" or "image" or "blob" or "tinyblob" or "mediumblob" or "longblob":
                return new("BLOB");
            case "binary" or "varbinary" when args.Length > 0 && !Max() && Arg(0, 1) <= 2000:
                return new($"RAW({Arg(0, 1)})");
            case "binary" or "varbinary":
                return new("BLOB");
            case "timestamp" when ctx.Source == Dialect.SqlServer:
            case "rowversion":
                return new("RAW(8)", null, "rowversion becomes RAW(8): Oracle does not stamp rows on update");
            case "uuid" or "uniqueidentifier" or "guid":
                return new("VARCHAR2(36 CHAR)", null, null, IsText: true);
            case "inet" or "cidr":
                return new("VARCHAR2(43 CHAR)", null, $"{name} is stored as text", IsText: true);
            case "date":
                return new("DATE", null, "date becomes DATE, which also holds a time of day (midnight here)");
            case "time" or "timetz":
                return new($"INTERVAL DAY(0) TO SECOND({Math.Min(Arg(0, 6), 9)})", null, "time becomes INTERVAL DAY(0) TO SECOND: Oracle has no time-of-day type");
            case "timestamptz" or "datetimeoffset":
            case "timestamp" when suffix.Contains("with time zone", StringComparison.Ordinal) || mysql:
                return new($"TIMESTAMP({Math.Min(Arg(0, mysql && args.Length == 0 ? 0 : 6), 9)}) WITH TIME ZONE");
            case "timestamp" or "datetime2":
                return new($"TIMESTAMP({Math.Min(Arg(0, ctx.Source == Dialect.SqlServer ? 7 : 6), 9)})");
            case "datetime" when ctx.Source == Dialect.SqlServer:
                return new("TIMESTAMP(3)");
            case "datetime" when mysql:
                return new($"TIMESTAMP({Math.Min(Arg(0, 0), 9)})");
            case "datetime" or "smalldatetime":
                return new("TIMESTAMP(6)");
            case "interval":
                return new("INTERVAL DAY(9) TO SECOND(6)", null, "interval becomes INTERVAL DAY TO SECOND: months and years are not kept");
            case "json" or "jsonb":
                return new("CLOB", "{0} IS JSON", $"{name} is stored as a CLOB with an IS JSON check (Oracle 21c+ has a JSON type)", IsText: true);
            case "xml":
                return new("CLOB", null, "xml is stored as text (Oracle's XMLTYPE could hold it)", IsText: true);
            case "enum" when mysql:
                return OracleEnum(EnumLabels(m.Groups["args"].Value));
            case "set" when mysql:
                return new("VARCHAR2(4000 CHAR)", null, "SET is stored as its comma-separated text; the allowed values are not checked", IsText: true);
            case "" when sqlite:
                return new("CLOB", null, "the column has no declared type, so it may hold values of any type; they are converted to text", IsText: true);
        }

        if (sqlite)
        {
            // SQLite's affinity rules, in its order.
            var upper = t.ToUpperInvariant();
            if (upper.Contains("INT", StringComparison.Ordinal))
            {
                return new("NUMBER(19)");
            }

            if (upper.Contains("CHAR", StringComparison.Ordinal) || upper.Contains("CLOB", StringComparison.Ordinal) || upper.Contains("TEXT", StringComparison.Ordinal))
            {
                return new("CLOB", IsText: true);
            }

            if (upper.Contains("BLOB", StringComparison.Ordinal))
            {
                return new("BLOB");
            }

            if (upper.Contains("REAL", StringComparison.Ordinal) || upper.Contains("FLOA", StringComparison.Ordinal) || upper.Contains("DOUB", StringComparison.Ordinal))
            {
                return new("BINARY_DOUBLE");
            }

            return new("NUMBER", null, $"declared type {t} has NUMERIC affinity: values may be integers or reals; check the precision");
        }

        return new("CLOB", null, $"type {t} has no Oracle counterpart; stored as text", IsText: true);
    }

    private static TypeMapping OracleEnum(IReadOnlyList<string> labels)
    {
        var length = Math.Max(1, labels.Count == 0 ? 1 : labels.Max(l => l.Length));
        return new($"VARCHAR2({length} CHAR)", $"{{0}} IN ({string.Join(", ", labels.Select(ConversionContext.OracleLiteral))})", null, IsText: true);
    }

    // ------------------------------------------------------------------ from Oracle

    /// <summary>An Oracle column type (as the exporter writes it: VARCHAR2(20 CHAR), NUMBER(12,2) ...) in the target.</summary>
    public static TypeMapping FromOracle(string type, ConversionContext ctx)
    {
        var pg = OracleToPgType(type);
        return ctx.Target switch
        {
            Dialect.PostgreSql => pg,
            Dialect.SqlServer => Annotate(PgToTSql(pg.Type, ctx), pg),
            Dialect.Sqlite => Annotate(ToSqlite(pg.Type, ctx), pg),
            Dialect.MySql => Annotate(ToMySql(pg.Type, ctx), pg),
            _ => pg,
        };

        // A note from the first step (Oracle → PostgreSQL) stays with the final mapping.
        static TypeMapping Annotate(TypeMapping mapped, TypeMapping first) =>
            first.Note is null ? mapped : mapped with { Note = mapped.Note is null ? first.Note : first.Note + "; " + mapped.Note };
    }

    /// <summary>The PostgreSQL counterpart of an Oracle type.</summary>
    public static TypeMapping OracleToPgType(string type)
    {
        var t = type.Trim();
        var m = OracleTypeRegex().Match(t);
        var name = m.Success ? m.Groups["name"].Value.ToUpperInvariant() : t.ToUpperInvariant();
        var args = m.Success && m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToArray() : [];
        var suffix = m.Success ? m.Groups["suffix"].Value.ToUpperInvariant() : string.Empty;
        int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i].Split(' ')[0], CultureInfo.InvariantCulture, out var v) ? v : fallback;

        switch (name)
        {
            case "NUMBER" when args.Length == 0:
                return new("numeric");
            case "NUMBER" when Arg(1, 0) == 0 && args[0] != "*":
                var digits = Arg(0, 38);
                return digits switch
                {
                    <= 4 => new("smallint"),
                    <= 9 => new("integer"),
                    // NUMBER(19) is how bigint is declared in Oracle (and what an identity usually is).
                    <= 19 => new("bigint", null, digits == 19 ? "NUMBER(19) becomes bigint, which holds up to 9223372036854775807" : null),
                    _ => new($"numeric({digits})"),
                };
            case "NUMBER":
                return new($"numeric({(args[0] == "*" ? 38 : Arg(0, 38))},{Arg(1, 0)})");
            case "INTEGER" or "INT" or "SMALLINT":
                return new("numeric(38)", null, "INTEGER is NUMBER(38) in Oracle; check whether bigint holds the values");
            case "FLOAT" or "BINARY_DOUBLE" or "DOUBLE PRECISION":
                return new("double precision");
            case "BINARY_FLOAT" or "REAL":
                return new("real");
            case "VARCHAR2" or "NVARCHAR2" or "VARCHAR":
                return new($"varchar({Arg(0, 4000)})", null, args.Length > 0 && args[0].EndsWith("BYTE", StringComparison.OrdinalIgnoreCase) ? $"{t} is a length in bytes; the target counts characters" : null, IsText: true);
            case "CHAR" or "NCHAR":
                return new($"char({Arg(0, 1)})", IsText: true);
            case "CLOB" or "NCLOB" or "LONG":
                return new("text", IsText: true);
            case "BLOB" or "RAW" or "LONG RAW" or "BFILE":
                return new("bytea", null, name == "BFILE" ? "BFILE points at a file outside the database; only the column is converted" : null);
            case "DATE":
                return new("timestamp(0)", null, "Oracle DATE holds a time of day too, so it becomes timestamp(0)");
            case "TIMESTAMP" when suffix.Contains("LOCAL", StringComparison.Ordinal):
            case "TIMESTAMP" when suffix.Contains("TIME ZONE", StringComparison.Ordinal):
                return new($"timestamp({Math.Min(Arg(0, 6), 6)}) with time zone");
            case "TIMESTAMP":
                return new($"timestamp({Math.Min(Arg(0, 6), 6)})", null, Arg(0, 6) > 6 ? "fractions of a second beyond microseconds are cut" : null);
            case "INTERVAL":
                return new("interval");
            case "JSON":
                return new("jsonb");
            case "BOOLEAN":
                return new("boolean");
            case "XMLTYPE":
                return new("xml");
            case "ROWID" or "UROWID":
                return new("varchar(4000)", null, "ROWID values are only meaningful in the source database", IsText: true);
        }

        return new("text", null, $"Oracle type {t} has no direct counterpart; stored as text", IsText: true);
    }

    [GeneratedRegex(@"^(?<name>LONG\s+RAW|DOUBLE\s+PRECISION|INTERVAL|[A-Za-z_][A-Za-z0-9_]*)\s*(?:\((?<args>[^)]*)\))?(?<suffix>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex OracleTypeRegex();
}
