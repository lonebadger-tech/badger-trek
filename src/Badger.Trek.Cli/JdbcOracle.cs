using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Badger.Trek.Core.Import;

namespace Badger.Trek.Cli;

/// <summary>
/// Maps an Oracle JDBC URL to trek.yaml environment settings: jdbc:oracle:thin:@host:port/service (or @//host...,
/// EZConnect Plus with tcps:// and ?options), @host:port:SID, a full (DESCRIPTION=...) descriptor, or a TNS alias
/// (with ?TNS_ADMIN=). An inline user/password@ is split off; passwords are never copied: they become ${env:...}
/// references.
/// </summary>
public static partial class JdbcOracle
{
    public static JsonObject? Map(ConnectionToImport connection, List<string> warnings)
    {
        var where = $"environment '{connection.EnvironmentName}'";
        var m = UrlRegex().Match(connection.Url.Trim());
        if (!m.Success)
        {
            warnings.Add($"{where}: '{InlinePasswordRegex().Replace(connection.Url, "$1/********@")}' is not an Oracle JDBC URL; set the connection in trek.yaml by hand.");
            return null;
        }

        var settings = new JsonObject();
        var target = m.Groups["target"].Value;
        var user = Env(connection.User) ?? (m.Groups["user"].Success ? m.Groups["user"].Value : null);
        var password = connection.Password ?? (m.Groups["password"].Success ? m.Groups["password"].Value : null);

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = target.StartsWith('(') ? -1 : target.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            foreach (var pair in target[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                props[Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "true" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            }

            target = target[..query];
        }

        var tls = new JsonObject();
        if (target.StartsWith('('))
        {
            // A full descriptor goes to the connection string as it is; ODP.NET reads the same syntax.
            settings["connectionString"] = $"Data Source={target}";
            if (TcpsRegex().IsMatch(target))
            {
                tls["mode"] = "mandatory";
            }
        }
        else if (EzConnectRegex().Match(target) is { Success: true } ez)
        {
            settings["host"] = ez.Groups["host"].Value.Trim('[', ']');
            if (ez.Groups["port"].Success)
            {
                settings["port"] = int.Parse(ez.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (ez.Groups["service"].Success)
            {
                settings["database"] = ez.Groups["service"].Value;
            }
            else
            {
                // host:port:SID. trek connects by service name; the SID usually is one too (non-CDB databases).
                settings["database"] = ez.Groups["sid"].Value;
                warnings.Add($"{where}: the URL names SID {ez.Groups["sid"].Value}; trek connects by service name. Set 'database' to the service name if it differs (for a pluggable database it does).");
            }

            if (ez.Groups["protocol"].Value.Equals("tcps", StringComparison.OrdinalIgnoreCase))
            {
                tls["mode"] = "mandatory";
            }
        }
        else
        {
            // A TNS alias, resolved from tnsnames.ora (in TNS_ADMIN).
            settings["connectionString"] = $"Data Source={target}";
            if (!props.ContainsKey("TNS_ADMIN") && Environment.GetEnvironmentVariable("TNS_ADMIN") is null)
            {
                warnings.Add($"{where}: '{target}' is a TNS alias; set connectionProperties TNS_Admin to the folder holding tnsnames.ora (and the wallet), or the TNS_ADMIN environment variable.");
            }
        }

        if (Take(props, "oracle.net.ssl_server_dn_match", "ssl_server_dn_match") is { } dnMatch && dnMatch.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            tls["mode"] = "strict";
        }

        settings["authentication"] = "password";
        if (user is not null)
        {
            settings["user"] = user;
        }
        else
        {
            warnings.Add($"{where}: no user in the Flyway settings; set 'user' in trek.yaml (trek deploys to that user's schema).");
        }

        if (password is not null)
        {
            if (Env(password) is { } reference && reference.StartsWith("${env:", StringComparison.Ordinal))
            {
                settings["password"] = reference;
            }
            else
            {
                var variable = connection.EnvironmentName == "default" ? "FLYWAY_PASSWORD" : $"FLYWAY_{Regex.Replace(connection.EnvironmentName.ToUpperInvariant(), "[^A-Z0-9]+", "_")}_PASSWORD";
                warnings.Add($"{where}: the password was not copied; trek reads it from the {variable} environment variable.");
                settings["password"] = $"${{env:{variable}}}";
            }
        }

        if (tls.Count > 0)
        {
            settings["tls"] = tls;
        }

        var connectionProperties = new JsonObject();
        foreach (var (jdbc, odp) in new[] { ("TNS_ADMIN", "TNS_Admin"), ("oracle.net.tns_admin", "TNS_Admin"), ("wallet_location", "Wallet_Location"), ("oracle.net.wallet_location", "Wallet_Location"), ("oracle.net.CONNECT_TIMEOUT", "Connection Timeout") })
        {
            if (Take(props, jdbc) is { } value)
            {
                connectionProperties[odp] = jdbc.EndsWith("CONNECT_TIMEOUT", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var ms) ? (Math.Max(1, ms / 1000)).ToString(System.Globalization.CultureInfo.InvariantCulture) : value;
            }
        }

        if (connectionProperties.Count > 0)
        {
            settings["connectionProperties"] = connectionProperties;
        }

        if (props.Count > 0)
        {
            warnings.Add($"{where}: JDBC settings without a trek equivalent were left out: {string.Join(", ", props.Keys.Order(StringComparer.OrdinalIgnoreCase))}. Any ODP.NET keyword can go under connectionProperties.");
        }

        return settings;
    }

    private static string? Env(string? value) =>
        value is null ? null : EnvRegex().Match(value) is { Success: true } m ? $"${{env:{m.Groups["name"].Value}}}" : value;

    private static string? Take(Dictionary<string, string> props, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (props.Remove(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>jdbc:oracle:thin: or jdbc:oracle:oci:, an optional user[/password], '@', then where to connect.</summary>
    [GeneratedRegex(@"^jdbc:oracle:(?:thin|oci8?|kprb):(?:(?<user>[^/@]+)(?:/(?<password>[^@]*))?)?@(?<target>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UrlRegex();

    /// <summary>[tcp[s]://][//]host[:port](/service | :SID), with an IPv6 host in brackets.</summary>
    [GeneratedRegex(@"^(?:(?<protocol>tcps?)://)?(?://)?(?<host>\[[^\]]+\]|[^:/\s\[\]]+)(?::(?<port>\d+))?(?:/(?<service>[^:/\s]+)(?::[^/\s]*)?|:(?<sid>[A-Za-z][\w$#]*))$", RegexOptions.IgnoreCase)]
    private static partial Regex EzConnectRegex();

    [GeneratedRegex(@"\(\s*PROTOCOL\s*=\s*TCPS\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex TcpsRegex();

    [GeneratedRegex(@"(?i)(jdbc:oracle:\w+:[^/@]+)/[^@]*@")]
    private static partial Regex InlinePasswordRegex();

    [GeneratedRegex(@"^\$\{(?:env\.)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}$")]
    private static partial Regex EnvRegex();
}
