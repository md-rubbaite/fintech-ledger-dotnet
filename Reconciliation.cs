namespace Ledger.Core;

/// <summary>A line from the bank or payment-processor statement. Positive = money in.</summary>
public sealed record StatementLine(string Reference, long AmountMinor, DateOnly ValueDate);

public sealed record ReconciliationReport(
    IReadOnlyList<(StatementLine Line, JournalEntry Entry)> Matched,
    IReadOnlyList<StatementLine> UnmatchedStatementLines,
    IReadOnlyList<JournalEntry> UnmatchedLedgerEntries,
    IReadOnlyList<(StatementLine Line, JournalEntry Entry)> AmountMismatches)
{
    public bool IsClean => UnmatchedStatementLines.Count == 0 && UnmatchedLedgerEntries.Count == 0 && AmountMismatches.Count == 0;
}

public static class Reconciler
{
    /// <summary>
    /// Matches a cash account's ledger movements against an external statement by reference, then amount.
    /// Anything unmatched is an exception for a human to investigate. Nothing is auto-corrected.
    /// </summary>
    public static ReconciliationReport Reconcile(GeneralLedger ledger, string cashAccountId, IEnumerable<StatementLine> statement)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var movements = ledger.Journal
            .Where(e => e.Postings.Any(p => p.AccountId == cashAccountId))
            .ToDictionary(e => e.ExternalReference, StringComparer.Ordinal);

        var matched = new List<(StatementLine, JournalEntry)>();
        var mismatched = new List<(StatementLine, JournalEntry)>();
        var unmatchedLines = new List<StatementLine>();

        foreach (var line in statement)
        {
            if (!movements.Remove(line.Reference, out var entry))
            {
                unmatchedLines.Add(line);
                continue;
            }

            var net = entry.Postings.Where(p => p.AccountId == cashAccountId)
                .Sum(p => p.Direction == Direction.Debit ? p.AmountMinor : -p.AmountMinor);
            (net == line.AmountMinor ? matched : mismatched).Add((line, entry));
        }

        return new ReconciliationReport(matched, unmatchedLines, movements.Values.ToList(), mismatched);
    }
}
