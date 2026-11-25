namespace RecuperadorFirebird.Domain;

public static class FirebirdName
{
    public static string Normalize(string value) => value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')
        ? value[1..^1].Replace("\"\"", "\"")
        : value.ToUpperInvariant();
}

public sealed class ExcludedNames
{
    private readonly HashSet<string> names;

    private ExcludedNames(IEnumerable<string> names) =>
        this.names = names.ToHashSet(StringComparer.Ordinal);

    public static ExcludedNames Parse(string text) => new(
        text.Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(FirebirdName.Normalize));

    public bool Contains(string name) => names.Contains(name);

    public IEnumerable<string> Ordered() => names.Order(StringComparer.Ordinal);
}

public sealed record SchemaSelection(ExcludedNames Tables, ExcludedNames Objects)
{
    public bool IncludesObject(string name, string? table) =>
        !Objects.Contains(name) && (table is null || !Tables.Contains(table));

    public bool IncludesKey(string table, string? reference, string? name) =>
        (name is null || !Objects.Contains(name)) && !Tables.Contains(table) &&
        (reference is null || !Tables.Contains(reference));
}
