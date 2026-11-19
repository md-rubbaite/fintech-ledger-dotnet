namespace Ledger.Core;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

public enum Direction
{
    Debit,
    Credit,
}

/// <param name="AllowNegative">
/// False for customer wallets and similar accounts that must never go below zero
/// (measured on the account's normal side).
/// </param>
public sealed record Account(string Id, string Name, AccountType Type, string Currency, bool AllowNegative = true)
{
    /// <summary>Assets and expenses grow with debits; liabilities, equity and revenue grow with credits.</summary>
    public Direction NormalSide => Type is AccountType.Asset or AccountType.Expense ? Direction.Debit : Direction.Credit;
}

/// <summary>One leg of a journal entry. Amounts are positive minor units; the direction carries the sign.</summary>
public sealed record Posting(string AccountId, Direction Direction, long AmountMinor);

public sealed record PostRequest(string ExternalReference, string Description, IReadOnlyList<Posting> Postings);

/// <summary>An immutable, hash-chained journal entry. Corrections are made with reversing entries, never edits.</summary>
public sealed record JournalEntry(
    long Sequence,
    string ExternalReference,
    string Description,
    IReadOnlyList<Posting> Postings,
    string Currency,
    DateTimeOffset PostedAt,
    long? ReversesSequence,
    string PreviousHash,
    string Hash);

public abstract record PostResult
{
    private PostResult() { }

    public sealed record Posted(JournalEntry Entry) : PostResult;

    /// <summary>The external reference was already posted; the original entry is returned (idempotent).</summary>
    public sealed record Duplicate(JournalEntry Entry) : PostResult;

    public sealed record Rejected(string Reason) : PostResult;
}
