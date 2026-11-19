using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ledger.Core;

/// <summary>
/// Append-only double-entry ledger with idempotent posting, balance invariants,
/// reversals and a SHA-256 hash chain for tamper evidence.
/// </summary>
/// <remarks>
/// The in-memory journal keeps the reference small and testable. The same rules map to a relational
/// design (see docs/schema.sql): a unique constraint on the external reference, SERIALIZABLE or
/// row-locked balance checks, and no UPDATE/DELETE grants on the journal tables.
/// </remarks>
public sealed class GeneralLedger(TimeProvider clock)
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _balances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JournalEntry> _byReference = new(StringComparer.Ordinal);
    private readonly List<JournalEntry> _journal = [];
    private readonly object _gate = new();

    public IReadOnlyList<JournalEntry> Journal
    {
        get
        {
            lock (_gate)
            {
                return _journal.ToList();
            }
        }
    }

    public void OpenAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            if (!_accounts.TryAdd(account.Id, account))
            {
                throw new InvalidOperationException($"Account {account.Id} already exists.");
            }

            _balances[account.Id] = 0;
        }
    }

    /// <summary>Balance on the account's normal side (positive = normal).</summary>
    public long Balance(string accountId)
    {
        lock (_gate)
        {
            return _balances[accountId];
        }
    }

    public PostResult Post(PostRequest request) => Post(request, reversesSequence: null);

    /// <summary>Reverses an entry by posting its mirror image. The original stays in the journal.</summary>
    public PostResult Reverse(long sequence, string externalReference, string reason)
    {
        JournalEntry original;
        lock (_gate)
        {
            if (sequence < 1 || sequence > _journal.Count)
            {
                return new PostResult.Rejected($"Entry {sequence} does not exist.");
            }

            original = _journal[(int)sequence - 1];
            if (_journal.Any(e => e.ReversesSequence == sequence))
            {
                return new PostResult.Rejected($"Entry {sequence} is already reversed.");
            }
        }

        var mirrored = original.Postings
            .Select(p => p with { Direction = p.Direction == Direction.Debit ? Direction.Credit : Direction.Debit })
            .ToList();
        return Post(new PostRequest(externalReference, $"Reversal of #{sequence}: {reason}", mirrored), sequence);
    }

    /// <summary>Recomputes every hash. Returns the first sequence whose hash does not match, or null if intact.</summary>
    public long? VerifyChain()
    {
        lock (_gate)
        {
            var previous = GenesisHash;
            foreach (var e in _journal)
            {
                if (e.PreviousHash != previous || ComputeHash(e with { Hash = string.Empty }) != e.Hash)
                {
                    return e.Sequence;
                }

                previous = e.Hash;
            }

            return null;
        }
    }

    /// <summary>Sum of debits minus credits over all accounts. Always zero for a consistent ledger.</summary>
    public long TrialBalance()
    {
        lock (_gate)
        {
            return _journal.SelectMany(e => e.Postings)
                .Sum(p => p.Direction == Direction.Debit ? p.AmountMinor : -p.AmountMinor);
        }
    }

    /// <summary>Test hook to simulate tampering with stored data.</summary>
    internal void TamperForTesting(long sequence, Func<JournalEntry, JournalEntry> change)
    {
        lock (_gate)
        {
            _journal[(int)sequence - 1] = change(_journal[(int)sequence - 1]);
        }
    }

    private PostResult Post(PostRequest request, long? reversesSequence)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ExternalReference))
        {
            return new PostResult.Rejected("An external reference is required for idempotency.");
        }

        lock (_gate)
        {
            if (_byReference.TryGetValue(request.ExternalReference, out var existing))
            {
                return SamePostings(existing.Postings, request.Postings)
                    ? new PostResult.Duplicate(existing)
                    : new PostResult.Rejected("External reference already used for a different entry.");
            }

            if (reversesSequence is { } target && _journal.Any(e => e.ReversesSequence == target))
            {
                return new PostResult.Rejected($"Entry {target} is already reversed.");
            }

            if (Validate(request) is { } error)
            {
                return new PostResult.Rejected(error);
            }

            // Compute the new balances first; commit only if every invariant holds.
            var newBalances = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var p in request.Postings)
            {
                var account = _accounts[p.AccountId];
                var current = newBalances.TryGetValue(p.AccountId, out var b) ? b : _balances[p.AccountId];
                newBalances[p.AccountId] = checked(current + (p.Direction == account.NormalSide ? p.AmountMinor : -p.AmountMinor));
            }

            var overdrawn = newBalances.FirstOrDefault(kv => kv.Value < 0 && !_accounts[kv.Key].AllowNegative);
            if (overdrawn.Key is not null)
            {
                return new PostResult.Rejected($"Insufficient funds in {overdrawn.Key}.");
            }

            var previousHash = _journal.Count == 0 ? GenesisHash : _journal[^1].Hash;
            var entry = new JournalEntry(
                _journal.Count + 1,
                request.ExternalReference,
                request.Description,
                request.Postings.ToList(),
                _accounts[request.Postings[0].AccountId].Currency,
                clock.GetUtcNow(),
                reversesSequence,
                previousHash,
                string.Empty);
            entry = entry with { Hash = ComputeHash(entry) };

            _journal.Add(entry);
            _byReference[entry.ExternalReference] = entry;
            foreach (var (id, balance) in newBalances)
            {
                _balances[id] = balance;
            }

            return new PostResult.Posted(entry);
        }
    }

    private string? Validate(PostRequest request)
    {
        if (request.Postings.Count < 2)
        {
            return "An entry needs at least two postings.";
        }

        if (request.Postings.Any(p => p.AmountMinor <= 0))
        {
            return "Posting amounts must be positive.";
        }

        var missing = request.Postings.FirstOrDefault(p => !_accounts.ContainsKey(p.AccountId));
        if (missing is not null)
        {
            return $"Unknown account {missing.AccountId}.";
        }

        if (request.Postings.Select(p => _accounts[p.AccountId].Currency).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return "All postings in an entry must share one currency. Use FX clearing accounts for conversions.";
        }

        var debits = request.Postings.Where(p => p.Direction == Direction.Debit).Sum(p => p.AmountMinor);
        var credits = request.Postings.Where(p => p.Direction == Direction.Credit).Sum(p => p.AmountMinor);
        return debits == credits ? null : $"Entry is unbalanced: debits {debits} != credits {credits}.";
    }

    private static bool SamePostings(IReadOnlyList<Posting> a, IReadOnlyList<Posting> b) => a.SequenceEqual(b);

    private static string ComputeHash(JournalEntry e)
    {
        var sb = new StringBuilder();
        sb.Append(e.Sequence.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(e.ExternalReference).Append('|')
          .Append(e.Description).Append('|')
          .Append(e.Currency).Append('|')
          .Append(e.PostedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(e.ReversesSequence?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
          .Append(e.PreviousHash);
        foreach (var p in e.Postings)
        {
            sb.Append('|').Append(p.AccountId).Append(':').Append(p.Direction).Append(':')
              .Append(p.AmountMinor.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
