namespace SyncForge.Core.Postgres;

public sealed record QualifiedTableName(string Schema, string Name)
{
    public string Quoted => $"{Quote(Schema)}.{Quote(Name)}";

    public static QualifiedTableName Parse(string configuredName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredName);
        var parts = configuredName.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => new QualifiedTableName("public", RequireIdentifier(parts[0], nameof(configuredName))),
            2 => new QualifiedTableName(RequireIdentifier(parts[0], nameof(configuredName)), RequireIdentifier(parts[1], nameof(configuredName))),
            _ => throw new ArgumentException("Nama tabel harus berbentuk table atau schema.table.", nameof(configuredName))
        };
    }

    public static string Quote(string identifier) => $"\"{RequireIdentifier(identifier, nameof(identifier)).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            throw new ArgumentException("Identifier tidak valid.", parameterName);
        }

        return value;
    }
}
