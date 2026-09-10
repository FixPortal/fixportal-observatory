# Billed Spend Ledger — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record and query real billed spend by hand — three tables, a CRUD API whose write endpoint takes an array, date-correct currency conversion, a manual entry form, and a filterable ledger table showing the total of whatever is on screen.

**Architecture:** A spend ledger kept physically separate from the token-estimate pipeline (`UsageEvent` / `DailyAggregate`), joined to it only via a nullable `SpendVendor.Provider`. Categories and vendors are user-managed rows, not enums, so a new spend type is data entry rather than a deploy. Amounts are converted to GBP once at write using the rate on the charge date and stored, so historical totals never drift.

**Tech Stack:** .NET 10, EF Core + Npgsql + NodaTime, minimal APIs; React 19 + Vite + TypeScript + TanStack Query; xUnit v3 + AwesomeAssertions + NSubstitute; vitest.

**Spec:** `docs/superpowers/specs/2026-07-26-billed-spend-ledger-design.md`

## Global Constraints

- **Entities in `AiObservatory.Data.Entities` MUST be `sealed`** — `ArchitectureTests.Model_types_must_be_sealed` fails otherwise.
- **Interfaces MUST be `I`-prefixed** — `ArchitectureTests.Interfaces_must_have_I_prefix`.
- **No emoji anywhere** — code, comments, commit messages, PR bodies.
- **`SpendEntry` must never gain a property matching** `account|card|counterparty|iban|sortcode|transactionid` — this is the privacy boundary and Task 1 adds a test enforcing it.
- **Assert with AwesomeAssertions `.Should()`**, never `Assert.*`. Namespace is `AwesomeAssertions`.
- **WAF tests use `TestContext.Current.CancellationToken`**, never `CancellationToken.None`.
- **Pure-function tests go in `AiObservatory.Ingest.Tests`** — `Data.Tests` and `Api.Tests` need a local PostgreSQL (`docker compose up -d db`), and pure tests must stay runnable in the pre-push gate without it.
- **Endpoints go under the existing `/api` group**, which already applies `ApiKeyEndpointFilter`: GET = readonly-or-admin, any write = admin. Add no auth code.
- **Pre-push gate, all four must pass:** `npx tsc -b --noEmit`, `npx eslint .`, `npx vitest run` (in `src/AiObservatory.Web`), and `dotnet build` + `dotnet test`.
- **Money is `decimal`**, never `double`.
- **Dates that carry domain meaning are NodaTime** — `LocalDate` for a charge date, `Instant` for a timestamp.

## File Structure

**Create:**
| Path | Responsibility |
|---|---|
| `src/AiObservatory.Data/Entities/SpendCategory.cs` | Category entity |
| `src/AiObservatory.Data/Entities/SpendVendor.cs` | Vendor entity + nullable `Provider` link |
| `src/AiObservatory.Data/Entities/SpendEntry.cs` | Ledger row |
| `src/AiObservatory.Data/Entities/SpendSource.cs` | `Manual \| Csv \| Portal` enum |
| `src/AiObservatory.Data/Spend/SpendEntryKey.cs` | Pure key-derivation function |
| `src/AiObservatory.Api/Endpoints/SpendCatalogEndpoints.cs` | Category + vendor CRUD |
| `src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs` | Ledger CRUD, array POST |
| `src/AiObservatory.Web/src/pages/SpendPage.tsx` | Page shell, filter state |
| `src/AiObservatory.Web/src/components/SpendFilterBar.tsx` | Region 1 |
| `src/AiObservatory.Web/src/components/SpendTotals.tsx` | Region 2 |
| `src/AiObservatory.Web/src/components/SpendLedgerTable.tsx` | Region 6 |
| `src/AiObservatory.Web/src/components/SpendEntryModal.tsx` | Manual entry form |
| `src/AiObservatory.Web/src/lib/spendFilters.ts` | Pure filter/total helpers |

**Modify:**
| Path | Change |
|---|---|
| `src/AiObservatory.Data/AiObservatoryDbContext.cs` | 3 `DbSet`s + `OnModelCreating` config |
| `src/AiObservatory.Api/Services/Fx/FxRateProvider.cs` | Dated rate lookup |
| `src/AiObservatory.Api/Program.cs:326` | Register the two endpoint groups |
| `src/AiObservatory.Web/src/api/client.ts` | Types + fetch functions |
| `src/AiObservatory.Web/src/api/queries.ts` | Hooks |
| `src/AiObservatory.Web/src/pages/Dashboard.tsx:19-27` | Add the `spend` tab |
| `tests/AiObservatory.Api.Tests/ArchitectureTests.cs` | Privacy-boundary test |

Two endpoint files rather than one: the catalog (vendors, categories) and the ledger change for different reasons and have different shapes. Splitting by responsibility keeps each holdable in context.

---

### Task 1: Entities, schema, and the privacy boundary

**Files:**
- Create: `src/AiObservatory.Data/Entities/SpendSource.cs`, `SpendCategory.cs`, `SpendVendor.cs`, `SpendEntry.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Test: `tests/AiObservatory.Api.Tests/ArchitectureTests.cs`

**Interfaces:**
- Produces: `SpendCategory`, `SpendVendor`, `SpendEntry`, `SpendSource` in `AiObservatory.Data.Entities`; `db.SpendCategories`, `db.SpendVendors`, `db.SpendEntries`.

- [ ] **Step 1: Write the failing privacy test**

Append to `tests/AiObservatory.Api.Tests/ArchitectureTests.cs` (inside the class):

```csharp
    // The ledger deliberately holds no link back to a bank, card, invoice or
    // counterparty — that is the privacy boundary that let billed spend live in a
    // public repo at all (spec §3). A convention would erode; this makes it fail
    // the build. If a future feature genuinely needs one of these, that is a
    // design decision to reopen in the spec, not a test to relax.
    [Fact]
    public void SpendEntry_must_not_carry_bank_linkage()
    {
        var forbidden = new[] { "account", "card", "counterparty", "iban", "sortcode", "transactionid" };

        var offenders = typeof(AiObservatory.Data.Entities.SpendEntry)
            .GetProperties()
            .Where(p => forbidden.Any(f =>
                p.Name.Replace("_", "").Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToArray();

        offenders.Should().BeEmpty(
            "SpendEntry must not tie spend to a bank, card, invoice or counterparty (spec §3)");
    }
```

Add `using AwesomeAssertions;` to the file's usings if absent.

- [ ] **Step 2: Run it to verify it fails**

```
dotnet test tests/AiObservatory.Api.Tests --filter SpendEntry_must_not_carry_bank_linkage
```
Expected: FAIL to compile — `SpendEntry` does not exist.

- [ ] **Step 3: Create the four entity files**

`src/AiObservatory.Data/Entities/SpendSource.cs`:

```csharp
namespace AiObservatory.Data.Entities;

/// <summary>How a <see cref="SpendEntry"/> reached the ledger. Provenance only — never a bank reference.</summary>
public enum SpendSource { Manual, Csv, Portal }
```

`src/AiObservatory.Data/Entities/SpendCategory.cs`:

```csharp
using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// A user-managed spend category ("Code Review", "Credits", "CI"). Categories are data
/// rather than an enum so a new spend type needs no migration or deploy.
/// </summary>
public sealed class SpendCategory
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable slug ("code-review"). Imports and the portal feed reference this, so
    /// renaming <see cref="DisplayName"/> never breaks a feed.</summary>
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>CSS custom-property name used for this category's colour in charts.</summary>
    public string ColorVar { get; set; } = "";

    public int SortOrder { get; set; }

    /// <summary>Soft delete. A retired category is hidden from pickers but must still
    /// resolve for the historical rows that reference it.</summary>
    public Instant? ArchivedAt { get; set; }
}
```

`src/AiObservatory.Data/Entities/SpendVendor.cs`:

```csharp
using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// A user-managed vendor. Distinct from <see cref="Provider"/>, which means "a provider
/// whose tokens we can meter" — vendors include CodeRabbit, Gitar and GitHub Actions,
/// which have no tokens at all.
/// </summary>
public sealed class SpendVendor
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable slug ("coderabbit").</summary>
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Optional link to a token provider. The ONLY join between billed spend and the
    /// estimate, so variance is possible exactly where an estimate exists and structurally
    /// impossible where it does not. Null for CodeRabbit, Gitar, GitHub Actions.
    /// </summary>
    public Provider? Provider { get; set; }

    /// <summary>Pre-fills the entry form and lets a CSV omit the category column.
    /// A default, never a constraint — Anthropic spend lands in several categories.</summary>
    public Guid? DefaultCategoryId { get; set; }

    public Instant? ArchivedAt { get; set; }
}
```

`src/AiObservatory.Data/Entities/SpendEntry.cs`:

```csharp
using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// One billed charge. Deliberately carries NO account, card, counterparty, invoice number
/// or bank transaction id — see spec §3, enforced by
/// <c>ArchitectureTests.SpendEntry_must_not_carry_bank_linkage</c>.
/// </summary>
public sealed class SpendEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The charge date. Drives both the reporting period and the FX rate used.</summary>
    public LocalDate OccurredOn { get; set; }

    public Guid VendorId { get; set; }
    public Guid CategoryId { get; set; }

    /// <summary>Amount as charged, in <see cref="Currency"/>.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217, upper case.</summary>
    public string Currency { get; set; } = "GBP";

    /// <summary>
    /// <see cref="Amount"/> in GBP, converted once at write using the rate on
    /// <see cref="OccurredOn"/> and never recomputed. Totals sum this column. Converting at
    /// render instead — the convention used for token costs — would make a historical
    /// charge show a different figure every day and an annual total drift with the market.
    /// </summary>
    public decimal AmountGbp { get; set; }

    /// <summary>The rate actually applied, so every conversion is auditable. 1 when
    /// <see cref="Currency"/> is GBP.</summary>
    public decimal FxRate { get; set; }

    public string? Description { get; set; }

    public SpendSource Source { get; set; }

    /// <summary>
    /// Idempotency key, unique per source. Null for manual entries: a person typing the
    /// same charge twice is a mistake worth showing them, not one to silence.
    /// </summary>
    public string? EntryKey { get; set; }

    public Instant RecordedAt { get; set; }
}
```

- [ ] **Step 4: Register in the DbContext**

In `src/AiObservatory.Data/AiObservatoryDbContext.cs`, after the `GitHubWorkflowRuns` line (~line 19):

```csharp
    public DbSet<SpendCategory> SpendCategories => Set<SpendCategory>();
    public DbSet<SpendVendor> SpendVendors => Set<SpendVendor>();
    public DbSet<SpendEntry> SpendEntries => Set<SpendEntry>();
```

And inside `OnModelCreating`, after the `DailyAggregate` block:

```csharp
        modelBuilder.Entity<SpendCategory>(b =>
        {
            b.Property(c => c.Key).HasMaxLength(60);
            b.Property(c => c.DisplayName).HasMaxLength(100);
            b.Property(c => c.ColorVar).HasMaxLength(60);
            b.HasIndex(c => c.Key).IsUnique();
        });

        modelBuilder.Entity<SpendVendor>(b =>
        {
            b.Property(v => v.Key).HasMaxLength(60);
            b.Property(v => v.DisplayName).HasMaxLength(100);
            b.Property(v => v.Provider).HasConversion<string>();
            b.HasIndex(v => v.Key).IsUnique();
        });

        modelBuilder.Entity<SpendEntry>(b =>
        {
            b.Property(e => e.Currency).HasMaxLength(3);
            b.Property(e => e.Description).HasMaxLength(200);
            b.Property(e => e.EntryKey).HasMaxLength(200);
            b.Property(e => e.Source).HasConversion<string>();
            b.HasIndex(e => e.OccurredOn);
            b.HasIndex(e => e.VendorId);
            b.HasIndex(e => e.CategoryId);

            // Idempotency, scoped per source. Filtered so manual rows (EntryKey null) are
            // exempt — PostgreSQL would allow repeated NULLs anyway, but the filter makes
            // the intent explicit and keeps the index small.
            b.HasIndex(e => new { e.Source, e.EntryKey })
             .IsUnique()
             .HasFilter("\"EntryKey\" IS NOT NULL");

            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_SpendEntry_Amount_NonNegative", "\"Amount\" >= 0");
                t.HasCheckConstraint("CK_SpendEntry_AmountGbp_NonNegative", "\"AmountGbp\" >= 0");
                t.HasCheckConstraint("CK_SpendEntry_FxRate_Positive", "\"FxRate\" > 0");
            });
        });
```

- [ ] **Step 5: Generate the migration**

```
dotnet ef migrations add AddSpendLedger -p src/AiObservatory.Data -s src/AiObservatory.Api
```

Open the generated `Up` and confirm it creates three tables, the unique indexes on `Key`, the filtered unique index on `(Source, EntryKey)`, and the three check constraints. No data migration is needed — these are new tables.

- [ ] **Step 6: Run the test to verify it passes**

```
docker compose up -d db
dotnet test tests/AiObservatory.Api.Tests --filter SpendEntry_must_not_carry_bank_linkage
```
Expected: PASS.

- [ ] **Step 7: Full build and test**

```
dotnet build
dotnet test
```
Expected: all green. The migration applies against the WAF's real PostgreSQL during `Api.Tests` startup, so a broken migration surfaces here.

- [ ] **Step 8: Commit**

```bash
git add src/AiObservatory.Data tests/AiObservatory.Api.Tests/ArchitectureTests.cs
git commit -m "feat: add spend ledger tables

Three tables behind the billed-spend feature: user-managed SpendCategory and
SpendVendor, and SpendEntry as the ledger itself. Categories and vendors are data
rather than enums so a new spend type is data entry, not a migration.

SpendVendor.Provider is nullable and is the only join to the token pipeline,
which keeps Provider meaning 'a provider whose tokens we can meter' rather than
stretching it over CI minutes.

SpendEntry carries no account, card, counterparty or transaction id. An
architecture test enforces that rather than leaving it as a convention."
```

---

### Task 2: Date-correct FX

**Files:**
- Modify: `src/AiObservatory.Api/Services/Fx/FxRateProvider.cs`
- Test: `tests/AiObservatory.Api.Tests/Services/FxRateProviderTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `Task<decimal> FxRateProvider.GetGbpRateOnAsync(string currency, LocalDate on, CancellationToken ct = default)` — returns the multiplier that converts one unit of `currency` into GBP on that date. Returns `1m` for GBP.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiObservatory.Api.Tests/Services/FxRateProviderTests.cs`:

```csharp
using System.Net;
using AiObservatory.Api.Services.Fx;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

/// <summary>
/// The ledger converts once, at write, using the rate on the CHARGE date — not "now".
/// Converting at render would make a historical charge show a different figure every day.
/// </summary>
public class FxRateProviderTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static FxRateProvider Create(StubHandler handler) =>
        new(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<FxRateProvider>.Instance);

    [Fact]
    public async Task GbpShortCircuitsToOneAndMakesNoRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("GBP", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        rate.Should().Be(1m);
        handler.Requested.Should().BeEmpty("GBP needs no conversion, so it must not cost a network call");
    }

    [Fact]
    public async Task UsesTheDatedEndpointForTheChargeDate()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("USD", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        rate.Should().Be(0.7412m);
        handler.Requested.Should().ContainSingle()
            .Which.Should().Contain("/v1/2026-03-15").And.Contain("from=USD");
    }

    [Fact]
    public async Task CachesPerDateSoTheSameDayIsFetchedOnce()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);
        var date = new LocalDate(2026, 3, 15);

        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);
        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);

        handler.Requested.Should().ContainSingle("a historical rate is immutable, so it caches indefinitely");
    }

    [Fact]
    public async Task FallsBackRatherThanFailingTheWrite()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("USD", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        rate.Should().BeGreaterThan(0m, "an FX outage must not block recording a real charge");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```
dotnet test tests/AiObservatory.Api.Tests --filter FxRateProviderTests
```
Expected: FAIL to compile — `GetGbpRateOnAsync` does not exist.

- [ ] **Step 3: Implement**

Add to `FxRateProvider` (keep `GetUsdToGbpAsync` untouched — the token dashboard still uses it):

```csharp
    /// <summary>
    /// Rate converting one unit of <paramref name="currency"/> into GBP on
    /// <paramref name="on"/>. The ledger freezes this at write, so a historical total never
    /// drifts with the market. Historical rates are immutable and therefore cached without
    /// expiry, unlike the 12-hour cache on the latest rate.
    /// </summary>
    public virtual async Task<decimal> GetGbpRateOnAsync(
        string currency, LocalDate on, CancellationToken ct = default)
    {
        var code = currency.ToUpperInvariant();
        if (code == "GBP")
        {
            return 1m;
        }

        var key = $"fx:{code}-gbp:{on:yyyy-MM-dd}";
        if (cache.TryGetValue(key, out decimal cached))
        {
            return cached;
        }

        try
        {
            var resp = await http.GetFromJsonAsync<FrankfurterResponse>(
                $"https://api.frankfurter.dev/v1/{on:yyyy-MM-dd}?from={code}&to=GBP", ct);
            var rate = resp?.Rates is { } rates && rates.TryGetValue("GBP", out var gbp) ? gbp : 0m;

            if (rate <= 0m)
            {
                logger.LogWarning("FX {Code}->GBP missing for {Date}; using fallback {Fallback}", code, on, Fallback);
                return Fallback; // not cached — allow a retry
            }

            cache.Set(key, rate);   // no expiry: a past date's rate cannot change
            return rate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FX fetch failed for {Code} on {Date}; using fallback {Fallback}", code, on, Fallback);
            return Fallback;
        }
    }
```

Add `using NodaTime;` to the file.

- [ ] **Step 4: Run to verify they pass**

```
dotnet test tests/AiObservatory.Api.Tests --filter FxRateProviderTests
```
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Api/Services/Fx/FxRateProvider.cs tests/AiObservatory.Api.Tests/Services/FxRateProviderTests.cs
git commit -m "feat: resolve FX at the charge date, not at render

The ledger stores a GBP amount frozen at write. Converting at render -- the
convention token costs use -- is right for an estimate and wrong for a record of
what was paid: a March top-up would show a different figure every day and an
annual total would drift with the market.

Historical rates are immutable, so they cache without expiry rather than on the
12-hour cycle the latest rate uses. GBP short-circuits without a network call.
An FX outage falls back rather than blocking the write, and the rate used is
stored on the row so the conversion stays auditable."
```

---

### Task 3: Entry-key derivation

**Files:**
- Create: `src/AiObservatory.Data/Spend/SpendEntryKey.cs`
- Test: `tests/AiObservatory.Ingest.Tests/Spend/SpendEntryKeyTests.cs` (create)

Placed in `Ingest.Tests` because it is a pure function and that project runs without PostgreSQL — the same reasoning as `AnthropicPricingResolverTests`.

**Interfaces:**
- Produces: `static string SpendEntryKey.Derive(LocalDate occurredOn, string vendorKey, decimal amount, string currency, string? description, int occurrence)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiObservatory.Ingest.Tests/Spend/SpendEntryKeyTests.cs`:

```csharp
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Spend;

/// <summary>
/// The occurrence index is load-bearing. Without it two genuine identical charges on the
/// same day collide and the second silently vanishes — a quiet under-count, which is the
/// failure class this project has already been burned by.
/// </summary>
public class SpendEntryKeyTests
{
    private static readonly LocalDate Date = new(2026, 7, 12);

    [Fact]
    public void SameInputsProduceTheSameKey()
    {
        var a = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var b = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);

        a.Should().Be(b, "re-importing the same file must be a no-op");
    }

    [Fact]
    public void OccurrenceIndexDistinguishesIdenticalCharges()
    {
        var first = SpendEntryKey.Derive(Date, "anthropic", 5.00m, "GBP", "Top-up", 0);
        var second = SpendEntryKey.Derive(Date, "anthropic", 5.00m, "GBP", "Top-up", 1);

        second.Should().NotBe(first, "two genuine identical charges must both survive");
    }

    [Theory]
    [InlineData("anthropic", 80.00, "GBP", "Top-up")]
    [InlineData("coderabbit", 80.00, "GBP", "Top-up")]   // vendor differs
    [InlineData("anthropic", 80.01, "GBP", "Top-up")]    // amount differs
    [InlineData("anthropic", 80.00, "USD", "Top-up")]    // currency differs
    [InlineData("anthropic", 80.00, "GBP", "Credits")]   // description differs
    public void EveryInputParticipatesInTheKey(string vendor, double amount, string currency, string description)
    {
        var baseline = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var candidate = SpendEntryKey.Derive(Date, vendor, (decimal)amount, currency, description, 0);

        if (vendor == "anthropic" && amount == 80.00 && currency == "GBP" && description == "Top-up")
        {
            candidate.Should().Be(baseline);
        }
        else
        {
            candidate.Should().NotBe(baseline);
        }
    }

    [Fact]
    public void DateParticipatesInTheKey()
    {
        var a = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var b = SpendEntryKey.Derive(Date.PlusDays(1), "anthropic", 80.00m, "GBP", "Top-up", 0);

        b.Should().NotBe(a);
    }

    [Fact]
    public void NullAndEmptyDescriptionAreTheSame()
    {
        var withNull = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", null, 0);
        var withEmpty = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "", 0);

        withNull.Should().Be(withEmpty, "a blank CSV cell and an absent one are the same charge");
    }

    [Fact]
    public void KeyFitsTheColumn()
    {
        var key = SpendEntryKey.Derive(Date, new string('v', 500), 80.00m, "GBP", new string('d', 500), 0);

        key.Length.Should().BeLessThanOrEqualTo(200, "EntryKey is varchar(200)");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```
dotnet test tests/AiObservatory.Ingest.Tests --filter SpendEntryKeyTests
```
Expected: FAIL to compile — `SpendEntryKey` does not exist.

- [ ] **Step 3: Implement**

Create `src/AiObservatory.Data/Spend/SpendEntryKey.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NodaTime;

namespace AiObservatory.Data.Spend;

/// <summary>
/// Derives the idempotency key for an imported spend row, so re-importing a file lands
/// nothing rather than doubling a total.
/// </summary>
public static class SpendEntryKey
{
    /// <param name="occurrence">
    /// Zero-based index among the rows of the SAME import that share every other input.
    /// Load-bearing: without it, two genuine identical charges on one day collide and the
    /// second is silently dropped. Scoped to one file, which is a known and accepted limit
    /// (spec §6) — identical charges split across two imports still collide, and the fix
    /// is to differentiate the description.
    /// </param>
    public static string Derive(
        LocalDate occurredOn,
        string vendorKey,
        decimal amount,
        string currency,
        string? description,
        int occurrence)
    {
        // Invariant culture throughout: a machine with a comma decimal separator must not
        // derive a different key for the same charge. The pipe separator stops fields
        // running together, so ("ab","c") and ("a","bc") cannot hash alike.
        var material = string.Join('|',
            occurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            vendorKey.Trim().ToLowerInvariant(),
            amount.ToString("F4", CultureInfo.InvariantCulture),
            currency.Trim().ToUpperInvariant(),
            (description ?? string.Empty).Trim(),
            occurrence.ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash);   // 64 chars, comfortably inside varchar(200)
    }
}
```

- [ ] **Step 4: Run to verify they pass**

```
dotnet test tests/AiObservatory.Ingest.Tests --filter SpendEntryKeyTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Data/Spend tests/AiObservatory.Ingest.Tests/Spend
git commit -m "feat: derive idempotency keys for imported spend rows

Content hash over date, vendor, amount, currency and description, plus an
occurrence index within the import. The index is the part that matters: without
it two genuine identical charges on the same day collide and the second silently
vanishes.

Invariant culture throughout, so a machine with a comma decimal separator cannot
derive a different key for the same charge. Tests live in Ingest.Tests because
the function is pure and that project runs without PostgreSQL."
```

---

### Task 4: Category and vendor endpoints

**Files:**
- Create: `src/AiObservatory.Api/Endpoints/SpendCatalogEndpoints.cs`
- Modify: `src/AiObservatory.Api/Program.cs:326`
- Test: `tests/AiObservatory.Api.Tests/SpendCatalogEndpointsWafTests.cs` (create)

**Interfaces:**
- Consumes: `SpendCategory`, `SpendVendor` (Task 1).
- Produces: `GET|POST /api/spend/categories`, `PATCH /api/spend/categories/{id}`, same for `vendors`; `MapSpendCatalogEndpoints`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiObservatory.Api.Tests/SpendCatalogEndpointsWafTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

public class SpendCatalogEndpointsWafTests(AiObservatoryApiFactory factory)
    : IClassFixture<AiObservatoryApiFactory>
{
    private static object NewCategory(string key) =>
        new { Key = key, DisplayName = "Code Review", ColorVar = "--spend-code-review", SortOrder = 10 };

    [Fact]
    public async Task PostCategory_CreatesAndListsIt()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key),
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/spend/categories",
            TestContext.Current.CancellationToken);
        list.EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .Should().Contain(key);
    }

    [Fact]
    public async Task PostCategory_WithDuplicateKey_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key), TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key), TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostVendor_WithUnknownProvider_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/spend/vendors",
            new { Key = $"v-{Guid.NewGuid():N}", DisplayName = "Nope", Provider = "not-a-provider" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostVendor_WithNullProvider_IsAccepted()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/spend/vendors",
            new { Key = $"v-{Guid.NewGuid():N}", DisplayName = "CodeRabbit", Provider = (string?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "vendors with no token estimate are the point of a separate vendor axis");
    }

    [Fact]
    public async Task ArchivedCategory_IsExcludedFromTheDefaultList()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key), TestContext.Current.CancellationToken);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();

        var patch = await client.PatchAsJsonAsync($"/api/spend/categories/{id}",
            new { Archived = true }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/spend/categories", TestContext.Current.CancellationToken);
        list.EnumerateArray().Select(c => c.GetProperty("key").GetString()).Should().NotContain(key);

        var all = await client.GetFromJsonAsync<JsonElement>("/api/spend/categories?includeArchived=true",
            TestContext.Current.CancellationToken);
        all.EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .Should().Contain(key, "history still references archived categories");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```
docker compose up -d db
dotnet test tests/AiObservatory.Api.Tests --filter SpendCatalogEndpointsWafTests
```
Expected: FAIL — 404s, endpoints not mapped.

- [ ] **Step 3: Implement the endpoints**

Create `src/AiObservatory.Api/Endpoints/SpendCatalogEndpoints.cs`:

```csharp
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

/// <summary>
/// Categories and vendors — the user-managed axes of the spend ledger. Kept apart from
/// the ledger endpoints: these change rarely and for different reasons.
/// </summary>
public static class SpendCatalogEndpoints
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapSpendCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/spend/categories", GetCategoriesAsync);
        app.MapPost("/spend/categories", CreateCategoryAsync);
        app.MapPatch("/spend/categories/{id:guid}", PatchCategoryAsync);

        app.MapGet("/spend/vendors", GetVendorsAsync);
        app.MapPost("/spend/vendors", CreateVendorAsync);
        app.MapPatch("/spend/vendors/{id:guid}", PatchVendorAsync);

        return app;
    }

    private static async Task<IResult> GetCategoriesAsync(
        AiObservatoryDbContext db, CancellationToken ct, bool includeArchived = false)
    {
        var q = db.SpendCategories.AsNoTracking();
        if (!includeArchived)
        {
            q = q.Where(c => c.ArchivedAt == null);
        }

        return Results.Ok(await q.OrderBy(c => c.SortOrder).ThenBy(c => c.DisplayName).ToListAsync(ct));
    }

    private static async Task<IResult> CreateCategoryAsync(
        SpendCategoryRequest req, AiObservatoryDbContext db, CancellationToken ct)
    {
        var key = Slug(req.Key);
        if (key is null || string.IsNullOrWhiteSpace(req.DisplayName) || req.DisplayName.Length > 100)
        {
            return Results.BadRequest("Key must be a slug of 60 characters or fewer and DisplayName is required");
        }

        if (await db.SpendCategories.AnyAsync(c => c.Key == key, ct))
        {
            return Results.Conflict($"Category key already exists: {key}");
        }

        var category = new SpendCategory
        {
            Key = key,
            DisplayName = req.DisplayName.Trim(),
            ColorVar = req.ColorVar?.Trim() ?? "",
            SortOrder = req.SortOrder,
        };
        db.SpendCategories.Add(category);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/spend/categories/{category.Id}", category);
    }

    private static async Task<IResult> PatchCategoryAsync(
        Guid id, SpendCatalogPatchRequest req, AiObservatoryDbContext db, IClock clock, CancellationToken ct)
    {
        var category = await db.SpendCategories.FindAsync([id], ct);
        if (category is null)
        {
            return Results.NotFound();
        }

        if (req.DisplayName is { } name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            {
                return Results.BadRequest("DisplayName is required and must be 100 characters or fewer");
            }
            category.DisplayName = name.Trim();
        }

        if (req.ColorVar is { } color) { category.ColorVar = color.Trim(); }
        if (req.SortOrder is { } order) { category.SortOrder = order; }
        // Archiving is a soft delete: historical entries keep resolving their category.
        if (req.Archived is { } archived) { category.ArchivedAt = archived ? clock.GetCurrentInstant() : null; }

        await db.SaveChangesAsync(ct);
        return Results.Ok(category);
    }

    private static async Task<IResult> GetVendorsAsync(
        AiObservatoryDbContext db, CancellationToken ct, bool includeArchived = false)
    {
        var q = db.SpendVendors.AsNoTracking();
        if (!includeArchived)
        {
            q = q.Where(v => v.ArchivedAt == null);
        }

        return Results.Ok(await q.OrderBy(v => v.DisplayName).ToListAsync(ct));
    }

    private static async Task<IResult> CreateVendorAsync(
        SpendVendorRequest req, AiObservatoryDbContext db, CancellationToken ct)
    {
        var key = Slug(req.Key);
        if (key is null || string.IsNullOrWhiteSpace(req.DisplayName) || req.DisplayName.Length > 100)
        {
            return Results.BadRequest("Key must be a slug of 60 characters or fewer and DisplayName is required");
        }

        // Null is legitimate and common: CodeRabbit, Gitar and GitHub Actions have no
        // token estimate to compare against. Only a non-null unparseable value is an error.
        Provider? provider = null;
        if (!string.IsNullOrWhiteSpace(req.Provider))
        {
            if (!Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Results.BadRequest($"Unknown provider: {req.Provider}");
            }
            provider = parsed;
        }

        if (await db.SpendVendors.AnyAsync(v => v.Key == key, ct))
        {
            return Results.Conflict($"Vendor key already exists: {key}");
        }

        if (req.DefaultCategoryId is { } categoryId
            && !await db.SpendCategories.AnyAsync(c => c.Id == categoryId, ct))
        {
            return Results.BadRequest($"Unknown DefaultCategoryId: {categoryId}");
        }

        var vendor = new SpendVendor
        {
            Key = key,
            DisplayName = req.DisplayName.Trim(),
            Provider = provider,
            DefaultCategoryId = req.DefaultCategoryId,
        };
        db.SpendVendors.Add(vendor);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/spend/vendors/{vendor.Id}", vendor);
    }

    private static async Task<IResult> PatchVendorAsync(
        Guid id, SpendVendorPatchRequest req, AiObservatoryDbContext db, IClock clock, CancellationToken ct)
    {
        var vendor = await db.SpendVendors.FindAsync([id], ct);
        if (vendor is null)
        {
            return Results.NotFound();
        }

        if (req.DisplayName is { } name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            {
                return Results.BadRequest("DisplayName is required and must be 100 characters or fewer");
            }
            vendor.DisplayName = name.Trim();
        }

        if (req.DefaultCategoryId is { } categoryId)
        {
            if (!await db.SpendCategories.AnyAsync(c => c.Id == categoryId, ct))
            {
                return Results.BadRequest($"Unknown DefaultCategoryId: {categoryId}");
            }
            vendor.DefaultCategoryId = categoryId;
        }

        if (req.Archived is { } archived) { vendor.ArchivedAt = archived ? clock.GetCurrentInstant() : null; }

        await db.SaveChangesAsync(ct);
        return Results.Ok(vendor);
    }

    /// <summary>Normalises a key to a lower-case slug, or null when it cannot be one.</summary>
    private static string? Slug(string? raw)
    {
        var trimmed = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 60)
        {
            return null;
        }

        return trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? trimmed : null;
    }
}

public sealed record SpendCategoryRequest(string Key, string DisplayName, string? ColorVar, int SortOrder);

public sealed record SpendVendorRequest(string Key, string DisplayName, string? Provider, Guid? DefaultCategoryId);

public sealed record SpendCatalogPatchRequest(string? DisplayName, string? ColorVar, int? SortOrder, bool? Archived);

public sealed record SpendVendorPatchRequest(string? DisplayName, Guid? DefaultCategoryId, bool? Archived);
```

- [ ] **Step 4: Register in Program.cs**

After `api.MapBudgetRulesEndpoints();` (~line 327):

```csharp
api.MapSpendCatalogEndpoints();
```

- [ ] **Step 5: Run to verify they pass**

```
dotnet test tests/AiObservatory.Api.Tests --filter SpendCatalogEndpointsWafTests
```
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/AiObservatory.Api/Endpoints/SpendCatalogEndpoints.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.Tests/SpendCatalogEndpointsWafTests.cs
git commit -m "feat: add spend category and vendor endpoints

CRUD for the two user-managed axes. Archiving is a soft delete so a retired
category disappears from pickers while historical rows still resolve it;
includeArchived=true exposes the full set.

A vendor's Provider link is optional by design -- CodeRabbit, Gitar and GitHub
Actions have no token estimate -- so only a non-null unparseable value is an
error, never its absence."
```

---

### Task 5: Recording entries — the array POST

**Files:**
- Create: `src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs`
- Modify: `src/AiObservatory.Api/Program.cs`
- Test: `tests/AiObservatory.Api.Tests/SpendEntriesEndpointsWafTests.cs` (create)

**Interfaces:**
- Consumes: `SpendEntry` (Task 1), `FxRateProvider.GetGbpRateOnAsync` (Task 2).
- Produces: `POST /api/spend/entries` taking `SpendEntryRequest[]`, returning `SpendEntryResult[]` where `Status` is `created` / `duplicate` / `rejected`; `MapSpendEntriesEndpoints`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiObservatory.Api.Tests/SpendEntriesEndpointsWafTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

public class SpendEntriesEndpointsWafTests(AiObservatoryApiFactory factory)
    : IClassFixture<AiObservatoryApiFactory>
{
    /// <summary>Creates a category and a vendor and returns their ids.</summary>
    private static async Task<(Guid CategoryId, Guid VendorId)> SeedCatalogAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ct = TestContext.Current.CancellationToken;

        var cat = await client.PostAsJsonAsync("/api/spend/categories",
            new { Key = $"credits-{suffix}", DisplayName = "Credits", ColorVar = "--c", SortOrder = 1 }, ct);
        var categoryId = (await cat.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var ven = await client.PostAsJsonAsync("/api/spend/vendors",
            new { Key = $"anthropic-{suffix}", DisplayName = "Anthropic", Provider = "anthropic" }, ct);
        var vendorId = (await ven.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        return (categoryId, vendorId);
    }

    private static object Entry(Guid categoryId, Guid vendorId, string? entryKey, decimal amount = 80m,
        string currency = "GBP", string source = "Csv") =>
        new
        {
            OccurredOn = "2026-07-12",
            VendorId = vendorId,
            CategoryId = categoryId,
            Amount = amount,
            Currency = currency,
            Description = "Top-up",
            Source = source,
            EntryKey = entryKey,
        };

    /// <summary>
    /// The single most important test here. Re-posting an identical payload must land
    /// nothing and leave the total untouched — the failure this project has been burned by.
    /// </summary>
    [Fact]
    public async Task RePostingTheSamePayload_LandsNothingAndLeavesTheTotalUnchanged()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var key = $"k-{Guid.NewGuid():N}";
        var payload = new[] { Entry(categoryId, vendorId, key) };

        var first = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Single().GetProperty("status").GetString().Should().Be("created");

        var totalAfterFirst = await TotalAsync(client, vendorId);

        var second = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        (await second.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Single().GetProperty("status").GetString().Should().Be("duplicate");

        (await TotalAsync(client, vendorId)).Should().Be(totalAfterFirst, "a duplicate must not move the total");
    }

    [Fact]
    public async Task MixedBatch_ReturnsPerRowVerdictsAndLandsOnlyTheGoodRow()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var existingKey = $"k-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/api/spend/entries",
            new[] { Entry(categoryId, vendorId, existingKey) }, ct);

        var mixed = new object[]
        {
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}"),          // good
            Entry(categoryId, vendorId, existingKey),                       // duplicate
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", -5m),      // rejected: negative
        };

        var response = await client.PostAsJsonAsync("/api/spend/entries", mixed, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var statuses = (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToArray();

        statuses.Should().Equal("created", "duplicate", "rejected");
    }

    [Fact]
    public async Task ManualEntriesAreNeverDeduplicated()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var payload = new[] { Entry(categoryId, vendorId, entryKey: null, source: "Manual") };

        await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        var second = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);

        (await second.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Single().GetProperty("status").GetString()
            .Should().Be("created", "a person typing the same charge twice is a mistake to show, not silence");
    }

    [Fact]
    public async Task GbpEntryStoresRateOneAndTheSameGbpAmount()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        await client.PostAsJsonAsync("/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 80m, "GBP") }, ct);

        var entries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/entries?vendorId={vendorId}", ct);
        var entry = entries.EnumerateArray().Single();

        entry.GetProperty("fxRate").GetDecimal().Should().Be(1m);
        entry.GetProperty("amountGbp").GetDecimal().Should().Be(80m);
    }

    [Fact]
    public async Task UnknownVendorIsRejectedRatherThanCreated()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, _) = await SeedCatalogAsync(client);

        var response = await client.PostAsJsonAsync("/api/spend/entries",
            new[] { Entry(categoryId, Guid.NewGuid(), $"k-{Guid.NewGuid():N}") }, ct);

        (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Single().GetProperty("status").GetString().Should().Be("rejected");
    }

    private static async Task<decimal> TotalAsync(HttpClient client, Guid vendorId)
    {
        var entries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/entries?vendorId={vendorId}", TestContext.Current.CancellationToken);
        return entries.EnumerateArray().Sum(e => e.GetProperty("amountGbp").GetDecimal());
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```
dotnet test tests/AiObservatory.Api.Tests --filter SpendEntriesEndpointsWafTests
```
Expected: FAIL — 404.

- [ ] **Step 3: Implement**

Create `src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs`:

```csharp
using AiObservatory.Api.Services.Fx;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

/// <summary>
/// The ledger itself. <c>POST</c> always takes an array — the manual form posts an array of
/// one — which is what lets the form, CSV import and the tax-portal feed share a single
/// endpoint with one contract and one code path.
/// </summary>
public static class SpendEntriesEndpoints
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapSpendEntriesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/spend/entries", GetEntriesAsync);
        app.MapPost("/spend/entries", RecordEntriesAsync);
        app.MapPatch("/spend/entries/{id:guid}", PatchEntryAsync);
        app.MapDelete("/spend/entries/{id:guid}", DeleteEntryAsync);

        return app;
    }

    private const int MaxBatch = 1000;

    private static async Task<IResult> RecordEntriesAsync(
        SpendEntryRequest[] requests,
        AiObservatoryDbContext db,
        FxRateProvider fx,
        IClock clock,
        CancellationToken ct)
    {
        if (requests.Length == 0)
        {
            return Results.BadRequest("Provide at least one entry");
        }

        if (requests.Length > MaxBatch)
        {
            return Results.BadRequest($"At most {MaxBatch} entries per request");
        }

        // Loaded once rather than per row: a CSV import is overwhelmingly the same handful
        // of vendors and categories repeated.
        var vendorIds = await db.SpendVendors.AsNoTracking().Select(v => v.Id).ToHashSetAsync(ct);
        var categoryIds = await db.SpendCategories.AsNoTracking().Select(c => c.Id).ToHashSetAsync(ct);

        var results = new List<SpendEntryResult>(requests.Length);
        var now = clock.GetCurrentInstant();

        foreach (var req in requests)
        {
            var rejection = Validate(req, vendorIds, categoryIds, out var source, out var currency);
            if (rejection is not null)
            {
                results.Add(new SpendEntryResult(null, "rejected", rejection));
                continue;
            }

            var rate = await fx.GetGbpRateOnAsync(currency, req.OccurredOn, ct);

            var entry = new SpendEntry
            {
                OccurredOn = req.OccurredOn,
                VendorId = req.VendorId,
                CategoryId = req.CategoryId,
                Amount = req.Amount,
                Currency = currency,
                // Frozen here, deliberately. See SpendEntry.AmountGbp.
                AmountGbp = decimal.Round(req.Amount * rate, 4, MidpointRounding.ToEven),
                FxRate = rate,
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                Source = source,
                EntryKey = string.IsNullOrWhiteSpace(req.EntryKey) ? null : req.EntryKey.Trim(),
                RecordedAt = now,
            };

            db.SpendEntries.Add(entry);
            try
            {
                await db.SaveChangesAsync(ct);
                results.Add(new SpendEntryResult(entry.Id, "created", null));
            }
            catch (DbUpdateException ex) when (
                entry.EntryKey is not null
                && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The row already exists for this source and key. Report it rather than
                // failing the batch: re-importing an overlapping statement is routine.
                db.Entry(entry).State = EntityState.Detached;
                var existingId = await db.SpendEntries.AsNoTracking()
                    .Where(e => e.Source == entry.Source && e.EntryKey == entry.EntryKey)
                    .Select(e => (Guid?)e.Id)
                    .FirstOrDefaultAsync(ct);
                results.Add(new SpendEntryResult(existingId, "duplicate", null));
            }
        }

        return Results.Ok(results);
    }

    /// <summary>Returns a rejection reason, or null when the request is sound.</summary>
    private static string? Validate(
        SpendEntryRequest req,
        HashSet<Guid> vendorIds,
        HashSet<Guid> categoryIds,
        out SpendSource source,
        out string currency)
    {
        source = SpendSource.Manual;
        currency = "GBP";

        if (!vendorIds.Contains(req.VendorId))
        {
            return $"Unknown VendorId: {req.VendorId}";
        }

        if (!categoryIds.Contains(req.CategoryId))
        {
            return $"Unknown CategoryId: {req.CategoryId}";
        }

        if (req.Amount < 0)
        {
            return "Amount must be non-negative";
        }

        currency = (req.Currency ?? "GBP").Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            return $"Currency must be a 3-letter ISO 4217 code, got: {req.Currency}";
        }

        if (req.Description is { Length: > 200 })
        {
            return "Description must be 200 characters or fewer";
        }

        if (req.EntryKey is { Length: > 200 })
        {
            return "EntryKey must be 200 characters or fewer";
        }

        if (!Enum.TryParse(req.Source, ignoreCase: true, out source) || !Enum.IsDefined(source))
        {
            return $"Unknown source: {req.Source}";
        }

        return null;
    }

    private static async Task<IResult> GetEntriesAsync(
        AiObservatoryDbContext db,
        CancellationToken ct,
        LocalDate? from = null,
        LocalDate? to = null,
        Guid? vendorId = null,
        Guid? categoryId = null,
        int limit = 5000)
    {
        var q = db.SpendEntries.AsNoTracking();
        if (from is { } f) { q = q.Where(e => e.OccurredOn >= f); }
        if (to is { } t) { q = q.Where(e => e.OccurredOn <= t); }
        if (vendorId is { } v) { q = q.Where(e => e.VendorId == v); }
        if (categoryId is { } c) { q = q.Where(e => e.CategoryId == c); }

        // Hard ceiling so an unbounded range cannot OOM the response; callers page by date.
        var capped = Math.Clamp(limit, 1, 5000);

        var rows = await q
            .OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.RecordedAt)
            .Take(capped)
            .ToListAsync(ct);

        return Results.Ok(rows);
    }

    private static async Task<IResult> PatchEntryAsync(
        Guid id,
        SpendEntryPatchRequest req,
        AiObservatoryDbContext db,
        FxRateProvider fx,
        CancellationToken ct)
    {
        var entry = await db.SpendEntries.FindAsync([id], ct);
        if (entry is null)
        {
            return Results.NotFound();
        }

        if (req.Amount is { } amount)
        {
            if (amount < 0) { return Results.BadRequest("Amount must be non-negative"); }
            entry.Amount = amount;
        }

        if (req.OccurredOn is { } occurredOn) { entry.OccurredOn = occurredOn; }
        if (req.VendorId is { } vendorId) { entry.VendorId = vendorId; }
        if (req.CategoryId is { } categoryId) { entry.CategoryId = categoryId; }
        if (req.Description is { } description)
        {
            if (description.Length > 200) { return Results.BadRequest("Description must be 200 characters or fewer"); }
            entry.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }

        // Amount, currency or date changing all invalidate the stored conversion, so
        // re-resolve at the (possibly new) charge date rather than leave a stale GBP figure.
        if (req.Amount is not null || req.Currency is not null || req.OccurredOn is not null)
        {
            var currency = (req.Currency ?? entry.Currency).Trim().ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            {
                return Results.BadRequest($"Currency must be a 3-letter ISO 4217 code, got: {req.Currency}");
            }

            entry.Currency = currency;
            entry.FxRate = await fx.GetGbpRateOnAsync(currency, entry.OccurredOn, ct);
            entry.AmountGbp = decimal.Round(entry.Amount * entry.FxRate, 4, MidpointRounding.ToEven);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(entry);
    }

    private static async Task<IResult> DeleteEntryAsync(Guid id, AiObservatoryDbContext db, CancellationToken ct)
    {
        var deleted = await db.SpendEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }
}

public sealed record SpendEntryRequest(
    LocalDate OccurredOn,
    Guid VendorId,
    Guid CategoryId,
    decimal Amount,
    string? Currency,
    string? Description,
    string Source,
    string? EntryKey);

public sealed record SpendEntryPatchRequest(
    LocalDate? OccurredOn,
    Guid? VendorId,
    Guid? CategoryId,
    decimal? Amount,
    string? Currency,
    string? Description);

/// <param name="Status">created | duplicate | rejected</param>
public sealed record SpendEntryResult(Guid? Id, string Status, string? Reason);
```

- [ ] **Step 4: Register in Program.cs**

After `api.MapSpendCatalogEndpoints();`:

```csharp
api.MapSpendEntriesEndpoints();
```

- [ ] **Step 5: Run to verify they pass**

```
dotnet test tests/AiObservatory.Api.Tests --filter SpendEntriesEndpointsWafTests
```
Expected: PASS, 5 tests.

- [ ] **Step 6: Full backend gate**

```
dotnet build
dotnet test
```
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.Tests/SpendEntriesEndpointsWafTests.cs
git commit -m "feat: record spend entries via an array endpoint

POST always takes an array -- the manual form posts an array of one -- so the
form, CSV import and the tax-portal feed share one contract and one code path
rather than needing a separate batch route.

Per-row verdicts (created / duplicate / rejected) instead of all-or-nothing: a
200-row import with one bad date should land 199 rows and report the one. A
unique-violation on (Source, EntryKey) is reported as duplicate rather than
failing the batch, because re-importing an overlapping statement is routine.
Manual rows carry a null key and are never deduplicated.

The GBP amount and the rate are frozen at write from the charge date, and a
PATCH that moves the amount, currency or date re-resolves both rather than
leaving a stale conversion behind."
```

---

### Task 6: Frontend API client and hooks

**Files:**
- Modify: `src/AiObservatory.Web/src/api/client.ts`, `src/AiObservatory.Web/src/api/queries.ts`
- Create: `src/AiObservatory.Web/src/lib/spendFilters.ts`
- Test: `src/AiObservatory.Web/src/lib/spendFilters.test.ts` (create)

**Interfaces:**
- Consumes: the endpoints from Tasks 4 and 5.
- Produces: types `SpendCategory`, `SpendVendor`, `SpendEntry`, `SpendEntryResult`; functions `getSpendCategories`, `getSpendVendors`, `getSpendEntries`, `postSpendEntries`, `deleteSpendEntry`; hooks `useSpendCategories`, `useSpendVendors`, `useSpendEntries`; pure helpers `filterEntries`, `totalGbp`.

- [ ] **Step 1: Write the failing tests for the pure helpers**

Create `src/AiObservatory.Web/src/lib/spendFilters.test.ts`:

```ts
import { describe, it, expect } from 'vitest'
import { filterEntries, totalGbp } from './spendFilters'
import type { SpendEntry } from '../api/client'

function entry(over: Partial<SpendEntry> = {}): SpendEntry {
  return {
    id: crypto.randomUUID(),
    occurredOn: '2026-07-12',
    vendorId: 'v1',
    categoryId: 'c1',
    amount: 80,
    currency: 'GBP',
    amountGbp: 80,
    fxRate: 1,
    description: 'Top-up',
    source: 'Csv',
    entryKey: 'k1',
    recordedAt: '2026-07-12T00:00:00Z',
    ...over,
  }
}

describe('filterEntries', () => {
  it('returns everything when no filter is set', () => {
    const rows = [entry(), entry({ categoryId: 'c2' })]
    expect(filterEntries(rows, {})).toHaveLength(2)
  })

  it('filters by category', () => {
    const rows = [entry({ categoryId: 'c1' }), entry({ categoryId: 'c2' })]
    expect(filterEntries(rows, { categoryId: 'c2' })).toHaveLength(1)
  })

  it('filters by vendor', () => {
    const rows = [entry({ vendorId: 'v1' }), entry({ vendorId: 'v2' })]
    expect(filterEntries(rows, { vendorId: 'v1' })).toHaveLength(1)
  })

  it('excludes categories switched off, so the total follows the legend', () => {
    const rows = [entry({ categoryId: 'c1' }), entry({ categoryId: 'c2' })]
    expect(filterEntries(rows, { excludedCategoryIds: ['c1'] })).toHaveLength(1)
  })

  it('combines filters', () => {
    const rows = [
      entry({ vendorId: 'v1', categoryId: 'c1' }),
      entry({ vendorId: 'v1', categoryId: 'c2' }),
      entry({ vendorId: 'v2', categoryId: 'c1' }),
    ]
    expect(filterEntries(rows, { vendorId: 'v1', categoryId: 'c1' })).toHaveLength(1)
  })
})

describe('totalGbp', () => {
  it('sums the GBP column, not the native amount', () => {
    const rows = [entry({ amount: 100, amountGbp: 74 }), entry({ amount: 10, amountGbp: 10 })]
    expect(totalGbp(rows)).toBe(84)
  })

  it('is zero for no rows', () => {
    expect(totalGbp([])).toBe(0)
  })

  it('reflects the filter, so the headline is the total of what is on screen', () => {
    const rows = [entry({ categoryId: 'c1', amountGbp: 50 }), entry({ categoryId: 'c2', amountGbp: 25 })]
    expect(totalGbp(filterEntries(rows, { categoryId: 'c1' }))).toBe(50)
  })
})
```

- [ ] **Step 2: Run to verify they fail**

```
cd src/AiObservatory.Web && npx vitest run src/lib/spendFilters.test.ts
```
Expected: FAIL — module not found.

- [ ] **Step 3: Add the client types and functions**

Append to `src/AiObservatory.Web/src/api/client.ts`:

```ts
export interface SpendCategory {
  id: string
  key: string
  displayName: string
  colorVar: string
  sortOrder: number
  archivedAt: string | null
}

export interface SpendVendor {
  id: string
  key: string
  displayName: string
  provider: string | null      // null for vendors with no token estimate
  defaultCategoryId: string | null
  archivedAt: string | null
}

export interface SpendEntry {
  id: string
  occurredOn: string           // ISO date yyyy-MM-dd
  vendorId: string
  categoryId: string
  amount: number               // as charged
  currency: string
  amountGbp: number            // frozen at write — sum this, never `amount`
  fxRate: number
  description: string | null
  source: 'Manual' | 'Csv' | 'Portal'
  entryKey: string | null
  recordedAt: string
}

export interface SpendEntryResult {
  id: string | null
  status: 'created' | 'duplicate' | 'rejected'
  reason: string | null
}

export interface NewSpendEntry {
  occurredOn: string
  vendorId: string
  categoryId: string
  amount: number
  currency: string
  description: string | null
  source: 'Manual' | 'Csv' | 'Portal'
  entryKey: string | null
}

export async function getSpendCategories(): Promise<SpendCategory[]> {
  return (await request('/spend/categories')).json()
}

export async function getSpendVendors(): Promise<SpendVendor[]> {
  return (await request('/spend/vendors')).json()
}

export async function getSpendEntries(from: string, to: string): Promise<SpendEntry[]> {
  return (await request(`/spend/entries?from=${from}&to=${to}`)).json()
}

/** Always an array — the manual form sends one. */
export async function postSpendEntries(entries: NewSpendEntry[]): Promise<SpendEntryResult[]> {
  const res = await request('/spend/entries', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(entries),
  })
  return res.json()
}

export async function deleteSpendEntry(id: string): Promise<void> {
  await request(`/spend/entries/${id}`, { method: 'DELETE' })
}
```

- [ ] **Step 4: Add the hooks**

Append to `src/AiObservatory.Web/src/api/queries.ts` (and extend its existing import from `./client` with the new functions and types):

```ts
export function useSpendCategories(): SpendCategory[] {
  const { data = [] } = useQuery({ queryKey: ['spend-categories'], queryFn: getSpendCategories })
  return data
}

export function useSpendVendors(): SpendVendor[] {
  const { data = [] } = useQuery({ queryKey: ['spend-vendors'], queryFn: getSpendVendors })
  return data
}

export function useSpendEntries(from: Date, to: Date): {
  entries: SpendEntry[]
  isLoading: boolean
  isError: boolean
} {
  const { data = [], isPending, isError } = useQuery({
    queryKey: ['spend-entries', localDate(from), localDate(to)],
    queryFn: () => getSpendEntries(localDate(from), localDate(to)),
  })
  return { entries: data, isLoading: isPending, isError }
}
```

- [ ] **Step 5: Add the pure helpers**

Create `src/AiObservatory.Web/src/lib/spendFilters.ts`:

```ts
import type { SpendEntry } from '../api/client'

export interface SpendFilter {
  vendorId?: string
  categoryId?: string
  /** Categories switched off in the legend. The headline total follows this. */
  excludedCategoryIds?: string[]
}

/** Pure — one filter state drives the totals and the table alike. */
export function filterEntries(entries: SpendEntry[], filter: SpendFilter): SpendEntry[] {
  const excluded = new Set(filter.excludedCategoryIds ?? [])
  return entries.filter(e =>
    (filter.vendorId == null || e.vendorId === filter.vendorId) &&
    (filter.categoryId == null || e.categoryId === filter.categoryId) &&
    !excluded.has(e.categoryId))
}

/**
 * Sums the GBP column, never `amount`. `amountGbp` was converted at the charge date and
 * frozen; re-converting here would make historical totals drift with the exchange rate.
 */
export function totalGbp(entries: SpendEntry[]): number {
  return entries.reduce((sum, e) => sum + e.amountGbp, 0)
}
```

- [ ] **Step 6: Run to verify they pass**

```
cd src/AiObservatory.Web && npx vitest run src/lib/spendFilters.test.ts
```
Expected: PASS, 8 tests.

- [ ] **Step 7: Typecheck and lint**

```
cd src/AiObservatory.Web && npx tsc -b --noEmit && npx eslint .
```
Expected: both clean.

- [ ] **Step 8: Commit**

```bash
git add src/AiObservatory.Web/src/api src/AiObservatory.Web/src/lib/spendFilters.ts src/AiObservatory.Web/src/lib/spendFilters.test.ts
git commit -m "feat: add spend API client, hooks and filter helpers

filterEntries and totalGbp are pure and separately tested, so the rule that the
headline is the total of whatever is on screen is verifiable without rendering
anything.

totalGbp sums amountGbp rather than amount: that column was converted at the
charge date and frozen, and re-converting in the browser would make historical
totals drift with the exchange rate."
```

---

### Task 7: The Spend page — filter bar, totals, ledger table

**Files:**
- Create: `src/AiObservatory.Web/src/pages/SpendPage.tsx`, `src/AiObservatory.Web/src/components/SpendFilterBar.tsx`, `SpendTotals.tsx`, `SpendLedgerTable.tsx`
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx:19-27,145-147`
- Test: `src/AiObservatory.Web/src/components/SpendTotals.test.tsx` (create)

**Interfaces:**
- Consumes: `useSpendEntries`, `useSpendCategories`, `useSpendVendors`, `filterEntries`, `totalGbp` (Task 6).
- Produces: the `spend` dashboard tab.

- [ ] **Step 1: Write the failing test**

Create `src/AiObservatory.Web/src/components/SpendTotals.test.tsx`:

```tsx
import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import SpendTotals from './SpendTotals'

describe('SpendTotals', () => {
  it('shows the filtered total in GBP', () => {
    render(<SpendTotals total={412.8} entryCount={14} largestCategory="Subscriptions" />)
    expect(screen.getByText('£412.80')).toBeInTheDocument()
  })

  it('shows the entry count', () => {
    render(<SpendTotals total={412.8} entryCount={14} largestCategory="Subscriptions" />)
    expect(screen.getByText('14')).toBeInTheDocument()
  })

  it('renders a dash rather than a category name when nothing is in range', () => {
    render(<SpendTotals total={0} entryCount={0} largestCategory={null} />)
    expect(screen.getByText('£0.00')).toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Run to verify it fails**

```
cd src/AiObservatory.Web && npx vitest run src/components/SpendTotals.test.tsx
```
Expected: FAIL — module not found.

- [ ] **Step 3: Implement `SpendTotals`**

Create `src/AiObservatory.Web/src/components/SpendTotals.tsx`:

```tsx
import { gbp } from '../lib/currency'

interface Props {
  total: number
  entryCount: number
  largestCategory: string | null
}

/**
 * Region 2. Every figure here is of the CURRENT filter, not the calendar month —
 * that is what makes "the filtered aggregate" unambiguous.
 */
export default function SpendTotals({ total, entryCount, largestCategory }: Props) {
  return (
    <div className="spend-totals">
      <div className="spend-totals__card">
        <span className="spend-totals__label">Filtered total</span>
        <span className="spend-totals__value">{gbp(total)}</span>
      </div>
      <div className="spend-totals__card">
        <span className="spend-totals__label">Entries</span>
        <span className="spend-totals__value">{entryCount}</span>
      </div>
      <div className="spend-totals__card">
        <span className="spend-totals__label">Largest category</span>
        <span className="spend-totals__value">{largestCategory ?? '—'}</span>
      </div>
    </div>
  )
}
```

- [ ] **Step 4: Implement the filter bar**

Create `src/AiObservatory.Web/src/components/SpendFilterBar.tsx`:

```tsx
import type { SpendCategory, SpendVendor } from '../api/client'

interface Props {
  categories: SpendCategory[]
  vendors: SpendVendor[]
  categoryId?: string
  vendorId?: string
  onCategoryChange: (id: string | undefined) => void
  onVendorChange: (id: string | undefined) => void
  onAddEntry: () => void
  canEdit: boolean
}

/** Region 1. One filter state, lifted to SpendPage, drives every other region. */
export default function SpendFilterBar({
  categories, vendors, categoryId, vendorId,
  onCategoryChange, onVendorChange, onAddEntry, canEdit,
}: Props) {
  return (
    <div className="spend-filters">
      <label className="spend-filters__field">
        <span>Category</span>
        <select
          value={categoryId ?? ''}
          onChange={e => onCategoryChange(e.target.value || undefined)}
        >
          <option value="">All categories</option>
          {categories.map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}
        </select>
      </label>

      <label className="spend-filters__field">
        <span>Vendor</span>
        <select
          value={vendorId ?? ''}
          onChange={e => onVendorChange(e.target.value || undefined)}
        >
          <option value="">All vendors</option>
          {vendors.map(v => <option key={v.id} value={v.id}>{v.displayName}</option>)}
        </select>
      </label>

      {canEdit && (
        <button type="button" className="spend-filters__add" onClick={onAddEntry}>
          Add entry
        </button>
      )}
    </div>
  )
}
```

- [ ] **Step 5: Implement the ledger table**

Create `src/AiObservatory.Web/src/components/SpendLedgerTable.tsx`:

```tsx
import { useState, useMemo } from 'react'
import type { SpendCategory, SpendEntry, SpendVendor } from '../api/client'
import { gbp, formatCurrency } from '../lib/currency'

type SortKey = 'occurredOn' | 'vendor' | 'category' | 'amountGbp'

interface Props {
  entries: SpendEntry[]
  categories: SpendCategory[]
  vendors: SpendVendor[]
  onDelete: (id: string) => void
  canEdit: boolean
}

/** Region 6. Sortable on any column; the rows are already filtered by SpendPage. */
export default function SpendLedgerTable({ entries, categories, vendors, onDelete, canEdit }: Props) {
  const [sortKey, setSortKey] = useState<SortKey>('occurredOn')
  const [ascending, setAscending] = useState(false)

  const categoryName = useMemo(
    () => new Map(categories.map(c => [c.id, c.displayName])), [categories])
  const vendorName = useMemo(
    () => new Map(vendors.map(v => [v.id, v.displayName])), [vendors])

  const sorted = useMemo(() => {
    const value = (e: SpendEntry): string | number => {
      if (sortKey === 'vendor') return vendorName.get(e.vendorId) ?? ''
      if (sortKey === 'category') return categoryName.get(e.categoryId) ?? ''
      if (sortKey === 'amountGbp') return e.amountGbp
      return e.occurredOn
    }
    return [...entries].sort((a, b) => {
      const av = value(a), bv = value(b)
      const cmp = typeof av === 'number' && typeof bv === 'number'
        ? av - bv
        : String(av).localeCompare(String(bv))
      return ascending ? cmp : -cmp
    })
  }, [entries, sortKey, ascending, categoryName, vendorName])

  const toggle = (key: SortKey) => {
    if (key === sortKey) {
      setAscending(!ascending)
    } else {
      setSortKey(key)
      setAscending(false)
    }
  }

  if (entries.length === 0) {
    return <p className="spend-ledger__empty">No spend recorded for this filter.</p>
  }

  return (
    <table className="spend-ledger">
      <thead>
        <tr>
          <th><button type="button" onClick={() => toggle('occurredOn')}>Date</button></th>
          <th><button type="button" onClick={() => toggle('vendor')}>Vendor</button></th>
          <th><button type="button" onClick={() => toggle('category')}>Category</button></th>
          <th>Description</th>
          <th className="spend-ledger__num">
            <button type="button" onClick={() => toggle('amountGbp')}>Amount</button>
          </th>
          <th>Source</th>
          {canEdit && <th><span className="visually-hidden">Actions</span></th>}
        </tr>
      </thead>
      <tbody>
        {sorted.map(e => (
          <tr key={e.id}>
            <td>{e.occurredOn}</td>
            <td>{vendorName.get(e.vendorId) ?? '—'}</td>
            <td>{categoryName.get(e.categoryId) ?? '—'}</td>
            <td>{e.description ?? ''}</td>
            <td className="spend-ledger__num">
              {gbp(e.amountGbp)}
              {e.currency !== 'GBP' && (
                <span className="spend-ledger__native"> ({formatCurrency(e.amount, e.currency)})</span>
              )}
            </td>
            <td>{e.source.toLowerCase()}</td>
            {canEdit && (
              <td>
                <button type="button" onClick={() => onDelete(e.id)} aria-label={`Delete entry from ${e.occurredOn}`}>
                  Delete
                </button>
              </td>
            )}
          </tr>
        ))}
      </tbody>
    </table>
  )
}
```

- [ ] **Step 6: Implement the page**

Create `src/AiObservatory.Web/src/pages/SpendPage.tsx`:

```tsx
import { useState, useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import SpendFilterBar from '../components/SpendFilterBar'
import SpendTotals from '../components/SpendTotals'
import SpendLedgerTable from '../components/SpendLedgerTable'
import SpendEntryModal from '../components/SpendEntryModal'
import { useSpendCategories, useSpendVendors, useSpendEntries } from '../api/queries'
import { deleteSpendEntry } from '../api/client'
import { filterEntries, totalGbp } from '../lib/spendFilters'
import { isReadonly } from '../auth/msal'

const RANGE_DAYS = 90

export default function SpendPage() {
  const qc = useQueryClient()
  const [categoryId, setCategoryId] = useState<string | undefined>()
  const [vendorId, setVendorId] = useState<string | undefined>()
  const [adding, setAdding] = useState(false)

  // Fixed 90-day window in phase 1; the configurable date range arrives with the
  // charts in phase 2, where it earns its keep.
  const [to] = useState(() => new Date())
  const from = useMemo(() => new Date(to.getTime() - RANGE_DAYS * 86_400_000), [to])

  const categories = useSpendCategories()
  const vendors = useSpendVendors()
  const { entries, isLoading, isError } = useSpendEntries(from, to)

  const visible = useMemo(
    () => filterEntries(entries, { categoryId, vendorId }),
    [entries, categoryId, vendorId])

  const total = useMemo(() => totalGbp(visible), [visible])

  const largestCategory = useMemo(() => {
    if (visible.length === 0) return null
    const byCategory = new Map<string, number>()
    for (const e of visible) {
      byCategory.set(e.categoryId, (byCategory.get(e.categoryId) ?? 0) + e.amountGbp)
    }
    const [topId] = [...byCategory.entries()].sort((a, b) => b[1] - a[1])[0]
    return categories.find(c => c.id === topId)?.displayName ?? null
  }, [visible, categories])

  const remove = useMutation({
    mutationFn: deleteSpendEntry,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['spend-entries'] }),
    onError: (err: Error) => alert(`Failed to delete entry: ${err.message}`),
  })

  if (isError) {
    return <div className="error-banner">Couldn’t load spend. Check the API service and try refreshing.</div>
  }

  return (
    <section className="spend-page">
      <SpendFilterBar
        categories={categories}
        vendors={vendors}
        categoryId={categoryId}
        vendorId={vendorId}
        onCategoryChange={setCategoryId}
        onVendorChange={setVendorId}
        onAddEntry={() => setAdding(true)}
        canEdit={!isReadonly}
      />

      <SpendTotals total={total} entryCount={visible.length} largestCategory={largestCategory} />

      {isLoading
        ? <p>Loading spend…</p>
        : <SpendLedgerTable
            entries={visible}
            categories={categories}
            vendors={vendors}
            onDelete={id => remove.mutate(id)}
            canEdit={!isReadonly}
          />}

      {adding && (
        <SpendEntryModal
          categories={categories}
          vendors={vendors}
          onClose={() => setAdding(false)}
        />
      )}
    </section>
  )
}
```

- [ ] **Step 7: Register the tab**

In `src/AiObservatory.Web/src/pages/Dashboard.tsx`, extend the union (line 19) and the `TABS` array:

```tsx
type DashboardTab = 'overview' | 'adversarial-review' | 'reporting' | 'activity' | 'github' | 'spend'
```

```tsx
  { id: 'spend', label: 'Spend', readonlyHidden: true },
```

Import it alongside the other pages and render it with the rest (~line 147):

```tsx
import SpendPage from './SpendPage'
```
```tsx
        {tab === 'spend' && <SpendPage />}
```

`readonlyHidden: true` matches Activity and GitHub: the share link is for showing token usage, not household spend.

- [ ] **Step 8: Run the test to verify it passes**

```
cd src/AiObservatory.Web && npx vitest run src/components/SpendTotals.test.tsx
```
Expected: PASS, 3 tests. (`SpendEntryModal` arrives in Task 8; create it as a stub returning `null` if the import fails to resolve, then replace it there.)

- [ ] **Step 9: Commit**

```bash
git add src/AiObservatory.Web/src/pages src/AiObservatory.Web/src/components/Spend*.tsx src/AiObservatory.Web/src/components/SpendTotals.test.tsx
git commit -m "feat: add the Spend page with filters, totals and the ledger table

Regions 1, 2 and 6 of the approved layout. Filter state is lifted to the page so
one state drives both the totals and the table -- the headline is always the
total of what is on screen, never the calendar month.

The tab is readonlyHidden, matching Activity and GitHub: the share link exists to
show token usage, not household spend."
```

---

### Task 8: Manual entry form

**Files:**
- Create: `src/AiObservatory.Web/src/components/SpendEntryModal.tsx`
- Test: `src/AiObservatory.Web/src/components/SpendEntryModal.test.tsx` (create)

**Interfaces:**
- Consumes: `postSpendEntries`, `NewSpendEntry` (Task 6); `SpendCategory`, `SpendVendor`.
- Produces: `SpendEntryModal({ categories, vendors, onClose })`.

- [ ] **Step 1: Write the failing tests**

Create `src/AiObservatory.Web/src/components/SpendEntryModal.test.tsx`:

```tsx
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import SpendEntryModal from './SpendEntryModal'
import * as client from '../api/client'

const categories = [{ id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '--c', sortOrder: 1, archivedAt: null }]
const vendors = [{ id: 'v1', key: 'anthropic', displayName: 'Anthropic', provider: 'anthropic', defaultCategoryId: 'c1', archivedAt: null }]

function renderModal(onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <SpendEntryModal categories={categories} vendors={vendors} onClose={onClose} />
    </QueryClientProvider>,
  )
}

describe('SpendEntryModal', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('posts an array of one, with source Manual and no entry key', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
    const [payload] = post.mock.calls[0]
    expect(payload).toHaveLength(1)
    expect(payload[0].source).toBe('Manual')
    expect(payload[0].entryKey).toBeNull()
  })

  it('refuses to submit a negative amount', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '-5' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())
    expect(post).not.toHaveBeenCalled()
  })

  it('surfaces a rejected verdict instead of closing', async () => {
    vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: null, status: 'rejected', reason: 'Unknown VendorId' }])
    const onClose = vi.fn()
    renderModal(onClose)

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Unknown VendorId'))
    expect(onClose).not.toHaveBeenCalled()
  })
})
```

- [ ] **Step 2: Run to verify they fail**

```
cd src/AiObservatory.Web && npx vitest run src/components/SpendEntryModal.test.tsx
```
Expected: FAIL — module not found (or the stub renders nothing).

- [ ] **Step 3: Implement**

Create `src/AiObservatory.Web/src/components/SpendEntryModal.tsx`:

```tsx
import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { postSpendEntries, type NewSpendEntry, type SpendCategory, type SpendVendor } from '../api/client'
import { localDate } from '../api/queries'

interface Props {
  categories: SpendCategory[]
  vendors: SpendVendor[]
  onClose: () => void
}

export default function SpendEntryModal({ categories, vendors, onClose }: Props) {
  const qc = useQueryClient()
  const [occurredOn, setOccurredOn] = useState(() => localDate(new Date()))
  const [vendorId, setVendorId] = useState(vendors[0]?.id ?? '')
  const [categoryId, setCategoryId] = useState(
    vendors[0]?.defaultCategoryId ?? categories[0]?.id ?? '')
  const [amount, setAmount] = useState('')
  const [currency, setCurrency] = useState('GBP')
  const [description, setDescription] = useState('')
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: (entry: NewSpendEntry) => postSpendEntries([entry]),
    onSuccess: results => {
      const result = results[0]
      // A per-row verdict is not an HTTP failure, so it has to be read rather than assumed.
      if (result?.status === 'rejected') {
        setError(result.reason ?? 'Entry rejected')
        return
      }
      qc.invalidateQueries({ queryKey: ['spend-entries'] })
      onClose()
    },
    onError: (err: Error) => setError(err.message),
  })

  const onVendorChange = (id: string) => {
    setVendorId(id)
    // Follow the vendor's default category, but only as a starting point.
    const preferred = vendors.find(v => v.id === id)?.defaultCategoryId
    if (preferred) setCategoryId(preferred)
  }

  const submit = () => {
    setError(null)
    const parsed = Number(amount)
    if (!Number.isFinite(parsed) || parsed < 0) {
      setError('Amount must be a non-negative number')
      return
    }
    if (!vendorId || !categoryId) {
      setError('Pick a vendor and a category')
      return
    }

    save.mutate({
      occurredOn,
      vendorId,
      categoryId,
      amount: parsed,
      currency,
      description: description.trim() || null,
      source: 'Manual',
      // Manual rows are deliberately un-keyed: a person entering the same charge twice
      // should see two rows and notice, not have the second silently swallowed.
      entryKey: null,
    })
  }

  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label="Add spend entry">
      <div className="modal">
        <h2>Add spend entry</h2>

        {error && <p role="alert" className="modal__error">{error}</p>}

        <label>Date
          <input type="date" value={occurredOn} onChange={e => setOccurredOn(e.target.value)} />
        </label>

        <label>Vendor
          <select value={vendorId} onChange={e => onVendorChange(e.target.value)}>
            {vendors.map(v => <option key={v.id} value={v.id}>{v.displayName}</option>)}
          </select>
        </label>

        <label>Category
          <select value={categoryId} onChange={e => setCategoryId(e.target.value)}>
            {categories.map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}
          </select>
        </label>

        <label>Amount
          <input type="number" step="0.01" min="0" value={amount} onChange={e => setAmount(e.target.value)} />
        </label>

        <label>Currency
          <select value={currency} onChange={e => setCurrency(e.target.value)}>
            <option value="GBP">GBP</option>
            <option value="USD">USD</option>
          </select>
        </label>

        <label>Description
          <input type="text" maxLength={200} value={description} onChange={e => setDescription(e.target.value)} />
        </label>

        <div className="modal__actions">
          <button type="button" onClick={onClose}>Cancel</button>
          <button type="button" onClick={submit} disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  )
}
```

- [ ] **Step 4: Run to verify they pass**

```
cd src/AiObservatory.Web && npx vitest run src/components/SpendEntryModal.test.tsx
```
Expected: PASS, 3 tests.

- [ ] **Step 5: Full pre-push gate**

```
cd src/AiObservatory.Web && npx tsc -b --noEmit
cd src/AiObservatory.Web && npx eslint .
cd src/AiObservatory.Web && npx vitest run
cd src/AiObservatory.Web && npx vite build
```
Then from the repo root:
```
dotnet build
dotnet test
```
All six must pass. `vite build` is included because `SpendPage` is newly SSR-rendered and jsdom masks SSR errors.

- [ ] **Step 6: Commit**

```bash
git add src/AiObservatory.Web/src/components/SpendEntryModal.tsx src/AiObservatory.Web/src/components/SpendEntryModal.test.tsx
git commit -m "feat: add the manual spend entry form

Posts an array of one to the shared endpoint rather than needing a route of its
own. Reads the per-row verdict from the response instead of treating HTTP 200 as
success, so a rejected row shows its reason and keeps the form open.

Manual entries send a null entry key by design: a person entering the same
charge twice should see two rows and notice it, not have the second silently
swallowed by deduplication meant for re-imported files."
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §5 `SpendCategory`, `SpendVendor`, `SpendEntry` | 1 |
| §5 unique index `(Source, EntryKey)`, manual rows null | 1, 5 |
| §5 `AmountGbp`/`FxRate` frozen at write | 2, 5 |
| §6 endpoints, array POST, per-row verdicts | 4, 5 |
| §6 `EntryKey` derivation with occurrence index | 3 |
| §6 dated FX, GBP short-circuit, indefinite cache | 2 |
| §3 privacy boundary enforced by test | 1 |
| §7 regions 1, 2, 6 | 7 |
| §7 manual entry form | 8 |
| §8 failure modes (unknown ids, bad amount, duplicate, FX outage, readonly) | 4, 5, 8 |
| §8 "archived" (soft-archived, still resolves for history) | 4 for the API; the frontend did not consume `includeArchived` until the final whole-branch fix wave, not task 7 as originally claimed here — see the amendment note below |
| §9 double-count guard, mixed batch, key derivation, dated FX, architecture | 1, 2, 3, 5, 6 |
| §9 "frontend filter" | claimed against task 6, but task 6 only unit-tests the pure `filterEntries`/`totalGbp` functions; no test ever asserted that a filter change moves the totals and the row count together in the rendered page. Corrected here rather than deleted — that integration test remains unwritten as of the final fix wave. |

Phase-2 and phase-3 items (time series, breakdowns, CSV import, variance) are correctly absent.

**Amendment (final whole-branch fix wave, base `bd6efa5`):** this self-review table, written when Task 8 completed, overclaimed two rows above. The whole-branch review after all eight tasks landed found one cross-task seam no per-task review could see: Task 4 built and tested `?includeArchived=true`, but no later task ever consumed it on the frontend, so archiving a category or vendor made every historical row referencing it render an em-dash instead of its recorded name. That finding, plus three others, went to the human partner. Their four rulings:

1. Archived resolution — split the query hooks: a live-only hook for pickers (`SpendFilterBar`, `SpendEntryModal`), a second `includeArchived=true` hook for the ledger table's name maps and `SpendPage`'s `largestCategory`. Fixed in the wave, with a test.
2. The manual-entry date input was unbounded against a fixed 90-day visible window with no picker until phase 2 — bound it with `min`/`max` (and an explicit range check, since the modal's `noValidate` bypasses native min/max enforcement).
3. The catalog ships with no seed data, so the entry form's selects are empty on first deploy — seeded the categories and vendors spec §2 names, via `HasData` with fixed ids. A follow-on catalog-management panel is new scope, tracked as its own task with its own review, not folded into this fix wave.
4. Spec §3 said a read-only share-link holder can see spend figures; the shipped tab is `readonlyHidden: true`, matching Activity and GitHub. Ruling: keep the tab hidden and correct the spec instead (done in `2026-07-26-billed-spend-ledger-design.md` §3).

Six deviations from this plan's literal text were approved over the branch's life (each recorded in-line at its own task above as either a "controller-mandated deviation" or a "correction to the plan"; summarised here for a reader who only reads this table). Three at Task 5: the array POST catches `FxUnavailableException` per row and reports it `rejected` rather than failing the whole batch (a direct consequence of Task 2's ruling, folded into Task 5's own implementation rather than the plan's endpoint sketch); `PATCH` validates a changed `VendorId`/`CategoryId` against the database before saving, which the plan's endpoint sketch did not call out; and the `from`/`to` query parameters bind as `string?` and are parsed manually rather than as `LocalDate?`, matching `AggregatesEndpoints`'s existing convention, because minimal-API query binding cannot construct a NodaTime type directly. Two at Task 6, both verified by the reviewer against the actual C#: `source` serialises lower-case (`'manual'`/`'csv'`/`'portal'`) rather than the plan's PascalCase, because the API's global `JsonStringEnumConverter(CamelCase)` governs the wire format and EF's `HasConversion<string>` governs only the column; and `filterEntries` is generic (`filterEntries<T extends SpendRowShape>`) rather than importing `SpendEntry` from `src/api` as the plan showed, because this repo's own `architecture.spec.ts` `lib -> api` rule forbids that edge. One at Task 7: it reused the existing generic `GitHubSortableHeader` component for the ledger's sortable columns instead of the plan's bespoke inline sort buttons, after the reviewer confirmed the component's generic signature and `aria-sort` behaviour genuinely fit.

Two further review findings, though not deviations from the plan's *text*, changed the shipped *design* enough to be worth a reader's attention alongside the six above: Task 1 added explicit foreign keys with `DeleteBehavior.Restrict` on `SpendEntry.VendorId`/`CategoryId` and `SpendVendor.DefaultCategoryId`, which the plan's prose implied ("a retired category must still resolve for historical rows") but never specified as schema; and Task 2's FX outage fallback narrowed to USD only — every other non-GBP, non-USD currency throws `FxUnavailableException` instead of silently freezing an undetectably wrong rate. Both went to the human partner as plan-mandated findings and were ruled on, same as the four final-review rulings above.

**Task 9, the spend catalog panel — shipped, but not described anywhere above.** It is new scope, not planned work, so this plan's body contains no brief for it and a reader looking for one will not find it. It was approved alongside ruling 3 above: the question that produced that ruling asked how the catalog should get populated, and the human partner chose both the seed migration (ruling 3 as recorded) and a management panel, the panel being the half that could not ride in the fix wave. What shipped: a "Manage catalog" button in `SpendFilterBar`, gated on the same `canEdit` as "Add entry", opening a tabbed `SpendCatalogModal` over `SpendCategoryCatalog` and `SpendVendorCatalog`. Each lists its rows *including archived ones* — a management view that hid them would make un-archiving impossible — and supports create, rename, and archive/un-archive against the six endpoints Task 4 had already built and tested but which nothing in the UI called. `Key` is deliberately not editable anywhere: imports and the portal feed reference it, which is the whole reason a separate slug exists, and it is enforced structurally by there being no input for it rather than by convention. It went through the same task cycle as Tasks 1-8 — implementer, review, one fix round, scoped re-review — and its review caught a defect in shared code that its own 29 passing tests had masked: `request()` in `client.ts` read error bodies with `res.text()`, but `Results.BadRequest(string)` and `Results.Conflict(string)` serialise as JSON, so the message reached users wrapped in literal quote characters, and it had regressed four pre-existing callers sharing that helper. Phase 2 should treat the panel as existing, shipped surface area, not as a gap to fill.

**Deferred to phase 2, flagged so it is not mistaken for an omission:** the configurable date range. Phase 1 uses a fixed 90-day window (`SpendPage.RANGE_DAYS`); the picker arrives with the charts, where a changeable period actually earns its keep. `useSpendEntries` already takes `from`/`to`, so this is a UI change only.

**Type consistency:** `SpendEntryResult.Status` is the lower-case string `created`/`duplicate`/`rejected` in both the C# record (Task 5) and the TS type (Task 6). `SpendSource` serialises as the lower-case `manual`/`csv`/`portal` and the TS union matches — **this paragraph originally claimed `Manual`/`Csv`/`Portal`, which was wrong.** The `HasConversion<string>` in Task 1 governs only the database column; the API's global `JsonStringEnumConverter(CamelCase)` governs the wire format, so the JSON is lower-case. Task 6's review verified this against the actual C# and corrected the TypeScript, and `Enum.TryParse(ignoreCase: true)` on the way in means either casing is accepted by the server — which is exactly why the mismatch could have survived unnoticed. `GetGbpRateOnAsync` has the same signature in Tasks 2 and 5. `filterEntries`/`totalGbp` signatures match between Tasks 6 and 7.

**Known ordering note:** Task 7 imports `SpendEntryModal`, which Task 8 creates. If executing strictly in order, create it as a one-line stub (`export default function SpendEntryModal() { return null }`) during Task 7 and replace it in Task 8 — Task 8's step 3 overwrites the file wholesale.
