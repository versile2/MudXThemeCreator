using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Npgsql;

namespace MudXtra.ThemeCreator.UI.Startup;

/// <summary>A read-only, single attempt. The Docker shell alone owns retry timing.</summary>
public static class PostgresReadiness
{
    public const int Success = 0;
    public const int TransientFailure = 10;
    public const int PermanentFailure = 20;
    public const int Cancelled = 130;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Validates required PostgreSQL configuration without contacting a database.</summary>
    public static string ResolveConnectionString(IConfiguration config)
    {
        try
        {
            var template = config.GetConnectionString("postgresql");
            if (string.IsNullOrWhiteSpace(template)) throw new ArgumentException();
            // Port is numeric; other replacements happen on parsed values, so secrets containing
            // semicolons/quotes remain values rather than injecting connection-string options.
            template = template.Replace("[dbPort]", config["dbPort"] ?? "[dbPort]");
            var builder = new NpgsqlConnectionStringBuilder(template);
            builder.Host = (builder.Host ?? "").Replace("[dbName]", config["dbName"] ?? "[dbName]");
            builder.Username = builder.Username?.Replace("[dbUser]", config["dbUser"] ?? "[dbUser]");
            builder.Password = builder.Password?.Replace("[dbPassword]", config["dbPassword"] ?? "[dbPassword]");
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database)
                || string.IsNullOrWhiteSpace(builder.Username)
                || Regex.IsMatch(builder.ConnectionString, @"\[[A-Za-z][A-Za-z0-9_]*\]"))
                throw new ArgumentException();
            return builder.ConnectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            // Never retain the original exception, whose message may contain credentials.
            throw new InvalidOperationException("PostgreSQL configuration is missing or invalid.");
        }
    }

    /// <summary>Authenticates and executes SELECT 1, never constructing a DbContext or migrating.</summary>
    public static async Task<ProbeResult> ProbeAsync(IConfiguration config, CancellationToken shutdown = default, TimeSpan? timeout = null)
    {
        if (shutdown.IsCancellationRequested) return new(Cancelled, "shutdown");
        string connectionString;
        try { connectionString = ResolveConnectionString(config); }
        catch (InvalidOperationException) { return new(PermanentFailure, "configuration"); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(timeout ?? AttemptTimeout);
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                Multiplexing = false,
                Timeout = Math.Max(1, (int)Math.Ceiling((timeout ?? AttemptTimeout).TotalSeconds)),
                CommandTimeout = Math.Max(1, (int)Math.Ceiling((timeout ?? AttemptTimeout).TotalSeconds)),
                CancellationTimeout = -1 // Close immediately on cancellation; no secondary cancellation wait.
            };
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
            dataSourceBuilder.ConfigureTypeLoading(options => options.EnableTypeLoading(false));
            await using var dataSource = dataSourceBuilder.Build();
            await using var connection = dataSource.CreateConnection();
            await connection.OpenAsync(deadline.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            var value = await command.ExecuteScalarAsync(deadline.Token);
            if (shutdown.IsCancellationRequested) return new(Cancelled, "shutdown");
            return value is int number && number == 1
                ? new(Success, "ready") : new(PermanentFailure, "unexpected-query-result");
        }
        catch (Exception ex)
        {
            if (shutdown.IsCancellationRequested) return new(Cancelled, "shutdown");
            if (deadline.IsCancellationRequested) return new(TransientFailure, "attempt-timeout");
            for (Exception? cause = ex; cause != null; cause = cause.InnerException)
                if (cause is AuthenticationException)
                    return new(PermanentFailure, "tls");
            if (ex is PostgresException postgres)
            {
                var state = postgres.SqlState;
                if (state.StartsWith("28", StringComparison.Ordinal)) return new(PermanentFailure, "authentication");
                if (state == "3D000") return new(PermanentFailure, "database-missing");
                // Everything not explicitly operational/transient fails closed, including class 28,
                // nonexistent database 3D000 and SQL/query/permission errors.
                return state.StartsWith("08", StringComparison.Ordinal) || state is "57P01" or "57P02" or "57P03" or "53300"
                    ? new(TransientFailure, "postgres-transient") : new(PermanentFailure, "postgres-permanent");
            }
            if (ex is NpgsqlException { IsTransient: true } or TimeoutException or SocketException)
                return new(TransientFailure, "connection-transient");
            return new(PermanentFailure, "connection-policy-or-query");
        }
    }
}

/// <summary>Stable shell contract; classifications intentionally contain no exception/connection details.</summary>
public sealed record ProbeResult(int ExitCode, string Classification);
