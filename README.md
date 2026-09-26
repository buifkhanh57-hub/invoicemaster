# invoicemaster

> Customers, invoices, payments and tax from your terminal — a zero-dependency .NET 8 CLI for small-business invoicing.

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)
![Dependencies](https://img.shields.io/badge/NuGet%20packages-0-success)
![Tests](https://img.shields.io/badge/tests-12%20suites-brightgreen)
![Platform](https://img.shields.io/badge/platform-linux%20%7C%20macOS%20%7C%20Windows-lightgrey)

---

## Overview

**invoicemaster** is a command-line invoice manager written in C# for .NET 8. It keeps your
customers, invoices, payments and tax rules as plain JSON files under `~/.invoicemaster/`,
computes per-line and per-invoice taxes, tracks who owes you what, ages your receivables
into 30/60/90-day buckets, drafts overdue reminder letters and exports everything to
RFC 4180 CSV — all without a database, a server or a single NuGet package.

The tool is built for people who live in the terminal: freelancers billing a handful of
clients, small agencies that want scriptable bookkeeping, and developers who would rather
pipe CSV into pandas than click through a web dashboard.

Key design goals:

- **Zero dependencies.** Only `System.*` BCL APIs, including the built-in `System.Text.Json`.
- **Data you own.** Human-readable JSON in one folder; copy it, back it up, `git diff` it.
- **Safe writes.** Atomic temp-file moves, rotated backups and self-healing loads.
- **Script friendly.** Stable exit codes (0/1/2), invariant culture output, `NO_COLOR` support.
- **Honest money math.** `decimal` everywhere, half-up rounding, per-currency minor units.

## Features

- **Customer bookkeeping** — register customers with contact details, VAT/tax id, postal
  address, default currency and tax region; keyword search across all fields; delete
  protection while invoices exist.
- **Invoice lifecycle** — drafts → issued → paid/overdue/void, with per-year numbering
  (`INV-2026-0001`), line items with quantities, discounts and exemption categories.
- **Tax engine** — per-region rules with standard rates, reduced rates per category and
  fully exempt categories; the tax amount is snapshotted at issue time so later rule edits
  never rewrite history.
- **Payments** — record cash/bank/card/check payments, auto-generated `PAY-000001` ids,
  currency checks, overpay protection with an explicit `--allow-overpay` escape hatch.
- **Reports** — outstanding balances, 30/60/90 aging with `--detail`, monthly
  invoiced-vs-collected revenue series, top-customer ranking by three metrics.
- **CSV export** — invoices and payments as RFC 4180 files with invariant numbers and
  ISO dates; loads cleanly in Excel, Sheets and pandas.
- **Reminder letters** — plain-text, severity-escalating dunning letters (gentle → firm →
  final notice) printed to stdout or written to files.
- **Durable storage** — atomic saves, `.bak.1..N` backup rotation, automatic recovery from
  the newest readable backup when a data file is corrupt.
- **Color where it helps** — ANSI-styled tables and status labels that respect `NO_COLOR`
  and disappear on redirected output.

## Requirements

| Requirement | Notes |
|---|---|
| .NET SDK 8.0 | `dotnet --version` should print `8.0.x` or newer |
| OS | Linux, macOS or Windows (anywhere .NET 8 runs) |
| Disk | A few MB for the binary; data is plain JSON |
| Optional | GNU `make` if you prefer the Makefile workflow |

No database, no runtime services, no NuGet packages. The project compiles against the
base class library only.

## Installation

### From source with dotnet

```bash
git clone <your-fork-url> invoicemaster
cd invoicemaster

# restore (no-op today, but future-proof) and build
dotnet restore invoicemaster.csproj
dotnet build invoicemaster.csproj -c Release

# run directly
dotnet run --project invoicemaster.csproj -c Release -- --version
```

### With make

```bash
make build            # compile in Release
make run ARGS="--version"
make test             # build + run the self-contained test runner
make publish RID=linux-x64   # single-file self-contained binary in ./publish
make clean            # remove build artifacts
```

The `publish` target produces a self-contained single-file executable, so you can drop
`invoicemaster` onto a machine (or a cron box) without installing the runtime there.

### Optional: put it on your PATH

```bash
ln -s "$(pwd)/bin/Release/net8.0/invoicemaster" ~/.local/bin/invoicemaster
```

## Quick Start

A complete first invoice in under a minute:

```bash
# 1. Register a customer
invoicemaster customer add --name "Acme Corp" \
    --email billing@acme.example --region US-CA --currency USD

# 2. Create a draft invoice for them
invoicemaster invoice create CUS-0001 --due 2026-03-01

# 3. Add a line item (8 hours of consulting at 120/h)
invoicemaster invoice add-item INV-2026-0001 \
    --description "Consulting" --qty 8 --price 120

# 4. Issue it (tax is computed from the US-CA rule and frozen)
invoicemaster invoice issue INV-2026-0001

# 5. Record the first payment
invoicemaster payment record INV-2026-0001 --amount 500 --method bank --ref TRX-1042
```

Expected output for steps 1–5 (with `NO_COLOR=1` for plain text):

```text
$ invoicemaster customer add --name "Acme Corp" --email billing@acme.example --region US-CA
Added customer CUS-0001 — Acme Corp
  Region   : US-CA
  Currency : USD
  Email    : billing@acme.example

$ invoicemaster invoice create CUS-0001 --due 2026-03-01
Created draft invoice INV-2026-0001 for CUS-0001 (Acme Corp).
Add items with: invoicemaster invoice add-item INV-2026-0001 --description "..." --qty 1 --price 100

$ invoicemaster invoice add-item INV-2026-0001 --description "Consulting" --qty 8 --price 120
Added item to INV-2026-0001: 8 x Consulting — net $960.00, items now 1.

$ invoicemaster invoice issue INV-2026-0001
Issued invoice INV-2026-0001 (due 2026-03-01).
  Subtotal : $960.00
  Tax      : $69.60
  Total    : $1,029.60

$ invoicemaster payment record INV-2026-0001 --amount 500 --method bank --ref TRX-1042
Recorded payment PAY-000001 ($500.00) on INV-2026-0001.
  Balance : $529.60  Status: Issued
```

Check the state of the world at any time:

```bash
invoicemaster invoice list
invoicemaster report outstanding
invoicemaster report aging --detail
```

## Usage

### Global options

| Option | Description |
|---|---|
| `--json-dir <path>` | Use an alternate data directory instead of `~/.invoicemaster` |
| `--no-color` | Disable ANSI colors for this run |
| `--version` | Print the version and exit |
| `-h, --help` | Show the main help, or help for one command |

The environment variable `INVOICEMASTER_HOME` overrides the default data directory, and
`NO_COLOR` (any non-empty value) disables colors globally, per the no-color.org convention.
Global options may appear before or after the command.

### Command reference

| Command | Summary |
|---|---|
| `customer add` | Register a new customer |
| `customer list` | List or search customers |
| `customer show` | Show one customer in detail |
| `customer update` | Edit fields of one customer |
| `customer delete` | Permanently delete a customer (`--force` required) |
| `invoice create` | Create a draft invoice |
| `invoice add-item` | Append a line item to a draft |
| `invoice issue` | Issue a draft invoice |
| `invoice list` | List invoices with filters |
| `invoice show` | Show one invoice in detail |
| `invoice void` | Cancel an invoice (`--reason` required) |
| `payment record` | Record a payment on an invoice |
| `payment list` | List recorded payments |
| `tax set` | Register or replace a tax rule |
| `tax get` | Show tax rules |
| `report outstanding` | Open balances per invoice |
| `report aging` | 30/60/90 day aging of receivables |
| `report revenue` | Monthly invoiced vs collected series |
| `report top-customers` | Rank customers by revenue |
| `export invoices` | Export invoices to CSV |
| `export payments` | Export payments to CSV |
| `remind` | Draft overdue reminder letters |
| `help` | Show help for everything or one command |
| `version` | Show the version |

Run `invoicemaster help <command>` (e.g. `invoicemaster help invoice add-item`) for the
full option list of any command — the help is generated from the same command registry the
parser validates against, so it can never drift from reality.

### Example: listing invoices

```text
$ invoicemaster invoice list
Number         Customer   Status  Issued      Due            Total     Paid  Balance
-------------  ---------  ------  ----------  ----------  ---------  -------  --------
INV-2026-0001  Acme Corp  Issued  2026-02-10  2026-03-01  $1,029.60  $500.00  $529.60

1 invoice(s), open balance $529.60.
```

Statuses are color-coded in a terminal: `Paid` green, `Overdue` yellow, drafts and voided
invoices dimmed. Filters combine freely: `invoicemaster invoice list --status overdue
--customer CUS-0001 --year 2026`.

### Example: invoice detail with per-line tax

```text
$ invoicemaster invoice show INV-2026-0001
Invoice INV-2026-0001  [Issued]
  Customer : CUS-0001 (Acme Corp)
  Currency : USD
  Region   : US-CA
  Issued   : 2026-02-10
  Due      : 2026-03-01

#  Description  Qty  Unit    Disc %  Net      Category  Tax
-  -----------  ---  ------  ------  -------  --------  -------
1  Consulting   8    120.00  0%      $960.00  Standard  $69.60

  Subtotal  : $960.00
  Discount  : $0.00
  Tax       : $69.60
  Total     : $1,029.60
  Paid      : $500.00
  Balance   : $529.60

Payments:
  PAY-000001  2026-02-15  Bank transfer  $500.00  ref: TRX-1042
```

The `Tax` column shows the per-line amount under the invoice's region rule; drafts show
`-` until a rule applies and `(tax computed at issue time)` in the totals block.

### Example: aging report

```text
$ invoicemaster report aging --as-of 2026-04-15
Aging as of 2026-04-15 (amounts in raw currency units):

Bucket    Invoices      Amount
--------  --------  -------
Current          0        0
1-30             0        0
31-60            1   1029.60
61-90            0        0
90+              0        0

Total outstanding: 1029.60
```

Add `--detail` to list every invoice inside its bucket, most overdue first. Amounts are
raw currency units so mixed-currency portfolios still add up; a note is printed when more
than one currency is present.

### Example: revenue and top customers

```text
$ invoicemaster report revenue --from 2026-01-01
Month      Invoiced  Collected
-------  ----------  ----------
2026-01        1029.60     500.00

Totals: invoiced 1029.60, collected 500.00.
```

```text
$ invoicemaster report top-customers --metric collected
Rank  Customer   Name       Invoices  Invoiced  Collected  Outstanding  Last
----  ---------  ---------  --------  --------  ---------  -----------  ----------
   1  CUS-0001   Acme Corp         1   1029.60     500.00       529.60  2026-02-10

Ranked by collected. Amounts in raw currency units.
```

### Example: CSV export

```text
$ invoicemaster export invoices --status issued
Exported 1 invoice(s) to /home/you/invoices-20260215-093012.csv.

$ head -2 invoices-20260215-093012.csv
invoice_number,status,customer_id,customer_name,issue_date,due_date,currency,subtotal,discount,tax,total,paid,balance,items_count
INV-2026-0001,Issued,CUS-0001,Acme Corp,2026-02-10,2026-03-01,USD,960.00,0.00,69.60,1029.60,500.00,529.60,1
```

### Example: reminder letters

```text
$ invoicemaster remind --as-of 2026-04-15 --out ./letters
Wrote ./letters/reminder-INV-2026-0001-20260415.txt (Firm, 45 days overdue, $1,029.60).

1 reminder letter(s) drafted.
```

Without `--out`, the full letter text is printed to stdout. Severity escalates
automatically: gentle up to 15 days late, firm up to 45, final notice beyond that.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Runtime or validation error (bad data, conflict, storage failure) |
| `2` | Command-line usage error (unknown option, malformed value, missing argument) |

Errors are written to stderr as `invoicemaster: error: ...`; a bug-shaped unexpected
failure never prints a stack trace and never corrupts data files.

## Tax configuration

Taxes come from **region rules**. Eight built-in rules ship with the tool:

| Region | Name | Standard rate | Reduced | Exempt |
|---|---|---|---|---|
| `US-CA` | California sales tax | 7.25% | — | Food, Medicine, Education, Export |
| `US-NY` | New York sales tax | 8.875% | — | Food, Medicine, Education, Export |
| `GB` | United Kingdom VAT | 20% | — | Food, Books, Medicine, Education, Export |
| `DE` | Germany VAT | 19% | Food 7%, Books 7% | Medicine, Export |
| `VN` | Vietnam VAT | 10% | Food/Agriculture/Medicine/Education/Books 5% | Export |
| `SG` | Singapore GST | 9% | — | Export |
| `AU` | Australia GST | 10% | — | Food, Education, Medicine, Export |
| `INTL` | International / default | 0% | — | — |

Every line item carries an **exemption category** (default `Standard`). Known categories:
`Standard`, `Food`, `Medicine`, `Education`, `Books`, `Export`, `Agriculture`,
`NonProfit`, `Other`. Tax is computed per line — net after the line discount, times the
effective rate, rounded half-up in the rule currency's minor units.

Register or replace a rule with `tax set`; rules persist in `taxrules.json` and override
built-ins for the same region:

```bash
# A Texas rule with a reduced rate for books and medicine exempt
invoicemaster tax set --region US-TX --rate 8.25 \
    --name "Texas sales tax" --reduced "Books=0" --exempt Medicine,Export

# A UK-style rule with reduced rates
invoicemaster tax set --region GB --rate 20 --currency GBP \
    --reduced "Food=0,Books=0,ChildrensClothes=0"   # unknown categories are rejected

# Inspect the effective rules
invoicemaster tax get
invoicemaster tax get --region US-TX
```

**Snapshot semantics:** when a draft is issued, the computed tax amount is frozen into the
invoice (`TaxAmountSnapshot`). Editing or deleting the rule afterwards never changes
already-issued invoices — reports and balances stay stable forever.

**Currency support:** amounts are `decimal`-based and formatted with 28 built-in ISO 4217
codes (USD, EUR, GBP, JPY, CNY, VND, KRW, SGD, HKD, AUD, NZD, CAD, CHF, INR, THB, MYR,
PHP, IDR, SEK, NOK, DKK, PLN, TRY, BRL, ZAR, MXN, AED, SAR). Zero-decimal currencies like
JPY, VND, KRW and IDR round to whole units. Unknown codes render as `1,234.50 XYZ`.

## Data storage format

Everything lives in one directory — `~/.invoicemaster` by default, overridden by
`--json-dir` or `INVOICEMASTER_HOME`:

```text
~/.invoicemaster/
├── customers.json    # all customers
├── invoices.json     # all invoices, including their line items and payments
├── taxrules.json     # user-defined tax rules (built-ins are code, not data)
└── backups/
    ├── customers.json.bak.1   # newest backup
    ├── customers.json.bak.2
    └── ...
```

JSON is indented, camelCase, with enums written as readable strings
(`"status": "Issued"`), so the files are pleasant to read and diff:

```json
{
  "number": "INV-2026-0001",
  "customerId": "CUS-0001",
  "currency": "USD",
  "region": "US-CA",
  "status": "Issued",
  "createdAtUtc": "2026-02-10T09:14:03.1234567Z",
  "issueDateUtc": "2026-02-10T00:00:00Z",
  "dueDateUtc": "2026-03-01T00:00:00Z",
  "taxAmountSnapshot": 69.60,
  "items": [
    {
      "description": "Consulting",
      "quantity": 8,
      "unitPrice": 120.00,
      "discountPercent": 0,
      "exemptionCategory": "Standard"
    }
  ],
  "payments": [
    {
      "id": "PAY-000001",
      "invoiceNumber": "INV-2026-0001",
      "amount": 500.00,
      "currency": "USD",
      "method": "BankTransfer",
      "paidAtUtc": "2026-02-15T00:00:00Z",
      "reference": "TRX-1042"
    }
  ]
}
```

Durability guarantees:

- **Atomic writes** — each save writes a temp file and moves it over the destination in
  one step; a crash never leaves a half-written document.
- **Backup rotation** — every successful save shifts `.bak.1..5` (the last five good
  versions are kept).
- **Self-healing loads** — if a main file is corrupt, the newest readable backup is
  restored transparently and a warning is printed to stderr.

Because the format is plain JSON, you can back the folder up with `rsync`, sync it with
git, or inspect it with `jq` — invoicemaster will simply read whatever is valid there.

## Project Structure

```text
invoicemaster/
├── invoicemaster.csproj          # net8.0 exe, RootNamespace InvoiceMaster, zero NuGet
├── Makefile                      # build / run / test / publish / clean helpers
├── .gitignore                    # bin/, obj/, publish/, editor and OS noise
├── LICENSE                       # MIT
├── README.md                     # this file
├── src/
│   ├── Program.cs                # (83 lines) entry point: wiring, exit-code mapping
│   ├── AppException.cs           # (158) InvoiceMasterException hierarchy: usage/validation/
│   │                             #   not-found/conflict/storage, each mapped to an exit code
│   ├── Money.cs                  # (259) decimal money helpers: rounding, 28-currency
│   │                             #   formatting, tolerant amount parsing
│   ├── Cli/
│   │   ├── CliParser.cs          # (464) tokenizer + command registry for all 24 commands
│   │   ├── CommandSpec.cs        # (288) command/option metadata + typed ParsedCommand getters
│   │   ├── Commands.cs           # (598) CliApp wiring + customer/invoice command handlers
│   │   ├── CommandModules.cs     # (516) payment/tax/report/export/remind handlers
│   │   ├── HelpText.cs           # (154) help renderer generated from the command registry
│   │   └── TableRenderer.cs      # (320) ANSI-aware fixed-width tables + color helpers
│   ├── Models/
│   │   ├── Address.cs            # (195) postal address with validation + rendering
│   │   ├── Customer.cs           # (205) customer aggregate: ids, regex shapes, validation
│   │   ├── Invoice.cs            # (483) invoice aggregate: numbering, totals, lifecycle
│   │   ├── LineItem.cs           # (222) line item: qty/price/discount/exemption category
│   │   └── Payment.cs            # (209) payment: methods, ids, validation
│   └── Services/
│       ├── JsonStore.cs          # (345) generic atomic JSON store with backups + recovery
│       ├── CustomerRepository.cs # (241) customers.json repository
│       ├── InvoiceRepository.cs  # (469) invoices.json repository: numbering, lifecycle,
│       │                         #   payments, overdue maintenance
│       ├── TaxCalculator.cs      # (444) TaxRule + regional tax engine + built-in rules
│       ├── ReportService.cs      # (564) outstanding / aging / revenue / top-customers
│       ├── CsvExporter.cs        # (224) RFC 4180 CSV primitives + invoice/payment export
│       └── ReminderService.cs    # (261) severity-escalating reminder letters
└── tests/
    ├── invoicemaster.Tests.csproj # test exe referencing ../src/**/*.cs
    └── TestRunner.cs              # (499) self-contained test runner, 12 suites, zero frameworks
```

~7,200 lines of C# in total, with XML doc comments on every public member.

## Architecture

Four clean layers, dependencies pointing strictly downward:

```text
            ┌─────────────────────────────┐
            │ Program (entry, exit codes) │
            └──────────────┬──────────────┘
                           │
            ┌──────────────▼──────────────┐
            │ Cli: parser, spec registry, │
            │ handlers, help, tables      │
            └──────────────┬──────────────┘
                           │
            ┌──────────────▼──────────────┐
            │ Services: repositories,     │
            │ tax engine, reports, CSV,   │
            │ reminders, JsonStore        │
            └──────────────┬──────────────┘
                           │
            ┌──────────────▼──────────────┐
            │ Models: Customer, Invoice,  │
            │ LineItem, Payment, Address  │
            └─────────────────────────────┘
```

Notable design decisions:

1. **Single source of truth for commands.** `CliParser.BuildSpecs()` registers every
   command with its options and positional bounds; argument validation *and* the help
   renderer both consume that registry, so usage text cannot lie.
2. **Snapshot-on-issue tax.** Taxes are recomputed only while a document is a draft; at
   issue time the amount is frozen. This mirrors real-world accounting, where an issued
   invoice must not mutate when a rate changes.
3. **Validation accumulates instead of failing fast.** `Validate()` methods return every
   problem at once, so a rejected command lists all offending fields in one pass.
4. **Copy-on-write mutations.** Repository updates mutate a `Clone()` of the aggregate,
   validate it, and only then swap it in and persist — a failed edit leaves the stored
   object untouched.
5. **Money is `decimal` or nothing.** No `double` anywhere near an amount; rounding is
   half-up and aware of per-currency minor units; all formatting is invariant-culture so
   CSV columns are byte-stable across machines.
6. **Exceptions carry exit codes.** `UsageException` → 2, everything else deliberate → 1;
   `Program.Main` is the only place that maps exceptions to process codes.

## Testing

The project ships a self-contained test runner with zero test frameworks:

```bash
make test
# or
dotnet run --project tests/invoicemaster.Tests.csproj -c Release
```

Twelve suites run against real temp directories and real JSON files:

| Suite | What it covers |
|---|---|
| money | half-up rounding, per-currency decimals, formatting, tolerant parsing |
| lineitem | gross/discount/net math, category normalization, validation |
| tax | per-line rates, exempt vs reduced regions, breakdowns, custom rules |
| numbering | `INV-yyyy-nnnn` generation, parsing, rejection of malformed numbers |
| lifecycle | draft → issue → paid, overdue detection, void rules, status guards |
| totals | subtotal/discount/tax/paid/balance aggregation |
| aging | 30/60/90 buckets end-to-end through repositories and reports |
| csv | RFC 4180 escaping and row joining |
| table | column width computation, truncation, alignment |
| store | atomic saves, backup rotation, corrupt-file recovery |
| repository | id sequencing, duplicate rejection, update isolation, persistence |
| payment | method aliases, id generation, full-settlement transitions |

The runner prints one `PASS`/`FAIL` line per check, a summary block, and exits `0` only
when every check passes — drop it straight into CI.

## FAQ

**Q: Where is my data stored?**
`~/.invoicemaster/` as plain JSON. Override with `--json-dir` for one run or
`INVOICEMASTER_HOME` for an environment. Nothing is ever sent anywhere.

**Q: Can I edit the JSON files by hand?**
Yes — they are the source of truth. Keep the shapes valid (ids, ISO dates, statuses as
strings); the loader validates and repairs what it can, and refuses to crash on junk.

**Q: What happens if a data file gets corrupted?**
`JsonStore` automatically restores the newest readable backup and warns on stderr. The
last five versions of each file are kept under `~/.invoicemaster/backups/`.

**Q: Can I use it for multiple businesses?**
Point each one at its own directory: `INVOICEMASTER_HOME=~/.acme invoicemaster ...` or
`--json-dir ~/.acme` per run.

**Q: Does issuing an invoice lock the tax rate?**
Yes. The tax amount is snapshotted at issue time. Later `tax set` changes affect only
drafts and future invoices.

**Q: What if a customer overpays?**
`payment record` refuses amounts above the balance unless you pass `--allow-overpay`,
which records the excess as credit (a negative balance due).

**Q: How do I void an invoice?**
`invoicemaster invoice void INV-2026-0001 --reason "duplicate"`. Paid invoices cannot be
voided — record a refund as a payment instead. Voided invoices are excluded from reports.

**Q: Why do report amounts sometimes lack currency symbols?**
Bucketed/ranked reports sum across invoices that may use different currencies, so they
print raw numeric units and note the mixed-currency case. Per-invoice listings always
format with the invoice's currency.

**Q: Does it work without internet?**
Completely. There are no network calls, no telemetry, no updates to check.

**Q: Can I script it?**
That is the point: stable exit codes (0/1/2), stderr-only errors, invariant output, and
CSV exports designed for `awk`/pandas.

## Roadmap

- [ ] `--json` machine-readable output mode for every command
- [ ] Credit notes and refunds as first-class documents
- [ ] Recurring invoices with monthly/annual schedules
- [ ] PDF invoice rendering (ASCII layout already defined by `invoice show`)
- [ ] Multi-currency exchange rates for cross-currency reports
- [ ] `invoice send` stubs for SMTP and file-based mail drop
- [ ] SQLite storage backend as an alternative to JSON
- [ ] Shell completions (bash/zsh/fish) generated from the command registry

## License

Released under the [MIT License](LICENSE) — free to use, modify and distribute.

---
**by Bui Bao Khanh**
