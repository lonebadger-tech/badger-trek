using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.Oracle.Scripting;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace Badger.Trek.Providers.Oracle.Conversion;

/// <summary>
/// Reads tables with a streaming reader and loads them with array-bound INSERT statements. Values read are
/// vendor-neutral CLR values: whole numbers (INTEGER, NUMBER(p) with p ≤ 18) as long, other NUMBER and FLOAT as decimal
/// (as their exact text when System.Decimal cannot hold them), BINARY_FLOAT as float, BINARY_DOUBLE as double, text, LOBs,
/// JSON and XMLTYPE as string, DATE and TIMESTAMP as DateTime (unspecified kind), TIMESTAMP WITH TIME ZONE as
/// DateTimeOffset, TIMESTAMP WITH LOCAL TIME ZONE as DateTime in UTC, RAW and BLOB as byte[], INTERVAL DAY TO SECOND as
/// TimeSpan, INTERVAL YEAR TO MONTH as "years-months" text, BOOLEAN as bool.
/// <para>
/// The load runs in one transaction with deferrable constraints deferred (they are checked at the commit). Oracle commits
/// DDL implicitly, so the target tables' triggers are disabled, and GENERATED ALWAYS identities switched to BY DEFAULT,
/// before the transaction opens; both are restored when the load ends or fails.
/// </para>
/// </summary>
public sealed partial class OracleDataTransfer : IDataTransfer
{
    /// <summary>Rows per INSERT execution.</summary>
    private const int RowsPerBatch = 1000;

    /// <summary>Approximate bound data per execution, for rows with large LOBs.</summary>
    private const long BytesPerBatch = 8 * 1024 * 1024;

    /// <summary>What BeginLoadAsync changed, to put back when the load ends or fails.</summary>
    private LoadState? load;

    private enum Kind
    {
        Text,
        NText,
        Clob,
        NClob,
        Long,
        Integer,
        Number,
        BinaryFloat,
        BinaryDouble,
        Date,
        Timestamp,
        TimestampTz,
        TimestampLtz,
        IntervalDs,
        IntervalYm,
        Raw,
        Blob,
        LongRaw,
        Json,
        Xml,
        Boolean,
        RowId,
    }

    private static OracleSession Session(IDatabaseSession session) => session as OracleSession ?? throw new ArgumentException("Not an Oracle session.", nameof(session));

    /// <summary>The table in the deploy schema; names are exactly as the dictionary stores them.</summary>
    private static string Name(IDatabaseSession session, PortableName table) =>
        OracleStateStore.Quote(OracleStateStore.Owner(session)) + "." + OracleStateStore.Quote(table.Name);

    private static string Q(string name) => OracleStateStore.Quote(name);

    public async Task<IReadOnlyList<TransferColumn>> ColumnsAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("""
            SELECT column_name AS c, data_type AS type, data_length AS len, char_length AS clen, char_used AS cused, data_precision AS prec, data_scale AS scale
              FROM all_tab_cols
             WHERE owner = :o AND table_name = :t AND hidden_column = 'NO'
               AND (virtual_column = 'NO' OR data_type = 'XMLTYPE')
             ORDER BY column_id
            """, new Dictionary<string, object?> { ["o"] = OracleStateStore.Owner(session), ["t"] = table.Name }, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new TransferColumn(Convert.ToString(r["c"], CultureInfo.InvariantCulture)!, OracleModel.ColumnType(r))).ToList();
    }

    public async Task<long> CountAsync(IDatabaseSession session, PortableName table, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT COUNT(*) AS n FROM {Name(session, table)}", null, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<object?[]> ReadAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var s = Session(session);
        var kinds = columns.Select(c => KindOf(c.Type)).ToArray();
        var select = string.Join(", ", columns.Select((c, i) => kinds[i] switch
        {
            Kind.Json => $"JSON_SERIALIZE({Q(c.Name)} RETURNING CLOB)",
            Kind.Xml => $"XMLSERIALIZE(CONTENT {Q(c.Name)} AS CLOB)",
            Kind.TimestampLtz => $"SYS_EXTRACT_UTC(CAST({Q(c.Name)} AS TIMESTAMP WITH TIME ZONE))",
            Kind.IntervalYm => $"TO_CHAR({Q(c.Name)})",
            Kind.RowId => $"ROWIDTOCHAR({Q(c.Name)})",
            _ => Q(c.Name),
        } + $" AS c{i.ToString(CultureInfo.InvariantCulture)}"));
        await using var command = new OracleCommand($"SELECT {select} FROM {Name(session, table)}", s.Connection)
        {
            BindByName = true,
            InitialLOBFetchSize = -1,
            InitialLONGFetchSize = -1,
            CommandTimeout = 0,
        };
        if (s.Transaction is not null)
        {
            command.Transaction = s.Transaction;
        }

        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        reader.FetchSize = Math.Clamp(reader.RowSize * 2000L, 1024 * 1024, 64 * 1024 * 1024);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Cell(reader, i, kinds[i], columns[i].Type);
            }

            yield return row;
        }
    }

    /// <summary>One value, as a vendor-neutral CLR value.</summary>
    private static object Cell(OracleDataReader reader, int i, Kind kind, string type)
    {
        switch (kind)
        {
            case Kind.Integer:
            case Kind.Number:
                decimal value;
                try
                {
                    value = reader.GetDecimal(i);
                }
                catch (Exception ex) when (ex is InvalidCastException or OverflowException)
                {
                    // More digits than System.Decimal holds: keep the exact text.
                    return reader.GetOracleDecimal(i).ToString();
                }

                if (kind == Kind.Integer && value >= long.MinValue && value <= long.MaxValue)
                {
                    return (long)value;
                }

                return value;
            case Kind.BinaryFloat:
                return reader.GetFloat(i);
            case Kind.BinaryDouble:
                return reader.GetDouble(i);
            case Kind.Date:
            case Kind.Timestamp:
                return DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Unspecified);
            case Kind.TimestampLtz:
                return DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc);
            case Kind.TimestampTz:
                var tz = reader.GetOracleTimeStampTZ(i);
                return new DateTimeOffset(DateTime.SpecifyKind(tz.Value, DateTimeKind.Unspecified), tz.GetTimeZoneOffset());
            case Kind.IntervalDs:
                return reader.GetTimeSpan(i);
            case Kind.IntervalYm:
                return YearMonth(reader.GetString(i), type);
            case Kind.Raw:
            case Kind.Blob:
            case Kind.LongRaw:
                return reader.GetValue(i) is byte[] bytes ? bytes : reader.GetOracleBinary(i).Value;
            case Kind.Boolean:
                return Convert.ToBoolean(reader.GetValue(i), CultureInfo.InvariantCulture);
            default:
                return reader.GetValue(i) is string text ? text : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>"+01-02" (TO_CHAR of an INTERVAL YEAR TO MONTH) as "1-2", the SQL standard's year-month literal.</summary>
    private static string YearMonth(string text, string type)
    {
        var m = YearMonthRegex().Match(text.Trim());
        if (!m.Success)
        {
            throw new MigrationException($"Unexpected {type} value '{text}'.");
        }

        var years = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var months = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return (m.Groups[1].Value == "-" ? "-" : string.Empty) + years.ToString(CultureInfo.InvariantCulture) + "-" + months.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<long> WriteAsync(IDatabaseSession session, PortableName table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken cancellationToken)
    {
        var s = Session(session);
        var kinds = columns.Select(c => KindOf(c.Type)).ToArray();
        var values = string.Join(", ", columns.Select((c, i) =>
        {
            var p = ":p" + i.ToString(CultureInfo.InvariantCulture);
            return kinds[i] switch
            {
                Kind.IntervalYm => $"TO_YMINTERVAL({p})",
                Kind.Xml => $"CASE WHEN {p} IS NULL THEN NULL ELSE XMLTYPE({p}) END",
                _ => p,
            };
        }));
        var sql = $"INSERT INTO {Name(session, table)} ({string.Join(", ", columns.Select(c => Q(c.Name)))}) VALUES ({values})";
        var batch = new List<object?[]>(RowsPerBatch);
        var (bytes, written) = (0L, 0L);

        async Task FlushAsync()
        {
            await using var command = new OracleCommand(sql, s.Connection) { BindByName = true, ArrayBindCount = batch.Count, CommandTimeout = 0 };
            if (s.Transaction is not null)
            {
                command.Transaction = s.Transaction;
            }

            for (var c = 0; c < columns.Count; c++)
            {
                var array = new object[batch.Count];
                for (var r = 0; r < batch.Count; r++)
                {
                    array[r] = batch[r][c] ?? DBNull.Value;
                }

                command.Parameters.Add(new OracleParameter("p" + c.ToString(CultureInfo.InvariantCulture), DbType(kinds[c])) { Value = array });
            }

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException ex)
            {
                throw Explain(ex, table, columns, batch);
            }

            written += batch.Count;
            batch.Clear();
            bytes = 0;
        }

        try
        {
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var converted = new object?[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    var value = Value(i < row.Length ? row[i] : null, kinds[i], table, columns[i]);
                    converted[i] = value;
                    bytes += value switch
                    {
                        string text => text.Length * 2L,
                        byte[] data => data.Length,
                        _ => 16,
                    };
                }

                batch.Add(converted);
                if (batch.Count >= RowsPerBatch || bytes >= BytesPerBatch)
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }

            if (batch.Count > 0)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Roll back and put the triggers and identities back: the caller does not end a load that failed.
            var rolledBack = session.InTransaction;
            await AbortAsync(session).ConfigureAwait(false);
            if (ex is System.Data.Common.DbException db)
            {
                throw new MigrationException($"Loading {table.Name} failed{(rolledBack ? " and the load was rolled back" : string.Empty)}: {db.Message}", db);
            }

            throw;
        }

        return written;
    }

    /// <summary>A failed INSERT as a message that says what to do about it.</summary>
    private static Exception Explain(OracleException ex, PortableName table, IReadOnlyList<TransferColumn> columns, List<object?[]> batch)
    {
        var messages = new[] { ex.Message }.Concat(ex.Errors.Cast<OracleError>().Select(e => e.Message)).ToList();
        var numbers = new[] { ex.Number }.Concat(ex.Errors.Cast<OracleError>().Select(e => e.Number)).ToHashSet();
        if (numbers.Contains(1400))
        {
            var column = messages.Select(m => NullColumnRegex().Match(m)).FirstOrDefault(m => m.Success)?.Groups[3].Value;
            var index = column is null ? -1 : columns.ToList().FindIndex(c => c.Name == column);
            var emptyStrings = index >= 0 ? batch.Any(r => r[index] is "") : batch.Any(r => r.Any(v => v is ""));
            if (emptyStrings)
            {
                return new MigrationException(
                    $"{table.Name}.{column ?? "?"}: the source has empty strings, which Oracle stores as NULL, and the column is NOT NULL. " +
                    "Make the column nullable or replace the empty strings in the source, then load again.", ex);
            }

            return new MigrationException($"{table.Name}.{column ?? "?"}: a row has no value for this NOT NULL column. {ex.Message}", ex);
        }

        if (numbers.Contains(2291) && messages.Select(m => ConstraintRegex().Match(m)).FirstOrDefault(m => m.Success) is { } fk)
        {
            return new MigrationException($"{table.Name}: a row breaks foreign key {Bare(fk.Groups[1].Value)} (no matching parent row), and the constraint is not deferrable. Fix the source data and load again.", ex);
        }

        return new MigrationException($"Loading {table.Name} failed: {ex.Message}", ex);
    }

    private static OracleDbType DbType(Kind kind) => kind switch
    {
        Kind.Text or Kind.IntervalYm or Kind.RowId => OracleDbType.Varchar2,
        Kind.NText => OracleDbType.NVarchar2,
        Kind.Clob or Kind.Json or Kind.Xml => OracleDbType.Clob,
        Kind.NClob => OracleDbType.NClob,
        Kind.Long => OracleDbType.Long,
        Kind.Integer or Kind.Number => OracleDbType.Decimal,
        Kind.BinaryFloat => OracleDbType.BinaryFloat,
        Kind.BinaryDouble => OracleDbType.BinaryDouble,
        Kind.Date => OracleDbType.Date,
        Kind.Timestamp => OracleDbType.TimeStamp,
        Kind.TimestampTz or Kind.TimestampLtz => OracleDbType.TimeStampTZ,
        Kind.IntervalDs => OracleDbType.IntervalDS,
        Kind.Raw => OracleDbType.Raw,
        Kind.Blob => OracleDbType.Blob,
        Kind.LongRaw => OracleDbType.LongRaw,
        Kind.Boolean => OracleDbType.Boolean,
        _ => OracleDbType.Varchar2,
    };

    /// <summary>
    /// A value from any vendor as an ODP.NET parameter value for the column: booleans as 1/0 in numbers, GUIDs as their
    /// 36-character text (16 bytes in RFC 4122 order for RAW), DateTimeOffset as UTC in TIMESTAMP and DATE, text in the
    /// PostgreSQL / ISO forms for intervals and times.
    /// </summary>
    private static object? Value(object? value, Kind kind, PortableName table, TransferColumn column)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        try
        {
            return kind switch
            {
                Kind.Integer or Kind.Number => Number(value),
                Kind.BinaryFloat => value switch
                {
                    float f => f,
                    bool b => b ? 1f : 0f,
                    string text => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                    _ => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                },
                Kind.BinaryDouble => value switch
                {
                    double d => d,
                    bool b => b ? 1d : 0d,
                    string text => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                    _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                },
                Kind.Date or Kind.Timestamp => Timestamp(value),
                Kind.TimestampTz => Zoned(value, utc: false),
                Kind.TimestampLtz => Zoned(value, utc: true),
                Kind.IntervalDs => value switch
                {
                    TimeSpan t => t,
                    TimeOnly t => t.ToTimeSpan(),
                    string text => Interval(text),
                    _ => throw new FormatException(),
                },
                Kind.IntervalYm => value switch
                {
                    string text => text.Trim(),
                    _ => YearsMonths(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                },
                Kind.Raw or Kind.Blob or Kind.LongRaw => value switch
                {
                    byte[] bytes => bytes,
                    Guid g => g.ToByteArray(bigEndian: true),
                    string text when Guid.TryParse(text, out var g) => g.ToByteArray(bigEndian: true),
                    string text => Encoding.UTF8.GetBytes(text),
                    _ => throw new FormatException(),
                },
                Kind.Boolean => value switch
                {
                    bool b => b,
                    string text => text.Trim().ToLowerInvariant() is "1" or "t" or "true" or "y" or "yes" or "on",
                    _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
                },
                _ => Text(value),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new MigrationException($"{table.Name}.{column.Name}: {Describe(value)} cannot be stored in {column.Type}.", ex);
        }
    }

    private static string Describe(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return $"the {value.GetType().Name} value '{(text.Length > 50 ? text[..50] + "..." : text)}'";
    }

    /// <summary>A number as decimal, or as OracleDecimal when it has more digits than System.Decimal holds.</summary>
    private static object Number(object value) => value switch
    {
        bool b => b ? 1m : 0m,
        decimal d => d,
        float f when float.IsFinite(f) => new OracleDecimal((double)f),
        double d when double.IsFinite(d) => new OracleDecimal(d),
        float or double => throw new FormatException(),
        string text when Digits(text) <= 28 && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        string text => OracleDecimal.Parse(text.Trim()),
        Guid => throw new FormatException(),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Significant digits of a number's text (System.Decimal rounds beyond 28 without saying so).</summary>
    private static int Digits(string text)
    {
        var mantissa = text.Trim().Split('e', 'E')[0];
        return mantissa.Where(char.IsAsciiDigit).SkipWhile(c => c == '0').Count();
    }

    private static DateTime Timestamp(object value) => value switch
    {
        DateTime { Kind: DateTimeKind.Local } d => DateTime.SpecifyKind(d.ToUniversalTime(), DateTimeKind.Unspecified),
        DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Unspecified),
        DateTimeOffset o => DateTime.SpecifyKind(o.UtcDateTime, DateTimeKind.Unspecified),
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
        _ => throw new FormatException(),
    };

    /// <summary>A TIMESTAMP WITH (LOCAL) TIME ZONE value; times without a zone are taken as UTC.</summary>
    private static OracleTimeStampTZ Zoned(object value, bool utc)
    {
        var offset = value switch
        {
            DateTimeOffset o => o,
            DateTime { Kind: DateTimeKind.Local } d => new DateTimeOffset(d.ToUniversalTime()),
            DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeSpan.Zero),
            DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            _ => throw new FormatException(),
        };
        if (utc)
        {
            offset = offset.ToUniversalTime();
        }

        var zone = (offset.Offset < TimeSpan.Zero ? "-" : "+") + offset.Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        return new OracleTimeStampTZ(offset.DateTime, zone);
    }

    /// <summary>
    /// Interval text: .NET's [-][d.]hh:mm:ss[.fffffff], or PostgreSQL's "3 days 04:05:06.5" (without months or years,
    /// which a day-to-second interval cannot hold).
    /// </summary>
    private static TimeSpan Interval(string text)
    {
        text = text.Trim();
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
        {
            return span;
        }

        var m = PgIntervalRegex().Match(text);
        if (!m.Success)
        {
            throw new FormatException();
        }

        var days = m.Groups["d"].Success ? long.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) : 0;
        var time = m.Groups["t"].Success ? TimeSpan.Parse(m.Groups["t"].Value.TrimStart('-', '+'), CultureInfo.InvariantCulture) : TimeSpan.Zero;
        if (m.Groups["t"].Value.StartsWith('-'))
        {
            time = -time;
        }

        return TimeSpan.FromDays(days) + time;
    }

    private static string YearsMonths(long months) =>
        (months < 0 ? "-" : string.Empty) + (Math.Abs(months) / 12).ToString(CultureInfo.InvariantCulture) + "-" + (Math.Abs(months) % 12).ToString(CultureInfo.InvariantCulture);

    private static string Text(object value) => value switch
    {
        string s => s,
        Guid g => g.ToString("D"),
        bool b => b ? "true" : "false",
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// TRUNCATE TABLE each table, with the foreign keys that reference them disabled meanwhile, so tables that others
    /// reference can be emptied in any order. A foreign key whose child table still has rows (it was not truncated) is
    /// enabled without validating them, and the truncate fails with a message naming it. It commits implicitly.
    /// </summary>
    public async Task TruncateAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        if (tables.Count == 0)
        {
            return;
        }

        if (session.InTransaction)
        {
            throw new MigrationException("Truncating Oracle tables commits implicitly; it cannot run inside a transaction.");
        }

        var owner = OracleStateStore.Owner(session);
        var names = tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var keys = (await session.QueryAsync("""
            SELECT c.table_name AS t, c.constraint_name AS name, p.table_name AS parent
              FROM all_constraints c
              JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name
             WHERE c.owner = :o AND p.owner = :o AND c.constraint_type = 'R' AND c.status = 'ENABLED'
            """, new Dictionary<string, object?> { ["o"] = owner }, cancellationToken).ConfigureAwait(false))
            .Where(r => names.Contains(S(r["parent"])))
            .Select(r => (Table: S(r["t"]), Name: S(r["name"])))
            .ToList();

        var disabled = new List<(string Table, string Name)>();
        var problems = new List<string>();
        try
        {
            foreach (var key in keys)
            {
                await session.ExecuteAsync($"ALTER TABLE {Q(owner)}.{Q(key.Table)} DISABLE CONSTRAINT {Q(key.Name)}", null, cancellationToken).ConfigureAwait(false);
                disabled.Add(key);
            }

            foreach (var table in tables)
            {
                await session.ExecuteAsync($"TRUNCATE TABLE {Name(session, table)}", null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var key in disabled)
            {
                try
                {
                    await session.ExecuteAsync($"ALTER TABLE {Q(owner)}.{Q(key.Table)} ENABLE CONSTRAINT {Q(key.Name)}", null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OracleException ex) when (ex.Number == 2298)
                {
                    await session.ExecuteAsync($"ALTER TABLE {Q(owner)}.{Q(key.Table)} ENABLE NOVALIDATE CONSTRAINT {Q(key.Name)}", null, CancellationToken.None).ConfigureAwait(false);
                    problems.Add($"{key.Table} still has rows that reference a truncated table, so foreign key {key.Name} is enabled without checking them (NOVALIDATE)");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new MigrationException(string.Join("; ", problems) + ". Truncate those tables too.");
        }
    }

    /// <summary>
    /// Disables the enabled triggers on the target tables and makes GENERATED ALWAYS identities BY DEFAULT (both DDL,
    /// which commits: the session must not be in a transaction), then opens the load's transaction with every deferrable
    /// constraint deferred to the commit.
    /// </summary>
    public async Task BeginLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        Session(session);
        if (session.InTransaction)
        {
            throw new MigrationException("Loading Oracle tables disables their triggers, which commits implicitly; it cannot start inside a transaction.");
        }

        if (load is not null)
        {
            await RestoreAsync(session, load, moveIdentities: false).ConfigureAwait(false);
        }

        var owner = OracleStateStore.Owner(session);
        var names = tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var p = new Dictionary<string, object?> { ["o"] = owner };
        var state = new LoadState(owner);
        state.Triggers.AddRange((await session.QueryAsync(
                "SELECT trigger_name AS name, table_name AS t FROM all_triggers WHERE owner = :o AND table_owner = :o AND base_object_type = 'TABLE' AND status = 'ENABLED' ORDER BY trigger_name",
                p, cancellationToken).ConfigureAwait(false))
            .Where(r => names.Contains(S(r["t"])))
            .Select(r => S(r["name"])));
        state.Identities.AddRange((await session.QueryAsync("""
                SELECT i.table_name AS t, i.column_name AS c, i.generation_type AS gen, c.default_on_null AS donull
                  FROM all_tab_identity_cols i
                  JOIN all_tab_cols c ON c.owner = i.owner AND c.table_name = i.table_name AND c.column_name = i.column_name
                 WHERE i.owner = :o
                """, p, cancellationToken).ConfigureAwait(false))
            .Where(r => names.Contains(S(r["t"])))
            .Select(r => new IdentityColumn(S(r["t"]), S(r["c"]), S(r["gen"]) == "ALWAYS", S(r["donull"]) == "YES")));
        load = state;

        try
        {
            foreach (var trigger in state.Triggers)
            {
                await session.ExecuteAsync($"ALTER TRIGGER {Q(owner)}.{Q(trigger)} DISABLE", null, cancellationToken).ConfigureAwait(false);
                state.Disabled.Add(trigger);
            }

            foreach (var identity in state.Identities.Where(i => i.Always))
            {
                await session.ExecuteAsync($"ALTER TABLE {Q(owner)}.{Q(identity.Table)} MODIFY ({Q(identity.Column)} GENERATED BY DEFAULT AS IDENTITY)", null, cancellationToken).ConfigureAwait(false);
                state.Switched.Add(identity);
            }

            await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await session.ExecuteAsync("SET CONSTRAINTS ALL DEFERRED", null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Commits (deferred foreign keys are checked then: a row without its parent rolls the whole load back and fails it),
    /// re-enables the triggers, restores GENERATED ALWAYS, and moves identities and the sequences behind NEXTVAL column
    /// defaults past the loaded values.
    /// </summary>
    public async Task EndLoadAsync(IDatabaseSession session, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        var state = load ?? new LoadState(OracleStateStore.Owner(session));
        load = null;
        try
        {
            if (session.InTransaction)
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OracleException ex)
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await RestoreAsync(session, state, moveIdentities: false).ConfigureAwait(false);
            if (ConstraintRegex().Match(ex.Message) is { Success: true } m)
            {
                var bare = Bare(m.Groups[1].Value);
                var table = (await session.QueryAsync("SELECT table_name AS t FROM all_constraints WHERE owner = :o AND constraint_name = :n",
                    new Dictionary<string, object?> { ["o"] = state.Owner, ["n"] = bare }, CancellationToken.None).ConfigureAwait(false)).FirstOrDefault()?["t"];
                var what = ex.Message.Contains("ORA-02292", StringComparison.Ordinal) ? "a parent row that child rows still reference" : "a row without its parent row";
                throw new MigrationException(
                    $"{(table is null ? string.Empty : S(table) + ": ")}foreign key {bare} found {what} when the load committed; the whole load was rolled back. Fix the source data and load again.", ex);
            }

            throw new MigrationException($"Committing the load failed, and it was rolled back: {ex.Message}", ex);
        }
        catch
        {
            await RestoreAsync(session, state, moveIdentities: false).ConfigureAwait(false);
            throw;
        }

        await RestoreAsync(session, state, moveIdentities: true).ConfigureAwait(false);
        await MoveSequencesAsync(session, state.Owner, tables, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>After a failure: rolls the load back and restores the triggers and identities.</summary>
    private async Task AbortAsync(IDatabaseSession session)
    {
        if (session.InTransaction)
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (load is { } state)
        {
            load = null;
            await RestoreAsync(session, state, moveIdentities: false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-enables the triggers BeginLoadAsync disabled, restores GENERATED ALWAYS, and (after a successful load) moves
    /// every identity of the loaded tables past the largest value (START WITH LIMIT VALUE). Runs outside the transaction.
    /// </summary>
    private static async Task RestoreAsync(IDatabaseSession session, LoadState state, bool moveIdentities)
    {
        var errors = new List<string>();
        async Task Run(string sql)
        {
            try
            {
                await session.ExecuteAsync(sql, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OracleException ex)
            {
                errors.Add($"{sql}: {ex.Message}");
            }
        }

        var owner = Q(state.Owner);
        foreach (var trigger in state.Disabled)
        {
            await Run($"ALTER TRIGGER {owner}.{Q(trigger)} ENABLE").ConfigureAwait(false);
        }

        state.Disabled.Clear();
        foreach (var identity in moveIdentities ? state.Identities : state.Switched)
        {
            var generation = identity.Always ? "ALWAYS" : identity.OnNull ? "BY DEFAULT ON NULL" : "BY DEFAULT";
            await Run($"ALTER TABLE {owner}.{Q(identity.Table)} MODIFY ({Q(identity.Column)} GENERATED {generation} AS IDENTITY{(moveIdentities ? " (START WITH LIMIT VALUE)" : string.Empty)})").ConfigureAwait(false);
        }

        state.Switched.Clear();
        if (errors.Count > 0)
        {
            throw new MigrationException("Restoring the target tables after the load failed: " + string.Join("; ", errors));
        }
    }

    /// <summary>Restarts each sequence that a loaded column's default draws from (seq.NEXTVAL) after the column's largest value.</summary>
    private static async Task MoveSequencesAsync(IDatabaseSession session, string owner, IReadOnlyList<PortableName> tables, CancellationToken cancellationToken)
    {
        var names = tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var p = new Dictionary<string, object?> { ["o"] = owner };
        var sequences = (await session.QueryAsync("SELECT sequence_name AS name, increment_by AS inc FROM all_sequences WHERE sequence_owner = :o", p, cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => S(r["name"]), r => Convert.ToDecimal(r["inc"], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var uses = new Dictionary<string, List<(string Table, string Column)>>(StringComparer.Ordinal);
        foreach (var r in await session.QueryAsync("""
            SELECT table_name AS t, column_name AS c, data_default AS def
              FROM all_tab_cols
             WHERE owner = :o AND hidden_column = 'NO' AND virtual_column = 'NO' AND identity_column = 'NO' AND data_default IS NOT NULL
            """, p, cancellationToken).ConfigureAwait(false))
        {
            if (!names.Contains(S(r["t"])) || NextValRegex().Match(S(r["def"])) is not { Success: true } m)
            {
                continue;
            }

            var schema = m.Groups["s"].Success ? Unquote(m.Groups["s"].Value) : owner;
            var sequence = Unquote(m.Groups["n"].Value);
            if (schema == owner && sequences.TryGetValue(sequence, out var increment) && increment > 0)
            {
                if (!uses.TryGetValue(sequence, out var list))
                {
                    uses[sequence] = list = [];
                }

                list.Add((S(r["t"]), S(r["c"])));
            }
        }

        foreach (var (sequence, columns) in uses)
        {
            decimal? max = null;
            foreach (var (table, column) in columns)
            {
                var value = (await session.QueryAsync($"SELECT MAX({Q(column)}) AS m FROM {Q(owner)}.{Q(table)}", null, cancellationToken).ConfigureAwait(false))[0]["m"];
                if (value is not null && Convert.ToDecimal(value, CultureInfo.InvariantCulture) is var v && (max is null || v > max))
                {
                    max = v;
                }
            }

            if (max is { } top)
            {
                var next = decimal.Floor(top) + 1;
                await session.ExecuteAsync($"ALTER SEQUENCE {Q(owner)}.{Q(sequence)} RESTART START WITH {next.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>An unquoted name as Oracle stores it (upper case); a quoted one as written.</summary>
    private static string Unquote(string name) =>
        name.StartsWith('"') ? name[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : name.ToUpperInvariant();

    private static Kind KindOf(string type)
    {
        var t = type.Trim().ToUpperInvariant();
        return t switch
        {
            _ when t.StartsWith("VARCHAR2", StringComparison.Ordinal) || t.StartsWith("CHAR", StringComparison.Ordinal) || t.StartsWith("VARCHAR", StringComparison.Ordinal) => Kind.Text,
            _ when t.StartsWith("NVARCHAR2", StringComparison.Ordinal) || t.StartsWith("NCHAR", StringComparison.Ordinal) => Kind.NText,
            "CLOB" => Kind.Clob,
            "NCLOB" => Kind.NClob,
            "LONG RAW" => Kind.LongRaw,
            "LONG" => Kind.Long,
            "INTEGER" => Kind.Integer,
            _ when NumberRegex().Match(t) is { Success: true } m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) <= 18 ? Kind.Integer : Kind.Number,
            _ when t.StartsWith("NUMBER", StringComparison.Ordinal) || t.StartsWith("FLOAT", StringComparison.Ordinal) => Kind.Number,
            "BINARY_FLOAT" => Kind.BinaryFloat,
            "BINARY_DOUBLE" => Kind.BinaryDouble,
            "DATE" => Kind.Date,
            _ when t.StartsWith("TIMESTAMP", StringComparison.Ordinal) && t.EndsWith("WITH LOCAL TIME ZONE", StringComparison.Ordinal) => Kind.TimestampLtz,
            _ when t.StartsWith("TIMESTAMP", StringComparison.Ordinal) && t.EndsWith("WITH TIME ZONE", StringComparison.Ordinal) => Kind.TimestampTz,
            _ when t.StartsWith("TIMESTAMP", StringComparison.Ordinal) => Kind.Timestamp,
            _ when t.StartsWith("INTERVAL DAY", StringComparison.Ordinal) => Kind.IntervalDs,
            _ when t.StartsWith("INTERVAL YEAR", StringComparison.Ordinal) => Kind.IntervalYm,
            _ when t.StartsWith("RAW", StringComparison.Ordinal) => Kind.Raw,
            "BLOB" => Kind.Blob,
            "JSON" => Kind.Json,
            "XMLTYPE" => Kind.Xml,
            "BOOLEAN" => Kind.Boolean,
            "ROWID" or "UROWID" => Kind.RowId,
            _ => Kind.Text,
        };
    }

    /// <summary>FK_NAME for OWNER.FK_NAME, as error messages name a constraint.</summary>
    private static string Bare(string name) => name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : name;

    private static string S(object? value) =>(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim();

    [GeneratedRegex(@"\ANUMBER\((\d+)\)\z")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\A([+-]?)(\d+)-(\d+)\z")]
    private static partial Regex YearMonthRegex();

    [GeneratedRegex(@"\A(?:(?<d>-?\d+)\s+days?)?\s*(?<t>[+-]?\d+:\d{2}:\d{2}(?:\.\d+)?)?\z", RegexOptions.IgnoreCase)]
    private static partial Regex PgIntervalRegex();

    [GeneratedRegex(@"\(""([^""]+)""\.""([^""]+)""\.""([^""]+)""\)")]
    private static partial Regex NullColumnRegex();

    [GeneratedRegex(@"ORA-0229[12]: [^(]*\(([^)]+)\)")]
    private static partial Regex ConstraintRegex();

    [GeneratedRegex(@"\A(?:(?<s>""[^""]+""|[A-Za-z][\w$#]*)\.)?(?<n>""[^""]+""|[A-Za-z][\w$#]*)\.""?NEXTVAL""?\z", RegexOptions.IgnoreCase)]
    private static partial Regex NextValRegex();

    private sealed record IdentityColumn(string Table, string Column, bool Always, bool OnNull);

    private sealed class LoadState(string owner)
    {
        public string Owner { get; } = owner;

        public List<string> Triggers { get; } = [];

        public List<string> Disabled { get; } = [];

        public List<IdentityColumn> Identities { get; } = [];

        public List<IdentityColumn> Switched { get; } = [];
    }
}
