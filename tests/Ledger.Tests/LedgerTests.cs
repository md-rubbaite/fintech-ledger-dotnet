using Ledger.Core;

namespace Ledger.Tests;

/// <summary>Wallet scenarios: top-up from the bank, P2P transfer with a fee, reversal, overdraft protection.</summary>
public sealed class LedgerTests
{
    private readonly GeneralLedger _ledger = new(TimeProvider.System);

    public LedgerTests()
    {
        _ledger.OpenAccount(new Account("bank:settlement", "Settlement bank account", AccountType.Asset, "BDT"));
        _ledger.OpenAccount(new Account("wallet:alice", "Alice wallet", AccountType.Liability, "BDT", AllowNegative: false));
        _ledger.OpenAccount(new Account("wallet:bob", "Bob wallet", AccountType.Liability, "BDT", AllowNegative: false));
        _ledger.OpenAccount(new Account("revenue:fees", "Transfer fees", AccountType.Revenue, "BDT"));
        _ledger.OpenAccount(new Account("bank:usd", "USD account", AccountType.Asset, "USD"));
    }

    private PostResult TopUp(string reference, string wallet, long amount) =>
        _ledger.Post(new PostRequest(reference, "Wallet top-up", [
            new Posting("bank:settlement", Direction.Debit, amount),
            new Posting(wallet, Direction.Credit, amount),
        ]));

    private PostResult Transfer(string reference, string from, string to, long amount, long fee) =>
        _ledger.Post(new PostRequest(reference, "P2P transfer", [
            new Posting(from, Direction.Debit, amount + fee),
            new Posting(to, Direction.Credit, amount),
            new Posting("revenue:fees", Direction.Credit, fee),
        ]));

    [Fact]
    public void Top_up_increases_both_cash_and_the_wallet_liability()
    {
        Assert.IsType<PostResult.Posted>(TopUp("tx-1", "wallet:alice", 100_000));

        Assert.Equal(100_000, _ledger.Balance("bank:settlement"));
        Assert.Equal(100_000, _ledger.Balance("wallet:alice"));
        Assert.Equal(0, _ledger.TrialBalance());
    }

    [Fact]
    public void Transfer_with_fee_moves_money_and_books_revenue()
    {
        TopUp("tx-1", "wallet:alice", 100_000);

        Assert.IsType<PostResult.Posted>(Transfer("tx-2", "wallet:alice", "wallet:bob", 50_000, 500));

        Assert.Equal(49_500, _ledger.Balance("wallet:alice"));
        Assert.Equal(50_000, _ledger.Balance("wallet:bob"));
        Assert.Equal(500, _ledger.Balance("revenue:fees"));
        Assert.Equal(0, _ledger.TrialBalance());
    }

    [Fact]
    public void Overdraft_is_rejected_and_nothing_is_written()
    {
        TopUp("tx-1", "wallet:alice", 1_000);

        var result = Transfer("tx-2", "wallet:alice", "wallet:bob", 1_000, 10);

        Assert.Contains("Insufficient funds", Assert.IsType<PostResult.Rejected>(result).Reason);
        Assert.Equal(1_000, _ledger.Balance("wallet:alice"));
        Assert.Single(_ledger.Journal);
    }

    [Fact]
    public void Unbalanced_entry_is_rejected()
    {
        var result = _ledger.Post(new PostRequest("tx-1", "bad", [
            new Posting("bank:settlement", Direction.Debit, 100),
            new Posting("wallet:alice", Direction.Credit, 99),
        ]));

        Assert.Contains("unbalanced", Assert.IsType<PostResult.Rejected>(result).Reason);
    }

    [Fact]
    public void Mixed_currencies_in_one_entry_are_rejected()
    {
        var result = _ledger.Post(new PostRequest("tx-1", "bad", [
            new Posting("bank:usd", Direction.Debit, 100),
            new Posting("wallet:alice", Direction.Credit, 100),
        ]));

        Assert.Contains("one currency", Assert.IsType<PostResult.Rejected>(result).Reason);
    }

    [Fact]
    public void Same_reference_twice_is_idempotent()
    {
        var first = Assert.IsType<PostResult.Posted>(TopUp("tx-1", "wallet:alice", 5_000));
        var second = Assert.IsType<PostResult.Duplicate>(TopUp("tx-1", "wallet:alice", 5_000));

        Assert.Equal(first.Entry, second.Entry);
        Assert.Equal(5_000, _ledger.Balance("wallet:alice"));
    }

    [Fact]
    public void Same_reference_with_different_postings_is_rejected()
    {
        TopUp("tx-1", "wallet:alice", 5_000);

        Assert.IsType<PostResult.Rejected>(TopUp("tx-1", "wallet:alice", 6_000));
    }

    [Fact]
    public void Reversal_restores_balances_and_keeps_history()
    {
        TopUp("tx-1", "wallet:alice", 10_000);
        var transfer = Assert.IsType<PostResult.Posted>(Transfer("tx-2", "wallet:alice", "wallet:bob", 4_000, 100));

        Assert.IsType<PostResult.Posted>(_ledger.Reverse(transfer.Entry.Sequence, "tx-2-rev", "customer dispute"));
        Assert.IsType<PostResult.Rejected>(_ledger.Reverse(transfer.Entry.Sequence, "tx-2-rev-2", "again"));

        Assert.Equal(10_000, _ledger.Balance("wallet:alice"));
        Assert.Equal(0, _ledger.Balance("wallet:bob"));
        Assert.Equal(3, _ledger.Journal.Count);
    }

    [Fact]
    public void Hash_chain_detects_tampering()
    {
        TopUp("tx-1", "wallet:alice", 10_000);
        TopUp("tx-2", "wallet:bob", 20_000);
        TopUp("tx-3", "wallet:alice", 30_000);
        Assert.Null(_ledger.VerifyChain());

        _ledger.TamperForTesting(2, e => e with { Postings = [new Posting("bank:settlement", Direction.Debit, 2), new Posting("wallet:bob", Direction.Credit, 2)] });

        Assert.Equal(2L, _ledger.VerifyChain());
    }

    [Fact]
    public async Task Concurrent_transfers_never_overdraw_and_keep_the_books_balanced()
    {
        TopUp("seed-a", "wallet:alice", 100_000);
        TopUp("seed-b", "wallet:bob", 100_000);

        var tasks = Enumerable.Range(0, 400).Select(i => Task.Run(() =>
            i % 2 == 0
                ? Transfer($"t-{i}", "wallet:alice", "wallet:bob", 1_700, 30)
                : Transfer($"t-{i}", "wallet:bob", "wallet:alice", 900, 30)));
        await Task.WhenAll(tasks);

        Assert.True(_ledger.Balance("wallet:alice") >= 0);
        Assert.True(_ledger.Balance("wallet:bob") >= 0);
        Assert.Equal(0, _ledger.TrialBalance());
        Assert.Equal(
            _ledger.Balance("bank:settlement"),
            _ledger.Balance("wallet:alice") + _ledger.Balance("wallet:bob") + _ledger.Balance("revenue:fees"));
        Assert.Null(_ledger.VerifyChain());
    }
}
