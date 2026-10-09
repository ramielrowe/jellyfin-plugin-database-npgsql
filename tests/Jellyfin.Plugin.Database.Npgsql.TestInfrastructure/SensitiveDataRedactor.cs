using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    internal StreamingSensitiveDataRedactor CreateStreamingRedactor()
    {
        lock (_secrets)
        {
            return new StreamingSensitiveDataRedactor(_secrets.ToArray());
        }
    }

    internal StreamingTextMatcher[] CreateSecretMatchers()
    {
        lock (_secrets)
        {
            return _secrets.Select(secret => new StreamingTextMatcher(secret)).ToArray();
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

internal sealed class StreamingSensitiveDataRedactor
{
    private readonly string[] _secrets;
    private readonly int _maximumSecretLength;
    private string _pending = string.Empty;

    public StreamingSensitiveDataRedactor(string[] secrets)
    {
        _secrets = secrets;
        _maximumSecretLength = Math.Max(1, secrets.Length == 0 ? 1 : secrets.Max(secret => secret.Length));
    }

    public string Redact(ReadOnlySpan<char> chunk, bool final)
    {
        var combined = string.Concat(_pending, chunk);
        var processLimit = final ? combined.Length : Math.Max(0, combined.Length - _maximumSecretLength + 1);
        var output = new StringBuilder(combined.Length);
        var position = 0;
        while (position < processLimit)
        {
            var matched = false;
            foreach (var secret in _secrets)
            {
                if (combined.AsSpan(position).StartsWith(secret, StringComparison.Ordinal))
                {
                    output.Append("<redacted>");
                    position += secret.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                output.Append(combined[position]);
                position++;
            }
        }

        _pending = combined[position..];
        if (final && _pending.Length != 0)
        {
            output.Append(_pending);
            _pending = string.Empty;
        }

        return output.ToString();
    }
}

internal sealed class StreamingTextMatcher(string pattern)
{
    private string _pending = string.Empty;

    public bool Contains(ReadOnlySpan<char> chunk, bool final = false)
    {
        var combined = string.Concat(_pending, chunk);
        if (combined.Contains(pattern, StringComparison.Ordinal))
        {
            return true;
        }

        _pending = final || pattern.Length <= 1
            ? string.Empty
            : combined[^Math.Min(combined.Length, pattern.Length - 1)..];
        return false;
    }
}
