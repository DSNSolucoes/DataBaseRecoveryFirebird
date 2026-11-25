namespace RecuperadorFirebird.Domain;

public sealed record TableReconciliation(string Table, long? Transferred, long? InDestination)
{
    public bool Matches => Transferred.HasValue && Transferred == InDestination;
}

public sealed class RecoveryReconciliation
{
    public IReadOnlyList<TableReconciliation> Tables { get; }
    public int TransferredTables => Tables.Count(table => table.Transferred.HasValue);
    public long TransferredRows => Tables.Sum(table => table.Transferred ?? 0);
    public int UnverifiedTables => Tables.Count(table => !table.Matches);

    public RecoveryReconciliation(IEnumerable<string> knownTables, IReadOnlyDictionary<string, long> transferred,
        IReadOnlyDictionary<string, long> destination, ExcludedNames excluded)
    {
        Tables = knownTables.Concat(transferred.Keys).Concat(destination.Keys)
            .Where(table => !excluded.Contains(table))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(table => new TableReconciliation(table, Count(transferred, table), Count(destination, table)))
            .ToArray();
    }

    private static long? Count(IReadOnlyDictionary<string, long> counts, string table) =>
        counts.TryGetValue(table, out var value) ? value : null;
}
