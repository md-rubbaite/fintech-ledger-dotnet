-- PostgreSQL mapping of the ledger rules.

CREATE TABLE accounts (
    id             varchar(64) PRIMARY KEY,
    name           text        NOT NULL,
    type           varchar(16) NOT NULL CHECK (type IN ('asset','liability','equity','revenue','expense')),
    currency       char(3)     NOT NULL,
    allow_negative boolean     NOT NULL DEFAULT true,
    balance_minor  bigint      NOT NULL DEFAULT 0,          -- on the normal side; maintained in the same transaction
    CONSTRAINT no_overdraft CHECK (allow_negative OR balance_minor >= 0)
);

CREATE TABLE journal_entries (
    sequence           bigint       PRIMARY KEY,
    external_reference varchar(128) NOT NULL UNIQUE,         -- idempotency
    description        text         NOT NULL,
    currency           char(3)      NOT NULL,
    posted_at          timestamptz  NOT NULL DEFAULT now(),
    reverses_sequence  bigint       UNIQUE REFERENCES journal_entries(sequence),  -- at most one reversal
    previous_hash      char(64)     NOT NULL,
    hash               char(64)     NOT NULL UNIQUE
);

CREATE TABLE postings (
    entry_sequence bigint      NOT NULL REFERENCES journal_entries(sequence),
    line_no        smallint    NOT NULL,
    account_id     varchar(64) NOT NULL REFERENCES accounts(id),
    direction      char(1)     NOT NULL CHECK (direction IN ('D','C')),
    amount_minor   bigint      NOT NULL CHECK (amount_minor > 0),
    PRIMARY KEY (entry_sequence, line_no)
);

-- Balanced-entry check at commit time.
CREATE FUNCTION assert_entry_balanced() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (SELECT sum(CASE direction WHEN 'D' THEN amount_minor ELSE -amount_minor END)
          FROM postings WHERE entry_sequence = NEW.entry_sequence) <> 0 THEN
        RAISE EXCEPTION 'journal entry % is unbalanced', NEW.entry_sequence;
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER entry_must_balance
AFTER INSERT ON postings DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION assert_entry_balanced();

-- Append-only: the application role can INSERT and SELECT, never UPDATE or DELETE.
REVOKE UPDATE, DELETE ON journal_entries, postings FROM ledger_app;
