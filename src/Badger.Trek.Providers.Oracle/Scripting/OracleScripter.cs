using System.Text.RegularExpressions;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Oracle.Scripting;

/// <summary>
/// Scripts the differences between two Oracle schemas: "deploy" turns the first into the second, "revert" the second back
/// into the first. Tables change with ALTER TABLE: columns added, dropped, modified (type, default, NOT NULL) and renamed
/// (same definition under a new name), constraints and indexes dropped and added, comments set. Foreign keys are dropped
/// first and added after every table exists. Sequences change with ALTER SEQUENCE (where they start is data, not
/// schema). Views, synonyms and PL/SQL are dropped and created again (CREATE OR REPLACE, each PL/SQL object followed by
/// a '/' line). Objects that repeatable scripts create are left to them. Oracle has no column positions: a new column is
/// added at the end.
/// </summary>
public sealed partial class OracleScripter : ISchemaScripter
{
    public async Task<SchemaScript> ScriptAsync(IDatabaseSession from, IDatabaseSession to, SchemaScriptOptions options, CancellationToken cancellationToken)
    {
        var patterns = options.Exclude.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        bool Excluded(string name) => patterns.Any(p => p.IsMatch(name) || p.IsMatch(name[(name.IndexOf('.') + 1)..]));

        var source = await OracleModel.ReadAsync(from, options.StateSchema, Excluded, cancellationToken).ConfigureAwait(false);
        var target = await OracleModel.ReadAsync(to, options.StateSchema, Excluded, cancellationToken).ConfigureAwait(false);
        var owned = OwnedObjects(options.RepeatableScripts);

        var deploy = new Generator(source, target, owned).Run();
        var revert = new Generator(target, source, owned).Run();
        var warnings = target.Warnings.Concat(deploy.Warnings).Distinct().ToList();
        if (deploy.Steps.Count > 0)
        {
            warnings.Add("Oracle commits each DDL statement as it runs: if a statement fails, the ones before it stay applied. Review the script, and keep it to what this change needs.");
        }

        return new SchemaScript(Render(deploy.Steps), Render(revert.Steps), deploy.Summary, warnings, deploy.DataLoss);
    }

    private static string Render(IReadOnlyList<string> steps) =>
        steps.Count == 0 ? string.Empty : string.Join("\n\n", steps.Select(s => s.Trim())) + "\n";

    /// <summary>Keys ("view NAME", "package body NAME" ...) of the objects that repeatable scripts create.</summary>
    public static HashSet<string> OwnedObjects(IEnumerable<string> scripts)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            foreach (Match m in DefinitionRegex().Matches(script))
            {
                var kind = Regex.Replace(m.Groups["kind"].Value.ToLowerInvariant(), @"\s+", " ");
                var name = m.Groups["name"].Value;
                name = name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..].Trim() : name;
                owned.Add($"{kind} {OracleConnectionFactory.Normalize(name)}");
            }
        }

        return owned;
    }

    [GeneratedRegex(@"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NON)?EDITIONABLE\s+)?(?:FORCE\s+)?(?<kind>VIEW|PROCEDURE|FUNCTION|PACKAGE\s+BODY|PACKAGE|TRIGGER|TYPE\s+BODY|TYPE|SYNONYM|SEQUENCE)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>(?:""[^""]+""|[A-Za-z][\w$#]*)(?:\s*\.\s*(?:""[^""]+""|[A-Za-z][\w$#]*))?)", RegexOptions.IgnoreCase)]
    private static partial Regex DefinitionRegex();

    private sealed record Result(IReadOnlyList<string> Steps, IReadOnlyList<string> Summary, IReadOnlyList<string> Warnings, bool DataLoss);

    /// <summary>One direction: scripts what turns <c>a</c> into <c>b</c>.</summary>
    private sealed partial class Generator(OracleModel a, OracleModel b, HashSet<string> owned)
    {
        private static readonly string[] CodeOrder = ["sequence", "synonym", "type", "type body", "package", "package body", "function", "procedure", "view", "trigger"];

        private readonly List<string> summary = [];
        private readonly List<string> warnings = [];
        private readonly List<string> dropForeignKeys = [];
        private readonly List<string> dropObjects = [];
        private readonly List<string> dropTables = [];
        private readonly List<string> alterTables = [];
        private readonly List<string> createTables = [];
        private readonly List<string> addForeignKeys = [];
        private readonly List<string> createObjects = [];
        private bool dataLoss;

        public Result Run()
        {
            Tables();
            Objects();
            return new Result([.. dropForeignKeys, .. dropObjects, .. dropTables, .. alterTables, .. createTables, .. addForeignKeys, .. createObjects], summary, warnings, dataLoss);
        }

        private static string Q(string name) => OracleModel.Q(name);

        private void Loss(string what)
        {
            dataLoss = true;
            warnings.Add($"Data loss: {what}.");
        }

        // ------------------------------------------------------------------ tables

        private void Tables()
        {
            foreach (var old in a.Tables.Values.Where(t => !b.Tables.ContainsKey(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                // CASCADE CONSTRAINTS: tables that reference each other are dropped together.
                dropTables.Add($"DROP TABLE {Q(old.Name)} CASCADE CONSTRAINTS PURGE;");
                summary.Add($"- table {old.Name}");
                Loss($"table {old.Name} is dropped");
            }

            foreach (var now in b.Tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (!a.Tables.TryGetValue(now.Name, out var old))
                {
                    createTables.Add(now.CreateWithoutForeignKeys() + ";");
                    summary.Add($"+ table {now.Name}");
                    foreach (var fk in now.Constraints.Where(c => c.ForeignKey))
                    {
                        addForeignKeys.Add($"ALTER TABLE {Q(now.Name)} ADD {fk.Definition};");
                    }

                    foreach (var index in now.Indexes)
                    {
                        createTables.Add(index.Create + ";");
                    }

                    Comments(null, now);
                    continue;
                }

                AlterTable(old, now);
            }

            // Foreign keys to a table that is dropped must go before it, wherever they are, unless their own table goes too.
            foreach (var table in a.Tables.Values.Where(t => b.Tables.ContainsKey(t.Name)))
            {
                foreach (var fk in table.Constraints.Where(c => c.ForeignKey && c.References is { } r && !b.Tables.ContainsKey(r)))
                {
                    if (dropForeignKeys.Any(d => d.Contains(Q(fk.Name), StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    warnings.Add($"Table {table.Name} has foreign key {fk.Name} to {fk.References}, which is dropped: the script drops the foreign key first.");
                    dropForeignKeys.Add($"ALTER TABLE {Q(table.Name)} DROP CONSTRAINT {Q(fk.Name)};");
                }
            }
        }

        private void AlterTable(OracleTable old, OracleTable now)
        {
            var t = Q(now.Name);
            bool Same(OracleConstraint x, OracleConstraint y) => x.Identity == y.Identity && x.Body == y.Body;

            // Foreign keys: changed or removed ones go first, new or changed ones are added last.
            foreach (var fk in old.Constraints.Where(c => c.ForeignKey && !now.Constraints.Any(n => Same(n, c))))
            {
                dropForeignKeys.Add($"ALTER TABLE {t} DROP CONSTRAINT {Q(fk.Name)};");
                if (!now.Constraints.Any(n => n.Identity == fk.Identity))
                {
                    summary.Add($"- foreign key {now.Name}.{Show(fk)}");
                }
            }

            foreach (var fk in now.Constraints.Where(c => c.ForeignKey && !old.Constraints.Any(o => Same(o, c))))
            {
                addForeignKeys.Add($"ALTER TABLE {t} ADD {fk.Definition};");
                summary.Add(old.Constraints.Any(o => o.Identity == fk.Identity) ? $"~ foreign key {now.Name}.{Show(fk)}" : $"+ foreign key {now.Name}.{Show(fk)}");
            }

            // Other constraints and indexes that change or go away are dropped before the columns change.
            foreach (var c in old.Constraints.Where(c => !c.ForeignKey && !now.Constraints.Any(n => Same(n, c))))
            {
                alterTables.Add($"ALTER TABLE {t} DROP CONSTRAINT {Q(c.Name)};");
                if (!now.Constraints.Any(n => n.Identity == c.Identity))
                {
                    summary.Add($"- {Kind(c)} {now.Name}.{Show(c)}");
                }
            }

            foreach (var index in old.Indexes.Where(i => !now.Indexes.Any(n => n.Name == i.Name && n.Create == i.Create)))
            {
                alterTables.Add($"DROP INDEX {Q(index.Name)};");
                if (!now.Indexes.Any(n => n.Name == index.Name))
                {
                    summary.Add($"- index {now.Name}.{index.Name}");
                }
            }

            Columns(old, now);

            foreach (var c in now.Constraints.Where(c => !c.ForeignKey && !old.Constraints.Any(o => Same(o, c))))
            {
                alterTables.Add($"ALTER TABLE {t} ADD {c.Definition};");
                summary.Add(old.Constraints.Any(o => o.Identity == c.Identity) ? $"~ {Kind(c)} {now.Name}.{Show(c)}" : $"+ {Kind(c)} {now.Name}.{Show(c)}");
            }

            foreach (var index in now.Indexes.Where(i => !old.Indexes.Any(o => o.Name == i.Name && o.Create == i.Create)))
            {
                alterTables.Add(index.Create + ";");
                summary.Add(old.Indexes.Any(o => o.Name == index.Name) ? $"~ index {now.Name}.{index.Name}" : $"+ index {now.Name}.{index.Name}");
            }

            Comments(old, now);
        }

        private static string Kind(OracleConstraint c) => c.Kind switch { "P" => "primary key", "U" => "unique", _ => "check" };

        private static string Show(OracleConstraint c) => c.Generated ? c.Body : c.Name;

        private void Comments(OracleTable? old, OracleTable now)
        {
            var t = Q(now.Name);
            if (now.Comment != old?.Comment && (now.Comment is not null || old is not null))
            {
                alterTables.Add($"COMMENT ON TABLE {t} IS {OracleModel.Literal(now.Comment ?? string.Empty)};");
                summary.Add($"~ comment {now.Name}");
            }

            var oldComments = old?.ColumnComments ?? new Dictionary<string, string>();
            foreach (var column in now.ColumnComments.Keys.Union(oldComments.Keys).Order(StringComparer.Ordinal))
            {
                var text = now.ColumnComments.GetValueOrDefault(column);
                if (text != oldComments.GetValueOrDefault(column) && now.Columns.Any(c => c.Name == column))
                {
                    alterTables.Add($"COMMENT ON COLUMN {t}.{Q(column)} IS {OracleModel.Literal(text ?? string.Empty)};");
                    summary.Add($"~ comment {now.Name}.{column}");
                }
            }
        }

        /// <summary>Adds, drops, renames and modifies columns so the table ends up with <paramref name="now"/>'s columns.</summary>
        private void Columns(OracleTable old, OracleTable now)
        {
            var t = Q(now.Name);
            var oldByName = old.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var newByName = now.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

            // A column that disappears while a new one with exactly the same definition appears is a rename, when each is
            // the other's only match.
            var removed = old.Columns.Where(c => !newByName.ContainsKey(c.Name)).ToList();
            var added = now.Columns.Where(c => !oldByName.ContainsKey(c.Name)).ToList();
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var o in removed)
            {
                var matches = added.Where(n => n.Rest == o.Rest).ToList();
                if (matches.Count == 1 && removed.Count(r => r.Rest == o.Rest) == 1)
                {
                    renamed[o.Name] = matches[0].Name;
                }
            }

            foreach (var (from, to) in renamed)
            {
                alterTables.Add($"ALTER TABLE {t} RENAME COLUMN {Q(from)} TO {Q(to)};");
                summary.Add($"~ column {now.Name}.{from} renamed to {to}");
            }

            foreach (var column in removed.Where(c => !renamed.ContainsKey(c.Name)))
            {
                alterTables.Add($"ALTER TABLE {t} DROP COLUMN {Q(column.Name)};");
                summary.Add($"- column {now.Name}.{column.Name}");
                Loss($"column {now.Name}.{column.Name} is dropped");
            }

            foreach (var column in added.Where(c => !renamed.ContainsValue(c.Name)))
            {
                alterTables.Add($"ALTER TABLE {t} ADD ({column.Definition});");
                summary.Add($"+ column {now.Name}.{column.Name}");
                if (!column.Nullable && column.Default is null && column.Identity is null && !column.Virtual)
                {
                    warnings.Add($"Column {now.Name}.{column.Name} is NOT NULL without a default: adding it fails while the table has rows. Add a default, or add it nullable, fill it and then make it NOT NULL.");
                }
            }

            foreach (var column in now.Columns.Where(c => oldByName.ContainsKey(c.Name)))
            {
                var before = oldByName[column.Name];
                if (before.Rest == column.Rest)
                {
                    continue;
                }

                if (before.Virtual != column.Virtual || before.Identity != column.Identity || before.Virtual)
                {
                    warnings.Add($"Column {now.Name}.{column.Name} changes from '{before.Rest}' to '{column.Rest}': identity and virtual columns are not altered by the script. Drop and add the column, or use ALTER TABLE ... MODIFY ... by hand.");
                    summary.Add($"~ column {now.Name}.{column.Name} (not scripted)");
                    continue;
                }

                var parts = new List<string>();
                if (before.Type != column.Type)
                {
                    parts.Add(column.Type);
                    Loss($"column {now.Name}.{column.Name} changes type from {before.Type} to {column.Type}; values that do not fit are rejected");
                    if (before.Type.StartsWith("NUMBER(", StringComparison.Ordinal) && column.Type.StartsWith("NUMBER(", StringComparison.Ordinal))
                    {
                        warnings.Add($"Column {now.Name}.{column.Name} changes from {before.Type} to {column.Type}: Oracle lowers a NUMBER's precision or scale only while the column is empty (ORA-01440). Copy the values to a new column instead if the table has rows.");
                    }
                }

                if (before.Default != column.Default || before.DefaultOnNull != column.DefaultOnNull)
                {
                    parts.Add($"DEFAULT{(column.DefaultOnNull ? " ON NULL" : string.Empty)} {column.Default ?? "NULL"}");
                }

                if (before.Nullable != column.Nullable)
                {
                    parts.Add(column.Nullable ? "NULL" : "NOT NULL");
                    if (!column.Nullable)
                    {
                        warnings.Add($"Column {now.Name}.{column.Name} becomes NOT NULL: the statement fails while any row has NULL there. Fill it first.");
                    }
                }

                alterTables.Add($"ALTER TABLE {t} MODIFY ({Q(column.Name)} {string.Join(' ', parts)});");
                summary.Add($"~ column {now.Name}.{column.Name}: {before.Rest} -> {column.Rest}");
            }
        }

        // ------------------------------------------------------------------ views, sequences, synonyms, PL/SQL

        private void Objects()
        {
            bool Owned(OracleObject o) => owned.Contains(o.Key);
            bool Changed(OracleObject o, OracleObject n) =>
                o.Kind == "sequence" ? a.SequenceOptionsByName.GetValueOrDefault(o.Name) != b.SequenceOptionsByName.GetValueOrDefault(n.Name) : o.Create != n.Create;

            foreach (var old in a.Objects.Values.Where(o => !Owned(o) && !b.Objects.ContainsKey(o.Key)).OrderByDescending(o => Array.IndexOf(CodeOrder, o.Kind)).ThenBy(o => o.Name, StringComparer.Ordinal))
            {
                // A trigger goes with its table; a package body with its package; a type body with its type.
                if ((old.Kind == "trigger" && old.Table is { } table && !b.Tables.ContainsKey(table))
                    || (old.Kind is "package body" or "type body" && a.Objects.ContainsKey(old.Key.Replace(" body", string.Empty, StringComparison.Ordinal)) && !b.Objects.ContainsKey(old.Key.Replace(" body", string.Empty, StringComparison.Ordinal))))
                {
                    summary.Add($"- {old.Kind} {old.Name}");
                    continue;
                }

                dropObjects.Add($"DROP {old.Kind.ToUpperInvariant()} {Q(old.Name)}{(old.Kind is "type" ? " FORCE" : string.Empty)};");
                summary.Add($"- {old.Kind} {old.Name}");
            }

            foreach (var now in b.Objects.Values.Where(o => !Owned(o)).OrderBy(o => Array.IndexOf(CodeOrder, o.Kind)).ThenBy(o => o.Name, StringComparer.Ordinal))
            {
                if (a.Objects.TryGetValue(now.Key, out var old))
                {
                    if (!Changed(old, now))
                    {
                        continue;
                    }

                    if (now.Kind == "sequence")
                    {
                        createObjects.Add($"ALTER SEQUENCE {Q(now.Name)} {b.SequenceOptionsByName[now.Name]};");
                        summary.Add($"~ sequence {now.Name}");
                        continue;
                    }

                    createObjects.Add(Statement(now));
                    summary.Add($"~ {now.Kind} {now.Name}");
                    continue;
                }

                createObjects.Add(Statement(now));
                summary.Add($"+ {now.Kind} {now.Name}");
            }
        }

        /// <summary>A PL/SQL object is followed by a '/' line, as SQL*Plus needs.</summary>
        private static string Statement(OracleObject o) => o.PlSql ? o.Create.TrimEnd() + "\n/" : o.Create.TrimEnd().TrimEnd(';') + ";";
    }
}
