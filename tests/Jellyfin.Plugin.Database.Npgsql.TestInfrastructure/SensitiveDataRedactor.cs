using System;
using System.Collections.Generic;
using System.Linq;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed class SensitiveDataRedactor
{
    private readonly List<string> _secrets = new();

    public SensitiveDataRedactor(params IEnumerable<string?> secrets)
    {
        Add(secrets);
    }

    public void Add(params IEnumerable<string?> secrets)
    {
        lock (_secrets)
        {
            foreach (var secret in secrets)
            {
                if (!string.IsNullOrEmpty(secret) && !_secrets.Contains(secret, StringComparer.Ordinal))
                {
                    _secrets.Add(secret);
                    _secrets.Sort((left, right) => right.Length.CompareTo(left.Length));
                }
            }
        }
    }

    public string Redact(string value)
    {
        var redacted = value;
        string[] secrets;
        lock (_secrets)
        {
            secrets = _secrets.ToArray();
        }

        foreach (var secret in secrets)
        {
            redacted = redacted.Replace(secret, "<redacted>", StringComparison.Ordinal);
        }

        return redacted;
    }

    public bool ContainsKnownSecret(string value)
    {
        lock (_secrets)
        {
            return _secrets.Any(secret => value.Contains(secret, StringComparison.Ordinal));
        }
    }

    public static string RedactConnectionString(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            _ = builder.Remove("Password");
            _ = builder.Remove("Passfile");
            _ = builder.Remove("SSL Password");
            builder.IncludeErrorDetail = false;
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return "<invalid PostgreSQL connection string>";
        }
    }
}
