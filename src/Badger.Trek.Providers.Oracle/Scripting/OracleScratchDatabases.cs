using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using Oracle.ManagedDataAccess.Client;

namespace Badger.Trek.Providers.Oracle.Scripting;

/// <summary>
/// Shadow schemas for 'trek add --from-diff': a user created on the environment's server with the environment's own
/// login (which needs CREATE USER, DROP USER, and the privileges it grants: for a shared server, point --shadow-env at a
/// local Oracle Database Free container instead), signed in with a random password, and dropped afterwards.
/// </summary>
internal sealed class OracleScratchDatabases(OracleProvider provider) : IScratchDatabases
{
    private const string Grants = "CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE, CREATE TRIGGER, CREATE SYNONYM, CREATE TYPE, CREATE MATERIALIZED VIEW";

    public async Task<ScratchDatabase> CreateAsync(DeploymentEnvironment server, SessionContext context, bool keep, CancellationToken cancellationToken)
    {
        var name = $"TREK_SHADOW_{DateTime.UtcNow:yyMMddHHmmss}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";
        var password = "T" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + "_x";
        await using (var session = await provider.OpenSessionAsync(server, context, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var tablespace = (string?)(await session.QueryAsync("SELECT default_tablespace FROM user_users", null, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?["default_tablespace"] ?? "USERS";
                await session.ExecuteAsync($"CREATE USER {name} IDENTIFIED BY \"{password}\" DEFAULT TABLESPACE {OracleStateStore.Quote(tablespace)} QUOTA UNLIMITED ON {OracleStateStore.Quote(tablespace)}", null, cancellationToken).ConfigureAwait(false);
                await session.ExecuteAsync($"GRANT {Grants} TO {name}", null, cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException ex)
            {
                throw new MigrationException($"Could not create the shadow schema on {session.Description}: {ex.Message.Split('\n')[0]} The login needs CREATE USER, DROP USER and the right to grant {Grants}; or pass --shadow-env with a server where it has them (a local Oracle Database Free container works well).", ex);
            }
        }

        var builder = environmentWithout(server);
        var shadow = builder with
        {
            User = name,
            Password = password,
            Authentication = AuthenticationKind.Password,
        };
        if (shadow.ConnectionString is { } cs)
        {
            shadow = shadow with { ConnectionString = new OracleConnectionStringBuilder(cs) { UserID = name, Password = password }.ConnectionString };
        }

        return new ScratchDatabase(shadow, async () =>
        {
            if (keep)
            {
                return;
            }

            OracleConnection.ClearAllPools();
            await using var session = await provider.OpenSessionAsync(server, context, CancellationToken.None).ConfigureAwait(false);
            await session.ExecuteAsync($"DROP USER {name} CASCADE", null, CancellationToken.None).ConfigureAwait(false);
        });

        // The shadow is its own schema: the environment's 'schema' setting does not apply to it.
        static DeploymentEnvironment environmentWithout(DeploymentEnvironment environment)
        {
            var settings = new JsonObject();
            foreach (var (key, value) in environment.ProviderSettings)
            {
                if (!string.Equals(key, OracleConnectionFactory.SchemaSetting, StringComparison.OrdinalIgnoreCase))
                {
                    settings[key] = value?.DeepClone();
                }
            }

            return environment with { ProviderSettings = settings };
        }
    }
}
