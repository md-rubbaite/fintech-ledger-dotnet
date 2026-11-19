using Ledger.Core;

namespace Ledger.Tests;

public sealed class ReconciliationTests
{
    [Fact]
    public void Reports_matches_missing_items_and_amount_differences()
    {
        var ledger = new GeneralLedger(TimeProvider.System);
        ledger.OpenAccount(new Account("bank", "Bank", AccountType.Asset, "BDT"));
        ledger.OpenAccount(new Account("wallet", "Wallet", AccountType.Liability, "BDT"));
        foreach (var (reference, amount) in new[] { ("r1", 1_000L), ("r2", 2_000L), ("r3", 3_000L) })
        {
            ledger.Post(new PostRequest(reference, "top-up", [new Posting("bank", Direction.Debit, amount), new Posting("wallet", Direction.Credit, amount)]));
        }

        var day = new DateOnly(2026, 11, 19);
        var report = Reconciler.Reconcile(ledger, "bank", [
            new StatementLine("r1", 1_000, day),
            new StatementLine("r2", 1_999, day),
            new StatementLine("bank-fee-77", -150, day),
        ]);

        Assert.False(report.IsClean);
        Assert.Equal("r1", Assert.Single(report.Matched).Line.Reference);
        Assert.Equal("r2", Assert.Single(report.AmountMismatches).Line.Reference);
        Assert.Equal("bank-fee-77", Assert.Single(report.UnmatchedStatementLines).Reference);
        Assert.Equal("r3", Assert.Single(report.UnmatchedLedgerEntries).ExternalReference);
    }
}
