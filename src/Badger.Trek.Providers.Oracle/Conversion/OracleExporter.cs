using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.Oracle.Scripting;

namespace Badger.Trek.Providers.Oracle.Conversion;

/// <summary>
/// Reads the deploy schema into a <see cref="PortableSchema"/> from the ALL_* dictionary views. The schema (its owner) is
/// the one schema: every name carries it, spelled as Oracle stores it (upper case unless created quoted). Column types are
/// written as DDL writes them (VARCHAR2(100 CHAR), NUMBER(12,2), TIMESTAMP(6) WITH TIME ZONE ...); defaults, checks,
/// index expressions and code are Oracle SQL and PL/SQL without the owner's qualifiers. A package (spec and body) has no
/// code kind of its own: it is one <see cref="PortableCodeKind.Function"/> entry holding both, each followed by a '/' line.
/// Materialized views, partitioning, object types, synonyms, database links and domain indexes are reported instead of
/// converted.
/// </summary>
public sealed partial class OracleExporter : ISchemaExporter
{
    public async Task<PortableSchema> ExportAsync(IDatabaseSession session, ExportOptions options, CancellationToken cancellationToken)
    {
        var owner = OracleStateStore.Owner(session);
        var p = new Dictionary<string, object?> { ["o"] = owner };
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) =>
            await session.QueryAsync(sql, p, cancellationToken).ConfigureAwait(false);
        string Local(object? definition) => OracleSchemaInspector.Unqualify(S(definition), owner);

        var schema = new PortableSchema
        {
            SourceProvider = OracleProvider.ProviderId,
            DefaultSchema = owner,
            CaseInsensitive = false,
            Collation = null,
        };
        schema.Schemas.Add(owner);

        var prefix = OracleStateStore.Prefix(options.StateSchema);
        var state = StateTables.Names.Append(OracleStateStore.LockTable).Select(n => prefix + n.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        PortableName N(string name) => new(owner, name);
        bool Skip(string name) => state.Contains(name) || OracleModel.System(name) || options.Excluded(N(name));

        // Tables (materialized view containers belong to the materialized view, which is not converted).
        var mviews = (await Q("SELECT mview_name AS name FROM all_mviews WHERE owner = :o ORDER BY mview_name")).Select(r => S(r["name"])).ToList();
        foreach (var name in mviews.Where(m => !Skip(m)))
        {
            schema.Warnings.Add($"materialized view {N(name)} is not converted.");
        }

        var tables = new Dictionary<string, TableParts>(StringComparer.Ordinal);
        foreach (var r in await Q("""
            SELECT table_name AS t, temporary AS tmp, partitioned AS part
              FROM all_tables
             WHERE owner = :o AND nested = 'NO' AND dropped = 'NO' AND secondary = 'N' AND (iot_type IS NULL OR iot_type = 'IOT')
             ORDER BY table_name
            """))
        {
            var name = S(r["t"]);
            if (Skip(name) || mviews.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            if (S(r["part"]) == "YES")
            {
                schema.Warnings.Add($"{N(name)}: partitioning is not converted.");
            }

            if (S(r["tmp"]) == "Y")
            {
                schema.Warnings.Add($"{N(name)} is a global temporary table; it is converted as a regular table.");
            }

            tables[name] = new TableParts(N(name));
        }

        foreach (var r in await Q("SELECT table_name AS t, comments AS c FROM all_tab_comments WHERE owner = :o AND table_type = 'TABLE' AND comments IS NOT NULL"))
        {
            if (tables.TryGetValue(S(r["t"]), out var table) && S(r["c"]) is { Length: > 0 } comment)
            {
                table.Comment = comment;
            }
        }

        // Columns.
        var identities = (await Q("SELECT table_name AS t, column_name AS c, generation_type AS gen, identity_options AS opts FROM all_tab_identity_cols WHERE owner = :o"))
            .ToDictionary(r => (S(r["t"]), S(r["c"])), r => Identity(S(r["gen"]), S(r["opts"])));
        var columnComments = (await Q("SELECT table_name AS t, column_name AS c, comments AS cmt FROM all_col_comments WHERE owner = :o AND comments IS NOT NULL"))
            .ToDictionary(r => (S(r["t"]), S(r["c"])), r => S(r["cmt"]));
        foreach (var r in await Q("""
            SELECT table_name AS t, column_name AS c, data_type AS type, data_type_owner AS towner, data_length AS len, char_length AS clen,
                   char_used AS cused, data_precision AS prec, data_scale AS scale, nullable AS nul, data_default AS def, virtual_column AS virt
              FROM all_tab_cols
             WHERE owner = :o AND hidden_column = 'NO'
             ORDER BY table_name, column_id
            """))
        {
            if (!tables.TryGetValue(S(r["t"]), out var table))
            {
                continue;
            }

            var name = S(r["c"]);
            var identity = identities.GetValueOrDefault((table.Name.Name, name));
            var isVirtual = S(r["virt"]) == "YES" && S(r["type"]) != "XMLTYPE"; // binary XML is stored as a virtual column over a hidden LOB
            var expression = S(r["def"]) is { Length: > 0 } d && !d.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? Local(d) : null;
            if (S(r["towner"]) is { Length: > 0 } typeOwner && typeOwner != "SYS" && typeOwner != "PUBLIC")
            {
                schema.Warnings.Add($"{table.Name}.{name}: column of object type {typeOwner}.{S(r["type"])} is not converted.");
            }

            table.Columns.Add(new PortableColumn(name, OracleModel.ColumnType(r), S(r["nul"]) == "Y")
            {
                Default = identity is null && !isVirtual ? expression : null,
                Identity = identity,
                Computed = isVirtual ? expression : null,
                ComputedStored = false,
                Comment = columnComments.GetValueOrDefault((table.Name.Name, name)) is { Length: > 0 } comment ? comment : null,
            });
        }

        // Constraints: primary keys, uniques, foreign keys and checks (not the NOT NULL checks Oracle makes up).
        var consColumns = (await Q("""
            SELECT owner AS o, constraint_name AS name, column_name AS c, position AS pos
              FROM all_cons_columns
             WHERE owner = :o
                OR (owner, constraint_name) IN (SELECT r_owner, r_constraint_name FROM all_constraints WHERE owner = :o AND constraint_type = 'R')
            """)).ToLookup(r => (S(r["o"]), S(r["name"])));
        List<string> Columns(string constraintOwner, string constraint) =>
            consColumns[(constraintOwner, constraint)].OrderBy(c => L(c["pos"]) ?? 0).Select(c => S(c["c"])).ToList();

        var constraintRows = await Q("""
            SELECT c.constraint_name AS name, c.constraint_type AS type, c.table_name AS t, c.search_condition_vc AS cond, c.delete_rule AS del,
                   c.generated AS gen, c.index_owner AS iowner, c.index_name AS idx, r.owner AS rowner, r.table_name AS rt, r.constraint_name AS rname
              FROM all_constraints c
              LEFT JOIN all_constraints r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
             WHERE c.owner = :o AND c.constraint_type IN ('P', 'U', 'R', 'C')
             ORDER BY c.table_name, c.constraint_name
            """);
        var constraintIndexes = constraintRows.Where(r => S(r["idx"]).Length > 0).Select(r => S(r["idx"])).ToHashSet(StringComparer.Ordinal);
        foreach (var r in constraintRows)
        {
            if (!tables.TryGetValue(S(r["t"]), out var table))
            {
                continue;
            }

            var name = S(r["name"]);
            var portableName = S(r["gen"]) == "GENERATED NAME" ? null : name;
            switch (S(r["type"]))
            {
                case "P":
                    table.PrimaryKey = new PortableKey(portableName, Columns(owner, name));
                    break;
                case "U":
                    table.Uniques.Add(new PortableKey(portableName, Columns(owner, name)));
                    break;
                case "R":
                    var rule = S(r["del"]) is "CASCADE" or "SET NULL" ? S(r["del"]) : "NO ACTION";
                    table.ForeignKeys.Add(new PortableForeignKey(portableName, Columns(owner, name), new PortableName(S(r["rowner"]), S(r["rt"])),
                        Columns(S(r["rowner"]), S(r["rname"])), rule, "NO ACTION"));
                    break;
                default:
                    var condition = Local(r["cond"]);
                    if (portableName is null && NotNullRegex().IsMatch(condition))
                    {
                        continue;
                    }

                    table.Checks.Add(new PortableCheck(portableName, condition));
                    break;
            }
        }

        // Indexes that do not back a constraint. A descending column is stored as a function-based index on the quoted
        // column name: it comes back as a plain descending column.
        var expressions = (await Q("SELECT index_name AS i, column_position AS pos, column_expression AS expr FROM all_ind_expressions WHERE index_owner = :o"))
            .ToDictionary(r => (S(r["i"]), L(r["pos"]) ?? 0), r => Local(r["expr"]));
        var indexColumns = (await Q("SELECT index_name AS i, column_name AS c, column_position AS pos, descend FROM all_ind_columns WHERE index_owner = :o"))
            .ToLookup(r => S(r["i"]));
        foreach (var r in await Q("""
            SELECT index_name AS i, table_name AS t, index_type AS type, uniqueness AS uniq
              FROM all_indexes
             WHERE owner = :o AND table_owner = :o AND index_type NOT IN ('LOB', 'IOT - TOP', 'CLUSTER') AND generated = 'N'
             ORDER BY table_name, index_name
            """))
        {
            var index = S(r["i"]);
            if (!tables.TryGetValue(S(r["t"]), out var table) || constraintIndexes.Contains(index) || Skip(index))
            {
                continue;
            }

            var type = S(r["type"]);
            if (type.Contains("DOMAIN", StringComparison.Ordinal))
            {
                schema.Warnings.Add($"{table.Name}: domain index {index} is not converted.");
                continue;
            }

            var columns = indexColumns[index].OrderBy(c => L(c["pos"]) ?? 0).Select(c =>
            {
                var descending = S(c["descend"]) == "DESC";
                if (!expressions.TryGetValue((index, L(c["pos"]) ?? 0), out var expression))
                {
                    return new PortableIndexColumn(S(c["c"]), false, descending);
                }

                return QuotedNameRegex().Match(expression) is { Success: true } m
                    ? new PortableIndexColumn(m.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal), false, descending)
                    : new PortableIndexColumn(expression, true, descending);
            }).ToList();
            var method = type.Contains("BITMAP", StringComparison.Ordinal) ? "bitmap" : null;
            table.Indexes.Add(new PortableIndex(index, S(r["uniq"]) == "UNIQUE", columns, [], null, method));
        }

        foreach (var t in tables.Values)
        {
            schema.Tables.Add(new PortableTable(t.Name, t.Columns, t.PrimaryKey, t.Uniques, t.Checks, t.ForeignKeys, t.Indexes) { Comment = t.Comment });
        }

        // Sequences (not the ones behind identity columns). LAST_NUMBER is where the sequence continues.
        foreach (var r in await Q("""
            SELECT sequence_name AS name, min_value AS lo, max_value AS hi, increment_by AS inc, cycle_flag AS cyc, last_number AS next
              FROM all_sequences
             WHERE sequence_owner = :o
             ORDER BY sequence_name
            """))
        {
            var name = S(r["name"]);
            if (!Skip(name))
            {
                schema.Sequences.Add(new PortableSequence(N(name), "NUMBER", L(r["next"]) ?? 1, L(r["inc"]) ?? 1, L(r["lo"]), L(r["hi"]), S(r["cyc"]) == "Y"));
            }
        }

        await ReadCodeAsync(session, schema, owner, Skip, cancellationToken).ConfigureAwait(false);

        foreach (var r in await Q("SELECT synonym_name AS name FROM all_synonyms WHERE owner = :o ORDER BY synonym_name"))
        {
            if (!Skip(S(r["name"])))
            {
                schema.Warnings.Add($"synonym {N(S(r["name"]))} is not converted.");
            }
        }

        foreach (var r in await Q("SELECT db_link AS name FROM all_db_links WHERE owner = :o ORDER BY db_link"))
        {
            schema.Warnings.Add($"database link {S(r["name"])} is not converted.");
        }

        return schema;
    }

    /// <summary>
    /// Views, functions, packages, procedures and triggers, each after the code objects it uses (ALL_DEPENDENCIES). Object
    /// types are reported instead.
    /// </summary>
    private static async Task ReadCodeAsync(IDatabaseSession session, PortableSchema schema, string owner, Func<string, bool> skip, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object?> { ["o"] = owner };
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) =>
            await session.QueryAsync(sql, p, cancellationToken).ConfigureAwait(false);
        PortableName N(string name) => new(owner, name);

        // What each object uses, by name; a package body's uses belong to its package.
        var uses = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var r in await Q("""
            SELECT name, referenced_name AS rname
              FROM all_dependencies
             WHERE owner = :o AND referenced_owner = :o AND type IN ('VIEW', 'FUNCTION', 'PROCEDURE', 'PACKAGE', 'PACKAGE BODY', 'TRIGGER')
               AND referenced_type IN ('VIEW', 'FUNCTION', 'PROCEDURE', 'PACKAGE')
            """))
        {
            var (name, used) = (S(r["name"]), S(r["rname"]));
            if (name != used)
            {
                if (!uses.TryGetValue(name, out var set))
                {
                    uses[name] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(N(used).Key);
            }
        }

        IReadOnlyList<string> DependsOn(string name) => uses.TryGetValue(name, out var set) ? set.Order(StringComparer.Ordinal).ToList() : [];

        var views = new List<PortableCode>();
        foreach (var r in await Q("SELECT view_name AS name, text FROM all_views WHERE owner = :o ORDER BY view_name"))
        {
            var name = S(r["name"]);
            if (!skip(name))
            {
                var text = OracleSchemaInspector.Unqualify(S(r["text"]), owner);
                views.Add(new PortableCode(PortableCodeKind.View, N(name), $"CREATE OR REPLACE VIEW {OracleModel.Q(name)} AS\n{text}")
                {
                    Language = "sql",
                    DependsOn = DependsOn(name),
                });
            }
        }

        var source = new Dictionary<(string Type, string Name), StringBuilder>();
        foreach (var r in await Q("""
            SELECT type, name, text
              FROM all_source
             WHERE owner = :o AND type IN ('PROCEDURE', 'FUNCTION', 'PACKAGE', 'PACKAGE BODY', 'TRIGGER', 'TYPE', 'TYPE BODY')
             ORDER BY type, name, line
            """))
        {
            var key = (S(r["type"]), S(r["name"]));
            if (!source.TryGetValue(key, out var text))
            {
                source[key] = text = new StringBuilder();
            }

            text.Append(r["text"] as string);
        }

        string? Create(string type, string name) => source.TryGetValue((type, name), out var text)
            ? "CREATE OR REPLACE " + OracleSchemaInspector.Unqualify(text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Trim(), owner)
            : null;

        PortableCode PlSql(PortableCodeKind kind, string name, string definition) =>
            new(kind, N(name), definition) { Language = "plsql", DependsOn = DependsOn(name) };

        var functions = new List<PortableCode>();
        var packages = new List<PortableCode>();
        var procedures = new List<PortableCode>();
        foreach (var (type, name) in source.Keys.OrderBy(k => k.Name, StringComparer.Ordinal).ThenBy(k => k.Type, StringComparer.Ordinal))
        {
            if (skip(name))
            {
                continue;
            }

            switch (type)
            {
                case "FUNCTION":
                    functions.Add(PlSql(PortableCodeKind.Function, name, Create(type, name)!));
                    break;
                case "PROCEDURE":
                    procedures.Add(PlSql(PortableCodeKind.Procedure, name, Create(type, name)!));
                    break;
                case "PACKAGE":
                    var body = Create("PACKAGE BODY", name);
                    var definition = Create(type, name) + "\n/\n" + (body is null ? string.Empty : body + "\n/\n");
                    packages.Add(PlSql(PortableCodeKind.Function, name, definition.TrimEnd('\n')));
                    schema.Warnings.Add($"package {N(name)} is converted as one function entry (specification and body) and kept commented out; rewrite it by hand.");
                    break;
                case "TYPE":
                    schema.Warnings.Add($"type {N(name)} is not converted.");
                    break;
            }
        }

        var triggers = new List<PortableCode>();
        foreach (var r in await Q("SELECT trigger_name AS name, table_owner AS towner, table_name AS t, base_object_type AS base FROM all_triggers WHERE owner = :o ORDER BY trigger_name"))
        {
            var (name, table) = (S(r["name"]), S(r["t"]));
            if (skip(name) || Create("TRIGGER", name) is not { } create)
            {
                continue;
            }

            if (S(r["base"]).Trim() is not ("TABLE" or "VIEW") || table.Length == 0)
            {
                schema.Warnings.Add($"trigger {N(name)} on {S(r["base"]).Trim().ToLowerInvariant()} is not converted.");
                continue;
            }

            if (S(r["towner"]) == owner && skip(table))
            {
                continue;
            }

            triggers.Add(PlSql(PortableCodeKind.Trigger, name, create) with { Table = new PortableName(S(r["towner"]), table) });
        }

        schema.Code.AddRange(CodeOrder.Sort(functions.Concat(packages).Concat(views).Concat(procedures).Concat(triggers)));
    }

    /// <summary>START WITH and INCREMENT BY from ALL_TAB_IDENTITY_COLS.IDENTITY_OPTIONS.</summary>
    private static PortableIdentity Identity(string generation, string options)
    {
        long Option(string key, long fallback) =>
            Regex.Match(options, key + @":\s*(-?\d+)") is { Success: true } m && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        return new PortableIdentity(Option("START WITH", 1), Option("INCREMENT BY", 1), generation == "ALWAYS");
    }

    /// <summary>A whole number that fits a long, or null (NUMBER bounds like 1E28 do not).</summary>
    private static long? L(object? value) =>
        decimal.TryParse(S(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= long.MinValue && d <= long.MaxValue ? (long)d : null;

    private static string S(object? value) => value switch
    {
        null => string.Empty,
        _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
    };

    [GeneratedRegex(@"\A""[^""]+""\s+IS\s+NOT\s+NULL\z", RegexOptions.IgnoreCase)]
    private static partial Regex NotNullRegex();

    [GeneratedRegex(@"\A""((?:[^""]|"""")+)""\z")]
    private static partial Regex QuotedNameRegex();

    private sealed class TableParts(PortableName name)
    {
        public PortableName Name { get; } = name;

        public string? Comment { get; set; }

        public List<PortableColumn> Columns { get; } = [];

        public PortableKey? PrimaryKey { get; set; }

        public List<PortableKey> Uniques { get; } = [];

        public List<PortableCheck> Checks { get; } = [];

        public List<PortableForeignKey> ForeignKeys { get; } = [];

        public List<PortableIndex> Indexes { get; } = [];
    }
}
