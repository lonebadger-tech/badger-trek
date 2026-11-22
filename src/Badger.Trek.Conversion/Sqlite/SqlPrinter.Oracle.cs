using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Sqlite;

/// <summary>
/// The Oracle side of the printer: PostgreSQL written as Oracle 19c SQL, and Oracle (views as ALL_VIEWS.TEXT gives them,
/// DATA_DEFAULT and SEARCH_CONDITION expressions) read in Oracle mode written as PostgreSQL. Oracle 19c has no boolean
/// values in SQL: a condition used as a value becomes CASE WHEN ... THEN 1 ELSE 0 END, and a boolean column (NUMBER(1))
/// used as a condition becomes column = 1. Oracle's '' is NULL and its || skips NULLs: both are flagged.
/// </summary>
internal sealed partial class SqlPrinter
{
    /// <summary>The type the printed expression lands in, in the target dialect, when known (a column default).</summary>
    public string? TargetType { get; init; }

    // ------------------------------------------------------------------ PostgreSQL → Oracle: conditions and booleans

    private static bool IsCondition(PgExpr e) => e switch
    {
        PgBinary { Op: "=" or "<>" or "<" or ">" or "<=" or ">=" or "and" or "or" } => true,
        PgUnary { Op: "not" } => true,
        PgIs or PgIsDistinct or PgBetween or PgInList or PgInQuery or PgLike or PgExists or PgQuantified => true,
        _ => false,
    };

    /// <summary>The condition can be neither true nor false (NULL): only then does the value form need a NULL branch.</summary>
    private bool CanBeUnknown(PgExpr e) => Unparen(e) switch
    {
        PgIs or PgIsDistinct or PgExists => false,
        PgBinary { Op: "and" or "or" } b => CanBeUnknown(b.Left) || CanBeUnknown(b.Right),
        PgBinary b => MaybeNull(b.Left) || MaybeNull(b.Right),
        PgUnary { Op: "not" } u => CanBeUnknown(u.Operand),
        PgLike l => MaybeNull(l.Operand) || MaybeNull(l.Pattern),
        PgBetween b => MaybeNull(b.Operand) || MaybeNull(b.Low) || MaybeNull(b.High),
        PgInList i => MaybeNull(i.Operand) || i.Values.Any(MaybeNull),
        PgName n => MaybeNull(n),
        _ => true,
    };

    /// <summary>A condition as an Oracle value: 1 when true, 0 when false, NULL when unknown.</summary>
    private string BooleanValue(PgExpr e)
    {
        var condition = ExprCore(e);
        return CanBeUnknown(e)
            ? $"CASE WHEN {condition} THEN 1 WHEN NOT ({condition}) THEN 0 END"
            : $"CASE WHEN {condition} THEN 1 ELSE 0 END";
    }

    private string OracleCondition(PgExpr e) => e switch
    {
        PgCall { Function.Parts: ["("] } p => "(" + OracleCondition(p.Args[0]) + ")",
        PgLiteral { Kind: LiteralKind.True } => "1 = 1",
        PgLiteral { Kind: LiteralKind.False or LiteralKind.Null } => "1 = 0",
        _ when IsCondition(e) => ExprCore(e),

        // A boolean column (NUMBER(1)), or anything else that gives a boolean in PostgreSQL.
        _ => $"{Expr(e)} = 1",
    };

    /// <summary>x IS [NOT] TRUE / FALSE / UNKNOWN.</summary>
    private string IsTruthToOracle(PgIs i)
    {
        var not = i.Not ? "NOT " : string.Empty;
        if (IsCondition(Unparen(i.Operand)))
        {
            var c = Cond(i.Operand);
            return (i.What, i.Not) switch
            {
                ("true", false) => c,
                ("false", false) => $"NOT ({c})",
                ("true", true) => $"CASE WHEN {c} THEN 1 ELSE 0 END = 0",
                ("false", true) => $"CASE WHEN NOT ({c}) THEN 1 ELSE 0 END = 0",
                _ => $"CASE WHEN {c} THEN 1 WHEN NOT ({c}) THEN 0 END IS {not}NULL",
            };
        }

        var v = Expr(i.Operand);
        return (i.What, i.Not) switch
        {
            ("true", false) => $"{v} = 1",
            ("false", false) => $"{v} = 0",
            ("true", true) => $"NVL({v}, 0) <> 1",
            ("false", true) => $"NVL({v}, 1) <> 0",
            _ => $"{v} IS {not}NULL",
        };
    }

    // ------------------------------------------------------------------ PostgreSQL → Oracle: operators

    private string LikeToOracle(PgLike l)
    {
        // x LIKE ANY (ARRAY['a%', 'b%']): one LIKE per pattern, OR'd (ALL: AND'd).
        if (l.Pattern is PgCall { Function.Last: var quantifier, Args: [var list] } && quantifier.ToLowerInvariant() is "any" or "all"
            && (Unparen(list) is PgCast { Operand: var inner } ? Unparen(inner) : Unparen(list)) is PgArray { Elements.Count: > 0 } array)
        {
            var join = quantifier.Equals("any", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
            return "(" + string.Join(join, array.Elements.Select(p => LikeToOracle(l with { Pattern = p }))) + ")";
        }

        var not = l.Not ? "NOT " : string.Empty;

        // PostgreSQL's LIKE escapes with a backslash by default; Oracle's has no escape character unless told.
        var escape = l.Escape is not null ? $" ESCAPE {Expr(l.Escape)}"
            : StringArg(l.Pattern)?.Contains('\\', StringComparison.Ordinal) == true ? " ESCAPE '\\'"
            : string.Empty;
        return l.Op switch
        {
            "like" => $"{Expr(l.Operand)} {not}LIKE {Expr(l.Pattern)}{escape}",
            "ilike" => $"UPPER({Expr(l.Operand)}) {not}LIKE UPPER({Expr(l.Pattern)}){escape}",
            "~" => $"{not}REGEXP_LIKE({Expr(l.Operand)}, {Expr(l.Pattern)})",
            "~*" => $"{not}REGEXP_LIKE({Expr(l.Operand)}, {Expr(l.Pattern)}, 'i')",
            _ => Unsupported($"{l.Op.ToUpperInvariant()} has no Oracle counterpart", $"{Show(l.Operand)} {l.Op} {Show(l.Pattern)}", "1 = 1"),
        };
    }

    private string BinaryToOracle(PgBinary b)
    {
        if (b.Op is "+" or "-" && MySqlIntervalParts(b.Right) is { } right)
        {
            return IntervalArithmetic(Expr(b.Left), right, b.Op == "-");
        }

        if (b.Op == "+" && MySqlIntervalParts(b.Left) is { } left)
        {
            return IntervalArithmetic(Expr(b.Right), left, false);
        }

        if (b.Op is "+" or "-" && (IsIntervalValue(b.Left) || IsIntervalValue(b.Right)))
        {
            return Unsupported("interval arithmetic not translated (only literal intervals and n * interval are)", Show(b));
        }

        if (b.Op is "->" or "->>" or "#>" or "#>>")
        {
            return JsonToOracle(b);
        }

        if (b.Op == "||")
        {
            var parts = ConcatParts(b).ToList();
            var text = string.Join(" || ", parts.Select(Expr));
            return parts.Any(p => StringArg(p) is null && MaybeNull(p))
                ? text + Note("Oracle's || treats NULL as an empty string; PostgreSQL's || gives NULL")
                : text;
        }

        return b.Op switch
        {
            "and" or "or" => $"{Cond(b.Left)} {b.Op.ToUpperInvariant()} {Cond(b.Right)}",

            // PostgreSQL divides integers as integers; Oracle's NUMBER division keeps the fraction.
            "/" when IsInteger(b.Left) && IsInteger(b.Right) => $"TRUNC({Expr(b.Left)} / {Expr(b.Right)})",
            "%" => $"MOD({Expr(b.Left)}, {Expr(b.Right)})",
            "^" => $"POWER({Expr(b.Left)}, {Expr(b.Right)})",
            "&" => $"BITAND({Expr(b.Left)}, {Expr(b.Right)})",
            "#" or "|" or "<<" or ">>" or "@>" or "<@" or "?" or "?|" or "?&" or "&&" or "@@" or "-|-" => Unsupported($"operator {b.Op} has no Oracle counterpart", Show(b)),
            _ => $"{Expr(b.Left)} {b.Op} {Expr(b.Right)}",
        };
    }

    /// <summary>
    /// x ± interval: days to seconds as INTERVAL 'n' DAY (NUMTODSINTERVAL for computed or large amounts), months and years
    /// with ADD_MONTHS, which, unlike adding INTERVAL '1' MONTH, does not fail on the 31st.
    /// </summary>
    private string IntervalArithmetic(string value, List<(string Amount, string Unit)> parts, bool negate)
    {
        var result = value;
        var months = false;
        foreach (var (computed, unit) in parts)
        {
            // n * interval '1 day' with a literal n: (30) is 30.
            var amount = Regex.Match(computed, @"^\((?<n>-?\d+(\.\d+)?)\)$") is { Success: true } m ? m.Groups["n"].Value : computed;
            switch (unit)
            {
                case "YEAR" or "MONTH":
                {
                    var n = unit == "YEAR" ? Multiply(amount, 12) : amount;
                    result = $"ADD_MONTHS({result}, {(negate ? Negate(n) : n)})";
                    months = true;
                    break;
                }

                default:
                {
                    var (n, u) = unit switch
                    {
                        "WEEK" => (Multiply(amount, 7), "DAY"),
                        "MICROSECOND" => (IsNumber(amount) ? (decimal.Parse(amount, CultureInfo.InvariantCulture) / 1000000).ToString(CultureInfo.InvariantCulture) : $"({amount}) / 1000000", "SECOND"),
                        _ => (amount, unit),
                    };
                    result = $"{result} {(negate ? "-" : "+")} {DaySecondInterval(n, u)}";
                    break;
                }
            }
        }

        return months ? result + Note("ADD_MONTHS keeps a month-end date at the end of the month and returns a DATE; PostgreSQL's month arithmetic does neither") : result;
    }

    private static bool IsNumber(string text) => Regex.IsMatch(text, @"^[-+]?\d+(\.\d+)?$");

    private static string Multiply(string amount, int factor) =>
        IsNumber(amount) ? (decimal.Parse(amount, CultureInfo.InvariantCulture) * factor).ToString(CultureInfo.InvariantCulture) : $"{amount} * {factor}";

    private static string Negate(string amount) => IsNumber(amount) ? (-decimal.Parse(amount, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture) : $"-({amount})";

    /// <summary>INTERVAL 'n' DAY for a whole literal amount within the default precision (two digits), NUMTODSINTERVAL otherwise.</summary>
    private static string DaySecondInterval(string amount, string unit) =>
        IsNumber(amount) && Math.Abs(decimal.Parse(amount, CultureInfo.InvariantCulture)) < 100 && (unit == "SECOND" || !amount.Contains('.', StringComparison.Ordinal))
            ? $"INTERVAL '{amount}' {unit}"
            : $"NUMTODSINTERVAL({amount}, '{unit}')";

    /// <summary>A PostgreSQL interval literal as an Oracle interval, or null when it mixes months and days.</summary>
    private string OracleIntervalLiteral(string text)
    {
        var parts = ParseIntervalText(text);
        if (parts is null)
        {
            return Unsupported("interval not translated", $"interval '{text}'");
        }

        var yearMonth = parts.All(p => p.Unit is "YEAR" or "MONTH");
        if (!yearMonth && parts.Any(p => p.Unit is "YEAR" or "MONTH"))
        {
            return Unsupported("an interval of months and days has no single Oracle type", $"interval '{text}'");
        }

        var values = parts.Select(p => p.Unit switch
        {
            "YEAR" or "MONTH" => IsNumber(p.Amount) && !p.Amount.Contains('.', StringComparison.Ordinal) && Math.Abs(decimal.Parse(p.Amount, CultureInfo.InvariantCulture)) < 100
                ? $"INTERVAL '{p.Amount}' {p.Unit}"
                : $"NUMTOYMINTERVAL({p.Amount}, '{p.Unit}')",
            "WEEK" => DaySecondInterval(Multiply(p.Amount, 7), "DAY"),
            "MICROSECOND" => DaySecondInterval((decimal.Parse(p.Amount, CultureInfo.InvariantCulture) / 1000000).ToString(CultureInfo.InvariantCulture), "SECOND"),
            _ => DaySecondInterval(p.Amount, p.Unit),
        }).ToList();
        return values.Count == 1 ? values[0] : "(" + string.Join(" + ", values) + ")";
    }

    /// <summary>PostgreSQL's JSON operators as JSON_VALUE (text) and JSON_QUERY (JSON); a chain becomes one path.</summary>
    private string JsonToOracle(PgBinary b)
    {
        var path = new List<string>();
        PgExpr root = b;
        while (Unparen(root) is PgBinary { Op: "->" or "->>" or "#>" or "#>>" } step
               && (ReferenceEquals(step, b) || step.Op is "->" or "#>")
               && JsonSegments(step) is { } segments)
        {
            path.InsertRange(0, segments);
            root = step.Left;
        }

        if (ReferenceEquals(root, b))
        {
            return Unsupported("JSON key or path that is not a literal (Oracle's JSON paths must be literals)", Show(b));
        }

        var jsonPath = Lit("$" + string.Concat(path));
        return b.Op is "->>" or "#>>"
            ? $"JSON_VALUE({Expr(root)}, {jsonPath})"
            : $"JSON_QUERY({Expr(root)}, {jsonPath})" + Note("-> became JSON_QUERY(), which gives NULL for a scalar (number, string); use JSON_VALUE for those");
    }

    private string OracleExtract(string field, PgExpr source)
    {
        var s = Expr(source);

        // Oracle's EXTRACT gives the UTC fields of a timestamp with time zone; PostgreSQL's those in the session's time zone.
        var zoned = Unparen(source) is PgCall { Function.Last: "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" or "clock_timestamp" }
            || ColumnTypes(source, t => t.Contains("with time zone", StringComparison.Ordinal) || t.StartsWith("timestamptz", StringComparison.Ordinal));
        s = zoned ? $"CAST(({s} AT LOCAL) AS TIMESTAMP)" : s;

        // Oracle extracts hours, minutes and seconds from a TIMESTAMP only, not from a DATE.
        var ts = IsTimestamp(source) || zoned ? s : $"CAST({s} AS TIMESTAMP)";
        return field switch
        {
            "year" or "month" or "day" => $"EXTRACT({field.ToUpperInvariant()} FROM {s})",
            "hour" or "minute" or "second" => $"EXTRACT({field.ToUpperInvariant()} FROM {ts})",
            "milliseconds" => $"(EXTRACT(SECOND FROM {ts}) * 1000)",
            "microseconds" => $"(EXTRACT(SECOND FROM {ts}) * 1000000)",

            // TO_CHAR(x, 'D') depends on NLS_TERRITORY; counting from the ISO week's Monday does not.
            "dow" => $"MOD(TRUNC({s}) - TRUNC({s}, 'IW') + 1, 7)",
            "isodow" => $"(TRUNC({s}) - TRUNC({s}, 'IW') + 1)",
            "doy" => $"TO_NUMBER(TO_CHAR({s}, 'DDD'))",
            "week" => $"TO_NUMBER(TO_CHAR({s}, 'IW'))",
            "quarter" => $"TO_NUMBER(TO_CHAR({s}, 'Q'))",
            "isoyear" => $"TO_NUMBER(TO_CHAR({s}, 'IYYY'))",
            "epoch" when zoned => $"ROUND((CAST(SYS_EXTRACT_UTC({Expr(source)}) AS DATE) - DATE '1970-01-01') * 86400)" + Note("epoch is computed without fractional seconds"),
            "epoch" => $"ROUND((CAST({s} AS DATE) - DATE '1970-01-01') * 86400)" + Note("epoch is computed from the date and time as written, without fractional seconds or time zone"),
            _ => Unsupported($"date field {field} not translated", $"extract({field} ...)"),
        };
    }

    private string AtTimeZoneToOracle(PgAtTimeZone z)
    {
        var utc = StringArg(z.Zone)?.Equals("utc", StringComparison.OrdinalIgnoreCase) == true;
        if (utc && Unparen(z.Operand) is PgCall { Function.Last: "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" or "clock_timestamp" })
        {
            return "SYS_EXTRACT_UTC(SYSTIMESTAMP)";
        }

        if (utc)
        {
            return $"SYS_EXTRACT_UTC({Expr(z.Operand)})" + Note("AT TIME ZONE 'utc' became SYS_EXTRACT_UTC(): right for a timestamp with time zone; a timestamp without one is taken as the session's time zone");
        }

        return $"CAST(({Expr(z.Operand)} AT TIME ZONE {Expr(z.Zone)}) AS TIMESTAMP)"
            + Note("check AT TIME ZONE: on a timestamp without time zone, PostgreSQL's works the other way round");
    }

    // ------------------------------------------------------------------ PostgreSQL → Oracle: casts and literals

    /// <summary>The Oracle CAST target for a PostgreSQL type and what kind of value it is, or null when there is none.</summary>
    private (string Type, string Kind)? OracleCastType(string pgType, int depth = 0)
    {
        var t = pgType.Trim().ToLowerInvariant();
        if (t.EndsWith("[]", StringComparison.Ordinal) || depth > 5)
        {
            return null;
        }

        var tz = t.Contains("with time zone", StringComparison.Ordinal) && !t.Contains("without", StringComparison.Ordinal);
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? t : t[..paren]).Replace(" without time zone", string.Empty, StringComparison.Ordinal).Replace(" with time zone", string.Empty, StringComparison.Ordinal).Trim();
        var args = paren < 0 ? string.Empty : t[(paren + 1)..t.IndexOf(')', paren)].Replace(" ", string.Empty, StringComparison.Ordinal);
        var dot = name.LastIndexOf('.');
        var schema = dot < 0 ? ctx.Schema.DefaultSchema : name[..dot].Trim('"');
        var bare = (dot < 0 ? name : name[(dot + 1)..]).Trim('"');
        if (ctx.Enum(schema, bare) is not null || bare == "citext")
        {
            return ("VARCHAR2(4000)", "text");
        }

        if (ctx.Domain(schema, bare) is { } domain)
        {
            return OracleCastType(domain.BaseType, depth + 1);
        }

        _ = int.TryParse(args.Split(',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length);
        return bare switch
        {
            "smallint" or "int2" => ("NUMBER(5)", "number"),
            "integer" or "int" or "int4" => ("NUMBER(10)", "number"),
            "bigint" or "int8" => ("NUMBER(19)", "number"),
            "numeric" or "decimal" => (args.Length > 0 ? $"NUMBER({args})" : "NUMBER", "number"),
            "real" or "float4" => ("BINARY_FLOAT", "number"),
            "double precision" or "float8" or "float" => ("BINARY_DOUBLE", "number"),
            "text" or "name" => ("VARCHAR2(4000)", "text"),
            "varchar" or "character varying" => (length is > 0 and <= 4000 ? $"VARCHAR2({length} CHAR)" : "VARCHAR2(4000)", "text"),
            "char" or "character" or "bpchar" => ($"CHAR({(length is > 0 and <= 2000 ? length : 1)} CHAR)", "text"),
            "uuid" => ("VARCHAR2(36)", "text"),
            "date" => ("DATE", "date"),
            "timestamp" or "timestamptz" => ((args.Length > 0 ? $"TIMESTAMP({args})" : "TIMESTAMP") + (tz || bare == "timestamptz" ? " WITH TIME ZONE" : string.Empty), "timestamp"),
            "json" or "jsonb" => ("CLOB", "json"),
            "bytea" => ("RAW(2000)", "raw"),
            _ => null,
        };
    }

    private bool IsEnumColumnOrText(PgExpr e) =>
        Unparen(e) is PgName n && ColumnsNamed(n.Last) is { Count: > 0 } columns && columns.All(c =>
        {
            if (ctx.KindOf(c.Type) == ConversionContext.ColumnKind.Text)
            {
                return true;
            }

            var parts = c.Type.Split('.');
            return ctx.Enum(parts.Length > 1 ? parts[^2].Trim('"') : ctx.Schema.DefaultSchema, parts[^1].Trim('"')) is not null;
        });

    private string CastToOracle(PgCast c)
    {
        var type = c.Type.Trim().ToLowerInvariant();
        if (type is "regclass" or "regproc" or "regtype" or "oid" or "name" && c.Operand is PgLiteral)
        {
            return Expr(c.Operand);
        }

        if (type == "interval")
        {
            return StringArg(c.Operand) is { } interval ? OracleIntervalLiteral(interval) : Unsupported("an interval value not translated (only literals and date arithmetic are)", Show(c));
        }

        if (type is "boolean" or "bool")
        {
            return Unparen(c.Operand) switch
            {
                PgLiteral { Kind: LiteralKind.Number } n => n.Value.Trim('0', '.', '-').Length == 0 ? "0" : "1",
                var o when StringArg(o) is { } s => s.Trim().ToLowerInvariant() is "t" or "true" or "y" or "yes" or "on" or "1" ? "1" : "0",
                var o when IsBooleanColumn(o) || IsCondition(o) => Expr(c.Operand),

                // An integer as a boolean: 0 is false, anything else true, NULL stays NULL.
                _ => $"SIGN(ABS({Expr(c.Operand)}))",
            };
        }

        if (type.StartsWith("time", StringComparison.Ordinal) && !type.StartsWith("timestamp", StringComparison.Ordinal))
        {
            return StringArg(c.Operand) is { } time
                ? Lit(time) + Note("Oracle has no time type: the time is kept as text")
                : Unsupported("Oracle has no time type", Show(c), $"TO_CHAR({Expr(c.Operand)}, 'HH24:MI:SS')");
        }

        if (OracleCastType(c.Type) is not { } castType)
        {
            return Unsupported($"a cast to {c.Type} has no Oracle counterpart", Show(c));
        }

        var (target, kind) = castType;

        // A string literal, or one cast to a text type ('x'::character varying); not a typed value ('...'::timestamp).
        var literal = StringArg(c.Operand) is { } text && (Unparen(c.Operand) is PgLiteral || (Unparen(c.Operand) is PgCast inner && OracleCastType(inner.Type)?.Kind == "text")) ? text : null;
        switch (kind)
        {
            case "text":
                if (literal is not null)
                {
                    return StringLiteral(literal);
                }

                if (IsEnumColumnOrText(c.Operand))
                {
                    return Expr(c.Operand);
                }

                return $"CAST({Expr(c.Operand)} AS {target})"
                    + (IsDate(c.Operand) || IsTimestamp(c.Operand) ? Note("a date as text follows NLS_DATE_FORMAT / NLS_TIMESTAMP_FORMAT in Oracle; use TO_CHAR with a format") : string.Empty);
            case "number" when Unparen(c.Operand) is PgLiteral { Kind: LiteralKind.Number } number:
                return number.Value;
            case "number" when literal is not null && IsNumber(literal.Trim()):
                return literal.Trim();
            case "date" when literal is not null && Regex.IsMatch(literal.Trim(), @"^\d{4}-\d{2}-\d{2}$"):
                return $"DATE {Lit(literal.Trim())}";
            case "date" when literal is not null || ctx.ColumnKindOf((Unparen(c.Operand) as PgName)?.Last ?? string.Empty) == ConversionContext.ColumnKind.Text:
                return $"TO_DATE({Expr(c.Operand)}, 'YYYY-MM-DD')";
            case "date":
                // PostgreSQL's ::date drops the time of day; Oracle's CAST AS DATE keeps it.
                return IsDate(c.Operand) ? Expr(c.Operand) : $"TRUNC({Expr(c.Operand)})";
            case "timestamp" when literal is not null:
                return TypedLiteralToOracle(new PgTypedLiteral(c.Type, literal));
            case "json":
                return literal is not null ? Lit(literal) : Expr(c.Operand);
            case "raw" when literal is not null && literal.StartsWith("\\x", StringComparison.OrdinalIgnoreCase):
                return $"HEXTORAW({Lit(literal[2..])})";
            case "raw":
                return Unsupported("a cast to bytea not translated", Show(c));
            default:
                return $"CAST({Expr(c.Operand)} AS {target})";
        }
    }

    private string TypedLiteralToOracle(PgTypedLiteral t)
    {
        var type = t.Type.ToLowerInvariant();
        if (type == "interval")
        {
            return OracleIntervalLiteral(t.Value);
        }

        if (type == "date")
        {
            return $"DATE {Lit(t.Value.Trim())}";
        }

        if (type.StartsWith("timestamp", StringComparison.Ordinal))
        {
            // Oracle's literal wants 'YYYY-MM-DD HH24:MI:SS[.FF][ +HH:MI]'.
            var value = Regex.Replace(t.Value.Trim(), @"^(\d{4}-\d{2}-\d{2})T", "$1 ");
            value = Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$") ? value + " 00:00:00" : value;
            value = Regex.Replace(value, @"([+-]\d{2})$", "$1:00");
            return $"TIMESTAMP {Lit(value)}";
        }

        if (type.StartsWith("time", StringComparison.Ordinal))
        {
            return Lit(t.Value) + Note("Oracle has no time type: the time is kept as text");
        }

        if (type == "bytea" && t.Value.StartsWith("\\x", StringComparison.OrdinalIgnoreCase))
        {
            return $"HEXTORAW({Lit(t.Value[2..])})";
        }

        return CastToOracle(new PgCast(new PgLiteral(LiteralKind.String, t.Value), t.Type));
    }

    // ------------------------------------------------------------------ PostgreSQL → Oracle: functions

    /// <summary>PostgreSQL functions in Oracle.</summary>
    private string ToOracle(string name, List<PgExpr> args, PgCall c)
    {
        string A(int i) => Expr(args[i]);
        string All() => string.Join(", ", args.Select(Expr));
        switch (name)
        {
            case "now" or "current_timestamp" or "transaction_timestamp" or "statement_timestamp" or "clock_timestamp":
                return "SYSTIMESTAMP";
            case "localtimestamp":
                return "LOCALTIMESTAMP";
            case "current_date":
                return "TRUNC(SYSDATE)";
            case "current_time" or "localtime":
                return Unsupported("Oracle has no time type", name, "TO_CHAR(SYSTIMESTAMP, 'HH24:MI:SS')");
            case "count" or "sum" or "avg" or "min" or "max" or "stddev" or "variance" or "stddev_samp" or "stddev_pop" or "var_samp" or "var_pop":
                return Aggregate(name.ToUpperInvariant(), c, args);
            case "bool_and" or "every":
                return Aggregate("MIN", c, args);
            case "bool_or":
                return Aggregate("MAX", c, args);
            case "string_agg":
                return ListAgg(c, args);
            case "array_agg" or "json_agg" or "jsonb_agg":
                return Aggregate("JSON_ARRAYAGG", c, args) + (name == "array_agg" ? Note("array_agg() became JSON_ARRAYAGG(): a JSON array") : string.Empty);
            case "json_object_agg" or "jsonb_object_agg" when args.Count == 2:
                return $"JSON_OBJECTAGG(KEY {A(0)} VALUE {A(1)})";
            case "coalesce" or "nullif" or "lower" or "upper" or "replace" or "abs" or "round" or "floor" or "sign" or "sqrt" or "exp" or "ln" or "sin" or "cos"
                or "tan" or "asin" or "acos" or "atan" or "atan2" or "mod" or "ascii" or "initcap" or "translate" or "chr" or "lpad" or "rpad" or "trunc"
                or "row_number" or "rank" or "dense_rank" or "ntile" or "lag" or "lead" or "first_value" or "last_value" or "nth_value" or "percent_rank" or "cume_dist":
                return $"{name.ToUpperInvariant()}({All()})";
            case "greatest" or "least":
                return $"{name.ToUpperInvariant()}({All()})" + Note($"Oracle's {name.ToUpperInvariant()}() is NULL when any argument is NULL; PostgreSQL's {name}() skips NULLs");
            case "ceil" or "ceiling":
                return $"CEIL({A(0)})";
            case "power" or "pow":
                return $"POWER({A(0)}, {A(1)})";
            case "log":
                return args.Count == 1 ? $"LOG(10, {A(0)})" : $"LOG({A(0)}, {A(1)})";
            case "log10":
                return $"LOG(10, {A(0)})";
            case "cbrt":
                return $"POWER({A(0)}, 1 / 3)";
            case "div":
                return $"TRUNC({A(0)} / {A(1)})";
            case "pi":
                return "ACOS(-1)";
            case "degrees":
                return $"({A(0)} * 180 / ACOS(-1))";
            case "radians":
                return $"({A(0)} * ACOS(-1) / 180)";
            case "random":
                return "DBMS_RANDOM.VALUE";
            case "length" or "char_length" or "character_length":
                return $"LENGTH({A(0)})";
            case "octet_length":
                return $"LENGTHB({A(0)})";
            case "substring" or "substr":
                return args.Count > 1 && Unparen(args[1]) is PgLiteral { Kind: LiteralKind.Number } start && decimal.TryParse(start.Value, CultureInfo.InvariantCulture, out var from) && from <= 0
                    ? $"SUBSTR({All()})" + Note("a start position of 0 or less counts differently in Oracle's SUBSTR (0 is 1, negative counts from the end)")
                    : $"SUBSTR({All()})";
            case "strpos":
                return $"INSTR({A(0)}, {A(1)})";
            case "left":
                return $"SUBSTR({A(0)}, 1, {A(1)})";
            case "right":
                return $"SUBSTR({A(0)}, GREATEST(LENGTH({A(0)}) - ({A(1)}) + 1, 1))";
            case "btrim":
                // Oracle's LTRIM and RTRIM remove a set of characters, like PostgreSQL's.
                return args.Count == 1 ? $"TRIM({A(0)})" : $"LTRIM(RTRIM({A(0)}, {A(1)}), {A(1)})";
            case "ltrim" or "rtrim":
                return $"{name.ToUpperInvariant()}({All()})";
            case "concat":
                // PostgreSQL's concat() skips NULLs, and so does Oracle's ||.
                return args.Count == 1 ? A(0) : "(" + string.Join(" || ", args.Select(Expr)) + ")";
            case "concat_ws" when args.Count > 1:
                return "(" + string.Join($" || {A(0)} || ", args.Skip(1).Select(Expr)) + ")"
                    + Note("concat_ws() leaves out NULL arguments and their separator; here a NULL leaves its separator in");
            case "repeat":
                return $"RPAD({A(0)}, LENGTH({A(0)}) * ({A(1)}), {A(0)})";
            case "reverse":
                return $"REVERSE({A(0)})" + Note("REVERSE() is not documented in Oracle");
            case "md5":
                return $"LOWER(RAWTOHEX(STANDARD_HASH({A(0)}, 'MD5')))";
            case "regexp_replace":
                return RegexpReplaceToOracle(args, c);
            case "gen_random_uuid" or "uuid_generate_v4":
                return TargetType?.Trim().StartsWith("raw", StringComparison.OrdinalIgnoreCase) == true
                    ? "SYS_GUID()"
                    : @"LOWER(REGEXP_REPLACE(RAWTOHEX(SYS_GUID()), '(.{8})(.{4})(.{4})(.{4})(.{12})', '\1-\2-\3-\4-\5'))"
                        + Note("SYS_GUID() is unique but not a random (version 4) UUID");
            case "date_trunc" when StringArg(args[0]) is { } unit:
                return DateTruncToOracle(unit.ToLowerInvariant(), args[1], c);
            case "date_part" when StringArg(args[0]) is { } field:
                return OracleExtract(field.ToLowerInvariant(), args[1]);
            case "to_char" when args.Count == 2 && StringArg(args[1]) is { } fmt:
                return $"TO_CHAR({A(0)}, {Lit(ToOracleFormat(fmt))})";
            case "to_date" or "to_timestamp" when args.Count == 2 && StringArg(args[1]) is { } fmt:
                return $"{name.ToUpperInvariant()}({A(0)}, {Lit(ToOracleFormat(fmt))})";
            case "to_timestamp" when args.Count == 1:
                return $"(TIMESTAMP '1970-01-01 00:00:00 UTC' + NUMTODSINTERVAL({A(0)}, 'SECOND'))";
            case "to_char" or "to_number":
                return $"{name.ToUpperInvariant()}({All()})";
            case "make_date" when args.Count == 3:
                return $"TO_DATE({A(0)} || '-' || {A(1)} || '-' || {A(2)}, 'YYYY-MM-DD')";
            case "nextval" or "currval" when args.Count == 1 && StringArg(args[0]) is { } sequence:
                return SequenceToOracle(name, sequence, c);
            case "nextval" or "currval" or "setval":
                return Unsupported($"{name}() not translated: use sequence.NEXTVAL / CURRVAL", Show(c));
            case "current_user" or "session_user" or "user" or "current_role":
                return "USER";
            case "current_database" or "current_catalog":
                return "SYS_CONTEXT('USERENV', 'DB_NAME')";
            case "current_schema":
                return "SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')";
            case "json_build_object" or "jsonb_build_object":
                return "JSON_OBJECT(" + string.Join(", ", args.Chunk(2).Select(p => p.Length == 2 ? $"KEY {Expr(p[0])} VALUE {Expr(p[1])}" : Expr(p[0]))) + ")";
            case "json_build_array" or "jsonb_build_array":
                return $"JSON_ARRAY({All()})";
            default:
                return $"{c.Function.Last}({All()})" + Note($"{c.Function.Last}() was kept as written; check that Oracle has it");
        }
    }

    /// <summary>string_agg as LISTAGG ... WITHIN GROUP (ORDER BY ...), which Oracle 19c requires.</summary>
    private string ListAgg(PgCall c, List<PgExpr> args)
    {
        var value = Expr(args[0]);
        if (c.Filter is not null)
        {
            value = $"CASE WHEN {Cond(c.Filter)} THEN {value} END";
        }

        var separator = args.Count > 1 ? Expr(args[1]) : "NULL";
        var note = args.Count > 1 && StringArg(args[1]) is null ? Note("LISTAGG() needs a constant separator") : string.Empty;
        var order = c.OrderBy.Count > 0 ? OrderBy(c.OrderBy) : "ORDER BY NULL";
        return $"LISTAGG({(c.Distinct ? "DISTINCT " : string.Empty)}{value}, {separator}) WITHIN GROUP ({order})"
            + note + Note("LISTAGG() fails when the result passes 4000 bytes (32767 with MAX_STRING_SIZE = EXTENDED)");
    }

    /// <summary>regexp_replace: PostgreSQL replaces the first match unless flagged 'g'; Oracle replaces every match unless told the occurrence.</summary>
    private string RegexpReplaceToOracle(List<PgExpr> args, PgCall c)
    {
        var flags = args.Count == 4 ? StringArg(args[3]) : args.Count == 3 ? string.Empty : null;
        if (flags is null || args.Count < 3 || StringArg(args[2])?.Contains("\\&", StringComparison.Ordinal) == true)
        {
            return Unsupported("regexp_replace() with these arguments not translated", Show(c));
        }

        var head = $"{Expr(args[0])}, {Expr(args[1])}, {Expr(args[2])}";
        return flags switch
        {
            "" => $"REGEXP_REPLACE({head}, 1, 1)",
            "g" => $"REGEXP_REPLACE({head})",
            "i" => $"REGEXP_REPLACE({head}, 1, 1, 'i')",
            "gi" or "ig" => $"REGEXP_REPLACE({head}, 1, 0, 'i')",
            _ => Unsupported($"regexp_replace() flags '{flags}' not translated", Show(c)),
        };
    }

    private string DateTruncToOracle(string unit, PgExpr source, PgCall c)
    {
        var format = unit switch
        {
            "year" => "YYYY",
            "quarter" => "Q",
            "month" => "MM",
            "week" => "IW",
            "day" => "DD",
            "hour" => "HH24",
            "minute" => "MI",
            _ => null,
        };
        return format is not null ? $"TRUNC({Expr(source)}, '{format}')"
            : unit == "second" ? $"CAST({Expr(source)} AS TIMESTAMP(0))"
            : Unsupported($"date_trunc('{unit}') not translated", Show(c));
    }

    /// <summary>nextval('seq') as SEQ.NEXTVAL, the sequence named the way the conversion names tables.</summary>
    private string SequenceToOracle(string name, string sequence, PgCall c)
    {
        var parts = Regex.Matches(sequence, @"""((?:[^""]|"""")*)""|([^.""]+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal) : m.Groups[2].Value.Trim().ToLowerInvariant()).ToList();
        if (parts.Count is 0 or > 2)
        {
            return Unsupported($"sequence name {sequence} not understood", Show(c));
        }

        var named = new PortableName(parts.Count == 2 ? parts[0] : ctx.Schema.DefaultSchema, parts[^1]);
        named = ctx.Schema.Sequences.Select(s => s.Name).FirstOrDefault(s =>
            string.Equals(s.Schema, named.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Name, named.Name, StringComparison.OrdinalIgnoreCase)) ?? named;
        return $"{ctx.Qualified(named)}.{(name == "nextval" ? "NEXTVAL" : "CURRVAL")}";
    }

    private static readonly (string From, string To)[] ToOracleFormatTokens =
    [
        ("SSSSS", "SSSSS"), ("SSSS", "SSSSS"), ("TZH", "TZH"), ("TZM", "TZM"), ("MS", "FF3"), ("US", "FF6"), ("TZ", "TZD"), ("OF", "TZH:TZM"),
    ];

    private static readonly (string From, string To)[] ToPgFormatTokens =
    [
        ("SSSSS", "SSSS"), ("RRRR", "YYYY"), ("RR", "YY"), ("FF1", "MS"), ("FF2", "MS"), ("FF3", "MS"), ("FF4", "US"), ("FF5", "US"), ("FF6", "US"),
        ("FF7", "US"), ("FF8", "US"), ("FF9", "US"), ("FF", "US"), ("TZR", "TZ"), ("TZD", "TZ"),
    ];

    /// <summary>A to_char format in the other dialect: most codes are shared; fractions, time zones and RR differ.</summary>
    private static string MapFormat(string fmt, (string From, string To)[] tokens)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < fmt.Length)
        {
            if (fmt[i] == '"')
            {
                var close = fmt.IndexOf('"', i + 1);
                var end = close < 0 ? fmt.Length : close + 1;
                sb.Append(fmt[i..end]);
                i = end;
                continue;
            }

            var token = tokens.FirstOrDefault(t => string.Compare(fmt, i, t.From, 0, t.From.Length, StringComparison.OrdinalIgnoreCase) == 0);
            if (token.From is not null)
            {
                sb.Append(token.To);
                i += token.From.Length;
            }
            else
            {
                sb.Append(fmt[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    private static string ToOracleFormat(string fmt) => MapFormat(fmt, ToOracleFormatTokens);

    private static string ToPgFormat(string fmt) => MapFormat(fmt, ToPgFormatTokens);

    // ------------------------------------------------------------------ PostgreSQL → Oracle: queries

    private string SetOpToOracle(PgSetOp op)
    {
        var name = op.Op == "except" ? "MINUS" : op.Op.ToUpperInvariant();
        var all = op.All ? " ALL" : string.Empty;
        var note = string.Empty;
        if (op.All && op.Op != "union")
        {
            all = string.Empty;
            note = Note($"{op.Op.ToUpperInvariant()} ALL needs Oracle 21c; {name} removes duplicates");
        }

        return $"{Body(op.Left)} {name}{all}{note} {Body(op.Right)}";
    }

    /// <summary>VALUES rows as SELECT ... FROM DUAL UNION ALL ...: Oracle 19c has no VALUES lists.</summary>
    private string ValuesToOracle(IReadOnlyList<IReadOnlyList<PgExpr>> rows, IReadOnlyList<string> columns) =>
        string.Join(" UNION ALL ", rows.Select(r =>
            "SELECT " + string.Join(", ", r.Select((v, i) => $"{Expr(v)} AS {ctx.Ident(i < columns.Count ? columns[i] : $"column{i + 1}")}")) + " FROM DUAL"));

    private string FromToOracle(PgFrom f)
    {
        string AliasOf(string? alias) => alias is null ? string.Empty : " " + ctx.Ident(alias);
        switch (f)
        {
            case PgTableRef t:
                if (t.ColumnAliases.Count > 0)
                {
                    issues.Add($"column aliases on table {string.Join('.', t.Name.Parts)} are not supported in Oracle; they were dropped");
                }

                // Oracle takes no AS before a table alias.
                return Table(t.Name) + AliasOf(t.Alias);
            case PgSubqueryRef { Query: { Body: PgValuesBody values, OrderBy.Count: 0, Limit: null, Offset: null } } s:
                return $"({ValuesToOracle(values.Rows, s.ColumnAliases)}){AliasOf(s.Alias)}";
            case PgSubqueryRef s:
            {
                var query = s.Query;
                if (s.ColumnAliases.Count > 0)
                {
                    if (query is { Body: PgSelectCore core } && core.Targets.Count == s.ColumnAliases.Count && core.Targets.All(t => t.Expr is not PgStar))
                    {
                        // (SELECT a, b ...) AS t(x, y): the names go on the select list.
                        query = query with { Body = core with { Targets = [.. core.Targets.Select((t, i) => t with { Alias = s.ColumnAliases[i] })] } };
                    }
                    else
                    {
                        issues.Add("column aliases on a derived table are not supported in Oracle; they were dropped");
                    }
                }

                return $"{(s.Lateral ? "LATERAL " : string.Empty)}({Query(query)}){AliasOf(s.Alias)}";
            }

            case PgValuesRef v:
                return $"({ValuesToOracle(v.Rows, v.ColumnAliases)}){AliasOf(v.Alias)}";
            case PgFunctionRef fn when fn.Call.Function.Last.Equals("generate_series", StringComparison.OrdinalIgnoreCase) && fn.Call.Args.Count is 2 or 3 && !fn.Call.Args.Any(IsIntervalValue):
            {
                var column = fn.ColumnAliases.Count > 0 ? fn.ColumnAliases[0] : fn.Alias ?? "generate_series";
                var a = fn.Call.Args.Select(Expr).ToList();
                var step = a.Count > 2 ? $" * {a[2]}" : string.Empty;
                var count = a.Count > 2 ? $"FLOOR(({a[1]} - {a[0]}) / {a[2]}) + 1" : $"{a[1]} - {a[0]} + 1";
                return $"(SELECT {a[0]} + (LEVEL - 1){step} AS {ctx.Ident(column)} FROM DUAL CONNECT BY LEVEL <= {count}){AliasOf(fn.Alias ?? "generate_series")}";
            }

            case PgFunctionRef fn:
                return Unsupported("set-returning function in FROM has no Oracle counterpart", Show(fn.Call), string.Empty);
            case PgJoin j:
            {
                var kind = j.Kind switch
                {
                    "left" => "LEFT JOIN",
                    "right" => "RIGHT JOIN",
                    "full" => "FULL JOIN",
                    "cross" => "CROSS JOIN",
                    _ => "JOIN",
                };
                var tail = j.On is not null ? " ON " + Cond(j.On)
                    : j.Using.Count > 0 ? $" USING ({string.Join(", ", j.Using.Select(ctx.Ident))})" + Note("Oracle rejects a qualified reference (t.column) to a USING column")
                    : string.Empty;
                return $"{From(j.Left)} {(j.Natural ? "NATURAL " : string.Empty)}{kind} {From(j.Right)}{tail}";
            }

            default:
                return Unsupported("FROM item not translated", f.GetType().Name, string.Empty);
        }
    }

    // ------------------------------------------------------------------ Oracle → PostgreSQL: names and operators

    /// <summary>Oracle pseudo-columns and sequence references (SYSDATE, SYSTIMESTAMP, ROWNUM, seq.NEXTVAL); null for an ordinary name.</summary>
    private string? OracleName(PgName n)
    {
        if (n.Parts.Count == 1)
        {
            return n.Last switch
            {
                "SYSDATE" => "LOCALTIMESTAMP(0)",
                "SYSTIMESTAMP" => "now()",
                "ROWNUM" => Unsupported("ROWNUM has no PostgreSQL counterpart outside WHERE ROWNUM <= n", "ROWNUM", "row_number() OVER ()"),
                "LEVEL" => Unsupported("LEVEL belongs to CONNECT BY, which has no PostgreSQL counterpart", "LEVEL"),
                "ROWID" => Unsupported("ROWID has no PostgreSQL counterpart (ctid is not stable)", "ROWID"),
                "UID" => Unsupported("UID has no PostgreSQL counterpart", "UID"),
                _ => null,
            };
        }

        if (n.Last is "NEXTVAL" or "CURRVAL")
        {
            var sequence = new PortableName(n.Parts.Count > 2 ? n.Parts[^3] : ctx.Schema.DefaultSchema, n.Parts[^2]);
            return $"{(n.Last == "NEXTVAL" ? "nextval" : "currval")}({Lit(ctx.Qualified(sequence))})";
        }

        return n.Parts is [var package, var function] && package.Equals("DBMS_RANDOM", StringComparison.OrdinalIgnoreCase) && function.Equals("VALUE", StringComparison.OrdinalIgnoreCase)
            ? "random()"
            : null;
    }

    /// <summary>A call through a package (DBMS_RANDOM.VALUE(1, 10)); null when the prefix is a schema of the source.</summary>
    private string? OraclePackageCall(PgCall c, List<PgExpr> args)
    {
        var package = c.Function.Parts[^2];
        if (package.Equals(ctx.Schema.DefaultSchema, StringComparison.OrdinalIgnoreCase) || ctx.Schema.Schemas.Contains(package, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var full = string.Join('.', c.Function.Parts).ToUpperInvariant();
        return full switch
        {
            "DBMS_RANDOM.VALUE" when args.Count == 0 => "random()",
            "DBMS_RANDOM.VALUE" when args.Count == 2 => $"({Expr(args[0])} + random() * ({Expr(args[1])} - {Expr(args[0])}))",
            _ => $"{string.Join('.', c.Function.Parts).ToLowerInvariant()}({string.Join(", ", args.Select(Expr))})" + Note($"{full}() is an Oracle package function; rewrite it for PostgreSQL"),
        };
    }

    /// <summary>A date (DATE or TIMESTAMP) value in Oracle, as far as can be told: pseudo-columns, date functions, casts, literals, columns.</summary>
    private bool OracleDateLike(PgExpr e) => Unparen(e) switch
    {
        PgName { Parts: ["SYSDATE" or "SYSTIMESTAMP"] } => true,
        PgName n => IsDate(n) || IsTimestamp(n),
        PgCall c => c.Function.Last.ToLowerInvariant() switch
        {
            "current_date" or "current_timestamp" or "localtimestamp" or "to_date" or "to_timestamp" or "add_months" or "last_day" or "next_day" or "from_tz" or "new_time" => true,
            "trunc" or "round" => c.Args.Count > 0 && (OracleDateLike(c.Args[0]) || (c.Args.Count > 1 && StringArg(c.Args[1]) is not null)),
            _ => false,
        },
        PgCast c => c.Type.StartsWith("date", StringComparison.OrdinalIgnoreCase) || c.Type.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase),
        PgTypedLiteral t => t.Type is "date" || t.Type.StartsWith("timestamp", StringComparison.Ordinal),
        PgBinary { Op: "+" or "-" } b => OracleDateLike(b.Left) && !OracleDateLike(b.Right),
        _ => false,
    };

    /// <summary>An Oracle DATE (not TIMESTAMP): DATE minus DATE is a number of days.</summary>
    private bool OracleIsDate(PgExpr e) => Unparen(e) switch
    {
        PgName { Parts: ["SYSDATE"] } => true,
        PgName n => IsDate(n),
        PgCall c => c.Function.Last.ToLowerInvariant() switch
        {
            "current_date" or "to_date" or "add_months" or "last_day" or "next_day" => true,
            "trunc" or "round" => c.Args.Count > 0 && OracleDateLike(c.Args[0]),
            _ => false,
        },
        PgCast c => c.Type.Equals("date", StringComparison.OrdinalIgnoreCase),
        PgTypedLiteral t => t.Type == "date",
        PgBinary { Op: "+" or "-" } b => OracleIsDate(b.Left) && !OracleDateLike(b.Right),
        _ => false,
    };

    /// <summary>A DATE operand of a subtraction: a date literal as a timestamp, so DATE - DATE is always an interval.</summary>
    private string DateOperand(PgExpr e) => Unparen(e) is PgTypedLiteral { Type: "date" } t ? $"timestamp {Lit(t.Value)}" : Expr(e);

    private string? BinaryFromOracle(PgBinary b)
    {
        switch (b.Op)
        {
            case "||":
            {
                // An empty string is neutral in || on both sides; elsewhere Oracle's '' is NULL.
                var parts = ConcatParts(b).ToList();
                var text = string.Join(" || ", parts.Select(p => StringArg(p) is "" ? "''" : AsText(p)));
                return parts.Any(p => StringArg(p) is null && MaybeNull(p))
                    ? text + Note("Oracle's || treats NULL as an empty string; PostgreSQL's || gives NULL: use concat() where NULLs occur")
                    : text;
            }

            case "-" when OracleIsDate(b.Left) && OracleIsDate(b.Right):
                // DATE - DATE is a number of days in Oracle, an interval in PostgreSQL.
                return $"(EXTRACT(epoch FROM ({DateOperand(b.Left)} - {DateOperand(b.Right)})) / 86400)";
            case "+" or "-" when OracleDateLike(b.Left) && !OracleDateLike(b.Right) && !IsIntervalValue(b.Right):
                return $"{Expr(b.Left)} {b.Op} {Days(b.Right)}";
            case "+" when OracleDateLike(b.Right) && !OracleDateLike(b.Left) && !IsIntervalValue(b.Left):
                return $"{Expr(b.Right)} + {Days(b.Left)}";
            case "/" when IsInteger(b.Left) && IsInteger(b.Right):
                // Oracle's NUMBER division keeps the fraction; PostgreSQL would divide integers as integers.
                return $"CAST({Expr(b.Left)} AS numeric) / {Expr(b.Right)}";
            case "=" or "<>" when IsBooleanColumn(b.Left) && BoolLiteral(b.Right) is { } r:
                return $"{Expr(b.Left)} {b.Op} {r}";
            case "=" or "<>" when IsBooleanColumn(b.Right) && BoolLiteral(b.Left) is { } l:
                return $"{l} {b.Op} {Expr(b.Right)}";
            default:
                return null;
        }
    }

    /// <summary>A number of days added to an Oracle date, as a PostgreSQL interval.</summary>
    private string Days(PgExpr n) =>
        Unparen(n) is PgLiteral { Kind: LiteralKind.Number } literal && !literal.Value.Contains('.', StringComparison.Ordinal)
            ? $"interval '{literal.Value} days'"
            : $"({Expr(n)}) * interval '1 day'";

    /// <summary>An Oracle type (CAST target) as a PostgreSQL type; null when unknown.</summary>
    private static string? PgTypeOfOracle(string oracleType, out bool text)
    {
        text = false;
        var t = oracleType.Trim().ToLowerInvariant();
        var tz = t.Contains("with time zone", StringComparison.Ordinal);
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? t : t[..paren]).Replace(" with time zone", string.Empty, StringComparison.Ordinal).Trim();
        var args = paren < 0 ? string.Empty : t[(paren + 1)..t.IndexOf(')', paren)].Replace(" ", string.Empty, StringComparison.Ordinal);
        switch (name)
        {
            case "number" or "numeric" or "decimal" or "dec":
                return args.Length == 0 || args.StartsWith('*') ? "numeric" : $"numeric({args})";
            case "integer" or "int" or "smallint":
                return "integer";
            case "float" or "binary_double" or "double precision":
                return "double precision";
            case "binary_float" or "real":
                return "real";
            case "varchar2" or "nvarchar2" or "varchar" or "character varying":
                text = true;
                return args.Length > 0 ? $"varchar({args})" : "text";
            case "char" or "nchar" or "character":
                text = true;
                return args.Length > 0 ? $"char({args})" : "char(1)";
            case "clob" or "nclob" or "long" or "text":
                text = true;
                return "text";
            case "date":
                // An Oracle DATE has a time of day, to the second.
                return "timestamp(0)";
            case "timestamp":
                return (args.Length > 0 ? $"timestamp({args})" : "timestamp") + (tz ? " with time zone" : string.Empty);
            case "raw" or "blob" or "long raw":
                return "bytea";
            case "json" or "jsonb":
                return "jsonb";
            default:
                return null;
        }
    }

    private string CastFromOracle(PgCast c)
    {
        var target = PgTypeOfOracle(c.Type, out var text);
        if (text && StringArg(c.Operand) is { } literal)
        {
            return StringLiteral(literal);
        }

        return target is null
            ? $"CAST({Expr(c.Operand)} AS {c.Type})" + Note($"a cast to {c.Type} was kept as written; check the PostgreSQL type")
            : $"CAST({Expr(c.Operand)} AS {target})";
    }

    private string TypedLiteralFromOracle(PgTypedLiteral t)
    {
        if (t.Type.StartsWith("timestamp", StringComparison.Ordinal))
        {
            // TIMESTAMP '... +02:00' or '... Europe/Paris' is a timestamp with time zone.
            var zoned = Regex.IsMatch(t.Value.Trim(), @"([+-]\d{1,2}:\d{2}|[A-Za-z]+(/[A-Za-z_]+)?)$");
            return $"{(zoned ? "timestamptz" : "timestamp")} {Lit(t.Value)}";
        }

        return $"{t.Type} {Lit(t.Value)}";
    }

    // ------------------------------------------------------------------ Oracle → PostgreSQL: functions

    private static readonly HashSet<string> OracleTextFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "lower", "upper", "initcap", "trim", "btrim", "ltrim", "rtrim", "substr", "replace", "translate", "lpad", "rpad", "to_char", "concat", "nvl", "coalesce",
        "chr", "regexp_replace", "regexp_substr", "listagg",
    };

    /// <summary>A string argument: Oracle converts a number or date to text implicitly, PostgreSQL's string functions do not.</summary>
    private string OracleText(PgExpr e) =>
        StringArg(e) is not null || Unparen(e) is PgBinary { Op: "||" } || (Unparen(e) is PgCall call && OracleTextFunctions.Contains(call.Function.Last))
        || (Unparen(e) is PgName n && ctx.ColumnKindOf(n.Last) == ConversionContext.ColumnKind.Text)
            ? Expr(e)
            : $"CAST({Expr(e)} AS text)";

    /// <summary>Oracle functions in PostgreSQL.</summary>
    private string FromOracle(string name, List<PgExpr> args, PgCall c)
    {
        string A(int i) => Expr(args[i]);
        string All() => string.Join(", ", args.Select(Expr));
        if (name is "lower" or "upper" or "initcap" or "lpad" or "rpad" or "ltrim" or "rtrim" or "btrim" or "replace" or "translate" or "length" or "lengthb"
            or "substr" or "substrb" or "instr" or "regexp_like" or "regexp_replace" or "regexp_substr" && args.Count > 0)
        {
            // The string being worked on: as text.
            args = [new PgRawExpr(OracleText(args[0])), .. args.Skip(1)];
        }

        switch (name)
        {
            case "nvl" when args.Count == 2:
                return $"coalesce({All()})";
            case "nvl2" when args.Count == 3:
                return $"CASE WHEN {A(0)} IS NOT NULL THEN {A(1)} ELSE {A(2)} END";
            case "decode" when args.Count >= 3:
                return Decode(args);
            case "current_date":
                // Oracle's CURRENT_DATE is a DATE: today's date and time, to the second.
                return "LOCALTIMESTAMP(0)";
            case "current_timestamp":
                return "CURRENT_TIMESTAMP";
            case "localtimestamp":
                return "LOCALTIMESTAMP";
            case "user" or "current_user":
                return "current_user";
            case "sys_context" when args.Count == 2 && StringArg(args[1]) is { } attribute:
                return attribute.ToUpperInvariant() switch
                {
                    "CURRENT_SCHEMA" => "current_schema()",
                    "CURRENT_USER" or "SESSION_USER" => "current_user",
                    "DB_NAME" => "current_database()",
                    _ => $"sys_context({All()})" + Note($"SYS_CONTEXT('USERENV', '{attribute}') has no PostgreSQL counterpart"),
                };
            case "coalesce" or "nullif" or "lower" or "upper" or "initcap" or "lpad" or "rpad" or "ltrim" or "rtrim" or "btrim" or "translate" or "ascii" or "chr"
                or "abs" or "ceil" or "floor" or "sign" or "sqrt" or "exp" or "ln" or "power" or "mod" or "sin" or "cos" or "tan" or "asin" or "acos" or "atan"
                or "atan2" or "stddev" or "variance" or "row_number" or "rank" or "dense_rank" or "ntile" or "lag" or "lead" or "first_value" or "last_value"
                or "nth_value" or "percent_rank" or "cume_dist" or "rollup" or "cube" or "grouping" or "length":
                return $"{name}({All()})";
            case "round" when args.Count == 2 && StringArg(args[1]) is not null:
                return Unsupported("ROUND() of a date not translated", Show(c));
            case "round":
                return $"round({All()})";
            case "replace" when args.Count == 2:
                return $"replace({A(0)}, {A(1)}, '')";
            case "replace":
                return $"replace({All()})";
            case "count" or "sum" or "avg" or "min" or "max":
                return Aggregate(name, c, args);
            case "median" when args.Count == 1:
                return $"percentile_cont(0.5) WITHIN GROUP (ORDER BY {A(0)})";
            case "listagg":
            {
                var order = c.OrderBy.Count > 0 ? " " + OrderBy(c.OrderBy) : string.Empty;
                return $"string_agg({(c.Distinct ? "DISTINCT " : string.Empty)}{AsText(args[0])}, {(args.Count > 1 ? A(1) : "''")}{order})";
            }

            case "concat" when args.Count == 2:
                // Oracle's CONCAT() treats NULL as an empty string, like PostgreSQL's concat().
                return $"concat({All()})";
            case "lengthb":
                return $"octet_length({A(0)})";
            case "substr" or "substrb":
                if (args.Count > 1 && Unparen(args[1]) is PgLiteral { Kind: LiteralKind.Number } start)
                {
                    if (start.Value.StartsWith('-'))
                    {
                        return Unsupported("SUBSTR() with a negative start counts from the end in Oracle; PostgreSQL has no such form", Show(c));
                    }

                    if (start.Value == "0")
                    {
                        // Oracle takes a start of 0 as 1.
                        return $"substr({A(0)}, 1{string.Concat(args.Skip(2).Select(a => ", " + Expr(a)))})";
                    }
                }

                return $"substr({All()})";
            case "instr" when args.Count == 2:
                return $"strpos({A(0)}, {A(1)})";
            case "instr":
                return Unsupported("INSTR() with a start position or occurrence has no PostgreSQL counterpart", Show(c));
            case "trunc":
                return TruncFromOracle(args, c);
            case "add_months" when args.Count == 2:
                return (Unparen(args[1]) is PgLiteral { Kind: LiteralKind.Number } months && !months.Value.Contains('.', StringComparison.Ordinal)
                        ? $"({A(0)} + interval '{months.Value} months')"
                        : $"({A(0)} + ({A(1)}) * interval '1 month')")
                    + Note("Oracle's ADD_MONTHS keeps a month-end date at the end of the month; PostgreSQL's month arithmetic does not");
            case "months_between" when args.Count == 2:
                return $"(EXTRACT(year FROM age({A(0)}, {A(1)})) * 12 + EXTRACT(month FROM age({A(0)}, {A(1)})))"
                    + Note("MONTHS_BETWEEN() became whole months from age(): Oracle's also counts the fraction of a month");
            case "last_day" when args.Count == 1:
                return $"(date_trunc('month', {A(0)}) + interval '1 month - 1 day')" + Note("Oracle's LAST_DAY() keeps the time of day; here it is midnight");
            case "to_char" or "to_nchar" when args.Count == 1:
                return $"CAST({A(0)} AS text)" + (OracleDateLike(args[0]) ? Note("TO_CHAR() of a date without a format follows NLS_DATE_FORMAT in Oracle") : string.Empty);
            case "to_char" or "to_nchar" when StringArg(args[1]) is { } fmt:
                return $"to_char({A(0)}, {Lit(ToPgFormat(fmt))})" + (args.Count > 2 ? Note("the NLS argument of TO_CHAR() was dropped") : string.Empty);
            case "to_date" when args.Count >= 2 && StringArg(args[1]) is { } fmt:
                // An Oracle DATE has a time of day, to the second.
                return $"CAST(to_timestamp({A(0)}, {Lit(ToPgFormat(fmt))}) AS timestamp(0))";
            case "to_date" when args.Count == 1:
                return $"CAST({A(0)} AS timestamp(0))" + Note("TO_DATE() without a format follows NLS_DATE_FORMAT in Oracle");
            case "to_timestamp" when args.Count >= 2 && StringArg(args[1]) is { } fmt:
                return $"CAST(to_timestamp({A(0)}, {Lit(ToPgFormat(fmt))}) AS timestamp)";
            case "to_timestamp" when args.Count == 1:
                return $"CAST({A(0)} AS timestamp)";
            case "to_number" when args.Count == 1:
                return $"CAST({A(0)} AS numeric)";
            case "to_number":
                return $"to_number({A(0)}, {A(1)})";
            case "to_clob" or "to_nclob":
                return $"CAST({A(0)} AS text)";
            case "greatest" or "least":
                return $"{name}({All()})" + Note($"PostgreSQL's {name}() skips NULLs; Oracle's is NULL when any argument is NULL");
            case "bitand" when args.Count == 2:
                return $"({A(0)} & {A(1)})";
            case "sys_guid" when args.Count == 0:
                return "gen_random_uuid()" + Note("SYS_GUID() is RAW(16) in Oracle; gen_random_uuid() is a uuid: check the column type");
            case "rawtohex" when args.Count == 1:
                return $"upper(encode({A(0)}, 'hex'))";
            case "hextoraw" when args.Count == 1:
                return $"decode({A(0)}, 'hex')";
            case "regexp_like" when args.Count is 2 or 3:
            {
                var flags = args.Count == 3 ? StringArg(args[2]) ?? "?" : string.Empty;
                var insensitive = flags.LastIndexOf('i') > flags.LastIndexOf('c');
                var note = flags.Trim('i', 'c').Length > 0 ? Note($"REGEXP_LIKE() match parameter '{flags}' was dropped") : string.Empty;
                return $"({A(0)} {(insensitive ? "~*" : "~")} {A(1)}){note}";
            }

            case "regexp_replace":
                return RegexpReplaceFromOracle(args, c);
            case "regexp_substr" when args.Count == 2:
                return $"substring({A(0)} from {A(1)})"
                    + (StringArg(args[1])?.Contains('(', StringComparison.Ordinal) != false ? Note("PostgreSQL's substring(... from pattern) returns the first parenthesized group when the pattern has one; Oracle's REGEXP_SUBSTR the whole match") : string.Empty);
            case "numtodsinterval" or "numtoyminterval" when args.Count == 2 && StringArg(args[1]) is { } unit:
                return $"({A(0)}) * interval '1 {unit.ToLowerInvariant()}'";
            case "json_value" or "json_query" when args.Count >= 2 && StringArg(args[1]) is { } path:
                return JsonFromOracle(name == "json_value", args, path, c);
            default:
                return $"{name}({All()})" + Note($"{name}() was kept as written; check that PostgreSQL has it");
        }
    }

    /// <summary>
    /// DECODE as CASE. DECODE takes NULL to match NULL: literal search values give a simple CASE, anything else
    /// IS NOT DISTINCT FROM.
    /// </summary>
    private string Decode(List<PgExpr> args)
    {
        static bool IsNull(PgExpr e) => Unparen(e) is PgLiteral { Kind: LiteralKind.Null } || StringArg(e) is "";
        var pairs = args.Skip(1).Chunk(2).Where(p => p.Length == 2).ToList();
        var otherwise = (args.Count - 1) % 2 == 1 ? $" ELSE {Expr(args[^1])}" : string.Empty;
        if (pairs.All(p => Unparen(p[0]) is PgLiteral { Kind: LiteralKind.Number or LiteralKind.String } && !IsNull(p[0])))
        {
            return $"CASE {Expr(args[0])}{string.Concat(pairs.Select(p => $" WHEN {Expr(p[0])} THEN {Expr(p[1])}"))}{otherwise} END";
        }

        var x = Expr(args[0]);
        return "CASE" + string.Concat(pairs.Select(p => IsNull(p[0]) ? $" WHEN {x} IS NULL THEN {Expr(p[1])}" : $" WHEN {x} IS NOT DISTINCT FROM {Expr(p[0])} THEN {Expr(p[1])}")) + otherwise + " END";
    }

    private string TruncFromOracle(List<PgExpr> args, PgCall c)
    {
        if (args.Count == 2 && StringArg(args[1]) is { } fmt)
        {
            var unit = fmt.Trim().ToUpperInvariant() switch
            {
                "DD" or "DDD" or "J" => "day",
                "MM" or "MON" or "MONTH" or "RM" => "month",
                "YYYY" or "YEAR" or "YY" or "Y" or "YYY" or "SYYYY" or "SYEAR" or "RRRR" or "RR" => "year",
                "Q" => "quarter",
                "IW" => "week",
                "HH" or "HH12" or "HH24" => "hour",
                "MI" => "minute",
                _ => null,
            };
            return unit is null ? Unsupported($"TRUNC() to '{fmt}' not translated", Show(c)) : $"date_trunc('{unit}', {Expr(args[0])})";
        }

        if (args.Count == 1 && OracleDateLike(args[0]))
        {
            return $"date_trunc('day', {Expr(args[0])})";
        }

        var numeric = Unparen(args[0]) is PgLiteral { Kind: LiteralKind.Number } || ColumnTypes(args[0], t => !t.StartsWith("date", StringComparison.Ordinal) && !t.StartsWith("timestamp", StringComparison.Ordinal));
        return $"trunc({string.Join(", ", args.Select(Expr))})"
            + (numeric || args.Count > 1 ? string.Empty : Note("TRUNC() was taken as numeric; if its argument is a date, use date_trunc('day', ...)"));
    }

    /// <summary>Oracle's REGEXP_REPLACE replaces every match unless told the occurrence; PostgreSQL's the first unless flagged 'g'.</summary>
    private string RegexpReplaceFromOracle(List<PgExpr> args, PgCall c)
    {
        var position = args.Count > 3 ? Unparen(args[3]) as PgLiteral : null;
        var occurrence = args.Count > 4 ? Unparen(args[4]) as PgLiteral : null;
        var match = args.Count > 5 ? StringArg(args[5]) : string.Empty;
        if (args.Count < 2 || (args.Count > 3 && position?.Value != "1") || (args.Count > 4 && occurrence?.Value is not ("0" or "1")) || match is null || match.Trim('i', 'c').Length > 0)
        {
            return Unsupported("REGEXP_REPLACE() with these arguments not translated", Show(c));
        }

        var flags = (occurrence?.Value == "1" ? string.Empty : "g") + (match.LastIndexOf('i') > match.LastIndexOf('c') ? "i" : string.Empty);
        return $"regexp_replace({Expr(args[0])}, {Expr(args[1])}, {(args.Count > 2 ? Expr(args[2]) : "''")}{(flags.Length > 0 ? $", '{flags}'" : string.Empty)})";
    }

    /// <summary>JSON_VALUE / JSON_QUERY as PostgreSQL operators on jsonb; text columns holding JSON are cast to jsonb.</summary>
    private string JsonFromOracle(bool value, List<PgExpr> args, string path, PgCall c)
    {
        var returning = c.ArgNames.Count == args.Count ? args.Where((_, i) => c.ArgNames[i] == "returning").Select(StringArg).FirstOrDefault() : null;
        var doc = ColumnTypes(args[0], t => t == "json") ? args[0] : new PgCast(args[0], "jsonb");
        var result = JsonPathToPg(doc, path, value, Show(c));
        return returning is not null && PgTypeOfOracle(returning, out _) is { } type && type != "text" ? $"CAST({result} AS {type})" : result;
    }

    // ------------------------------------------------------------------ Oracle → PostgreSQL: queries

    private static IEnumerable<PgExpr> Conjuncts(PgExpr e) =>
        Unparen(e) is PgBinary { Op: "and" } b ? Conjuncts(b.Left).Concat(Conjuncts(b.Right)) : [e];

    private static PgExpr? AllOf(IReadOnlyList<PgExpr> conditions) =>
        conditions.Count == 0 ? null : conditions.Skip(1).Aggregate(conditions[0], (a, b) => new PgBinary("and", a, b));

    /// <summary>WHERE ROWNUM &lt;= n (or &lt; n) as LIMIT n.</summary>
    private PgQuery RownumToLimit(PgQuery q)
    {
        if (q.Limit is not null || q.Body is not PgSelectCore { Where: { } where } core)
        {
            return q;
        }

        static bool IsRownum(PgExpr e) => Unparen(e) is PgName { Parts: ["ROWNUM"] };
        PgExpr? limit = null;
        var rest = new List<PgExpr>();
        foreach (var condition in Conjuncts(where))
        {
            var bound = limit is null ? Unparen(condition) switch
            {
                PgBinary { Op: "<=" } b when IsRownum(b.Left) => b.Right,
                PgBinary { Op: ">=" } b when IsRownum(b.Right) => b.Left,
                PgBinary { Op: "<" } b when IsRownum(b.Left) => Minus1(b.Right),
                PgBinary { Op: ">" } b when IsRownum(b.Right) => Minus1(b.Left),
                PgBinary { Op: "=" } b when IsRownum(b.Left) && Unparen(b.Right) is PgLiteral { Value: "1" } => b.Right,
                _ => null,
            }
            : null;
            if (bound is null)
            {
                rest.Add(condition);
            }
            else
            {
                limit = bound;
            }
        }

        if (limit is null)
        {
            return q;
        }

        if (q.OrderBy.Count > 0)
        {
            issues.Add("Oracle applies ROWNUM before ORDER BY; LIMIT applies after it: check which rows are wanted");
        }

        return q with { Body = core with { Where = AllOf(rest) }, Limit = limit };
    }

    private static PgExpr Minus1(PgExpr e) =>
        Unparen(e) is PgLiteral { Kind: LiteralKind.Number } n && int.TryParse(n.Value, CultureInfo.InvariantCulture, out var v)
            ? new PgLiteral(LiteralKind.Number, (v - 1).ToString(CultureInfo.InvariantCulture))
            : new PgBinary("-", e, new PgLiteral(LiteralKind.Number, "1"));

    /// <summary>A CTE of the query names itself: Oracle writes no RECURSIVE.</summary>
    private static bool OracleRecursive(PgQuery q) =>
        q.With.Any(cte => cte.Body is PgSelectStatement s && References(s.Query.Body, cte.Name));

    private static bool References(PgQueryBody body, string name) => body switch
    {
        PgSelectCore s => s.From.Any(f => References(f, name)),
        PgSetOp op => References(op.Left, name) || References(op.Right, name),
        PgNestedQuery n => References(n.Query.Body, name),
        _ => false,
    };

    private static bool References(PgFrom from, string name) => from switch
    {
        PgTableRef t => t.Name.Parts.Count == 1 && t.Name.Last == name,
        PgJoin j => References(j.Left, name) || References(j.Right, name),
        PgSubqueryRef s => References(s.Query.Body, name),
        _ => false,
    };

    /// <summary>
    /// An Oracle FROM list made PostgreSQL: DUAL dropped, and (+) outer joins in WHERE rewritten as LEFT JOINs when each
    /// joined table's conditions name only tables placed before it. Anything else keeps its (+) and is flagged.
    /// </summary>
    private PgSelectCore OracleFrom(PgSelectCore s)
    {
        var from = s.From.Where(f => !(f is PgTableRef { Alias: null } t && t.Name.Last.Equals("DUAL", StringComparison.OrdinalIgnoreCase)
            && (t.Name.Parts.Count == 1 || t.Name.Parts[^2].Equals("SYS", StringComparison.OrdinalIgnoreCase)))).ToList();
        s = s with { From = from };
        if (s.Where is null || !HasMarker(s.Where))
        {
            return s;
        }

        static string? KeyOf(PgFrom f) => f switch
        {
            PgTableRef t => t.Alias ?? t.Name.Last,
            PgSubqueryRef { Alias: { } alias } => alias,
            _ => null,
        };

        var keys = from.Select(KeyOf).ToList();
        if (keys.Any(k => k is null))
        {
            return s;
        }

        var conditions = new Dictionary<string, List<PgExpr>>(StringComparer.OrdinalIgnoreCase);
        var depends = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var rest = new List<PgExpr>();
        foreach (var condition in Conjuncts(s.Where))
        {
            if (!HasMarker(condition))
            {
                rest.Add(condition);
                continue;
            }

            var names = new List<(string? Qualifier, bool Marked)>();
            Qualifiers(condition, false, names);
            var optional = names.Where(n => n.Marked).Select(n => n.Qualifier).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (optional is not [{ } table] || !keys.Contains(table, StringComparer.OrdinalIgnoreCase) || names.Any(n => n.Qualifier is null))
            {
                rest.Add(condition);
                continue;
            }

            (conditions.TryGetValue(table, out var list) ? list : conditions[table] = []).Add(StripMarkers(condition));
            var others = depends.TryGetValue(table, out var set) ? set : depends[table] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            others.UnionWith(names.Where(n => !n.Marked && !string.Equals(n.Qualifier, table, StringComparison.OrdinalIgnoreCase)).Select(n => n.Qualifier!));
        }

        var inner = from.Where((_, i) => !conditions.ContainsKey(keys[i]!)).ToList();
        if (conditions.Count == 0 || inner.Count == 0)
        {
            return s;
        }

        // The inner tables first (their joins stay in WHERE), then each outer-joined table once what it names is placed.
        var placed = new HashSet<string>(inner.Select(f => KeyOf(f)!), StringComparer.OrdinalIgnoreCase);
        var tree = inner.Skip(1).Aggregate(inner[0], (left, right) => new PgJoin(left, right, "cross", null, [], false));
        var pending = from.Where((_, i) => conditions.ContainsKey(keys[i]!)).ToList();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(f => depends[KeyOf(f)!].All(placed.Contains));
            if (next is null)
            {
                return s;
            }

            var key = KeyOf(next)!;
            tree = new PgJoin(tree, next, "left", AllOf(conditions[key]), [], false);
            placed.Add(key);
            pending.Remove(next);
        }

        return s with { From = [tree], Where = AllOf(rest) };
    }

    private static bool IsMarker(PgExpr e) => e is PgCall { Function.Parts: [PgParser.OuterJoinMarker] };

    private static bool HasMarker(PgExpr e) => e switch
    {
        PgCall c => IsMarker(c) || c.Args.Any(HasMarker),
        PgBinary b => HasMarker(b.Left) || HasMarker(b.Right),
        PgUnary u => HasMarker(u.Operand),
        PgIs i => HasMarker(i.Operand),
        PgLike l => HasMarker(l.Operand) || HasMarker(l.Pattern),
        PgBetween b => HasMarker(b.Operand) || HasMarker(b.Low) || HasMarker(b.High),
        PgInList i => HasMarker(i.Operand) || i.Values.Any(HasMarker),
        PgCast c => HasMarker(c.Operand),
        _ => false,
    };

    /// <summary>The table qualifiers of the columns in a condition, and whether each carries (+); null for an unqualified column.</summary>
    private static void Qualifiers(PgExpr e, bool marked, List<(string?, bool)> names)
    {
        switch (e)
        {
            case PgName n:
                names.Add((n.Parts.Count >= 2 ? n.Parts[^2] : null, marked));
                break;
            case PgCall c when IsMarker(c):
                Qualifiers(c.Args[0], true, names);
                break;
            case PgCall c:
                c.Args.ToList().ForEach(a => Qualifiers(a, marked, names));
                break;
            case PgBinary b:
                Qualifiers(b.Left, marked, names);
                Qualifiers(b.Right, marked, names);
                break;
            case PgUnary u:
                Qualifiers(u.Operand, marked, names);
                break;
            case PgIs i:
                Qualifiers(i.Operand, marked, names);
                break;
            case PgLike l:
                Qualifiers(l.Operand, marked, names);
                Qualifiers(l.Pattern, marked, names);
                break;
            case PgBetween b:
                Qualifiers(b.Operand, marked, names);
                Qualifiers(b.Low, marked, names);
                Qualifiers(b.High, marked, names);
                break;
            case PgInList i:
                Qualifiers(i.Operand, marked, names);
                i.Values.ToList().ForEach(v => Qualifiers(v, marked, names));
                break;
            case PgCast c:
                Qualifiers(c.Operand, marked, names);
                break;
        }
    }

    private static PgExpr StripMarkers(PgExpr e) => e switch
    {
        PgCall c when IsMarker(c) => c.Args[0],
        PgCall c => c with { Args = [.. c.Args.Select(StripMarkers)] },
        PgBinary b => b with { Left = StripMarkers(b.Left), Right = StripMarkers(b.Right) },
        PgUnary u => u with { Operand = StripMarkers(u.Operand) },
        PgIs i => i with { Operand = StripMarkers(i.Operand) },
        PgLike l => l with { Operand = StripMarkers(l.Operand), Pattern = StripMarkers(l.Pattern) },
        PgBetween b => b with { Operand = StripMarkers(b.Operand), Low = StripMarkers(b.Low), High = StripMarkers(b.High) },
        PgInList i => i with { Operand = StripMarkers(i.Operand), Values = [.. i.Values.Select(StripMarkers)] },
        PgCast c => c with { Operand = StripMarkers(c.Operand) },
        _ => e,
    };
}
