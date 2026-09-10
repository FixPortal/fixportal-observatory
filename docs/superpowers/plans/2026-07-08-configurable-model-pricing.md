# Configurable model pricing for Anthropic/OpenAI ingest Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the hardcoded per-1M-token pricing tables in `AnthropicUsageClient` and `OpenAiUsageClient` out of code and into `appsettings.json`-backed configuration, folding the Anthropic Sonnet-5 intro-pricing window into a dated config entry instead of a special-cased `if` branch.

**Architecture:** New `IOptions<T>`-bound options classes (`AnthropicPricingOptions`, `OpenAiPricingOptions`) replace the `private static readonly Dictionary<...>` pricing tables. `ComputeCost` in each client resolves a `PricingEntry` from the injected options by longest-prefix match, date-range filter, and a dated-entry tie-break, falling back to a configured `FallbackPricing` rate on no match. `Program.cs` registers both options types unconditionally with `ValidateOnStart()`. A new `src/AiObservatory.Ingest/appsettings.json` carries today's rate values as config.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Options` + `Microsoft.Extensions.Options.DataAnnotations`, NodaTime (`LocalDate`), xUnit v3 + AwesomeAssertions.

## Global Constraints

- Provider-specific option schemas (Anthropic 4-field rates, OpenAI 3-field rates) — no shared type with a dangling nullable field.
- `EffectiveFrom`/`EffectiveTo` are `LocalDate?`; a dated entry beats an always-on entry at the same prefix length when both match a usage date.
- Both providers get a `FallbackPricing` entry used when no `ModelPrefix` matches (same values as today's hardcoded fallback), with the existing warning-log behavior preserved.
- Options registered unconditionally in `Program.cs` (not gated behind the provider's `IsConfigured(...)` API-key check) so `ValidateOnStart` fails fast regardless of whether that provider is enabled.
- Follow this codebase's existing options convention (see `IngestOptions.cs`): plain `{ get; set; }` properties with defaults, not `required`/`init`.
- No changes to `GoogleBillingClient` — out of scope (no per-token rate table there).

---

## File Structure

- Create: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicPricingOptions.cs` — `AnthropicPricingOptions`, `AnthropicPricingEntry`, `PricingRates4`.
- Create: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiPricingOptions.cs` — `OpenAiPricingOptions`, `OpenAiPricingEntry`, `PricingRates3`.
- Modify: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicUsageClient.cs` — replace `Pricing` dictionary + intro-window `if` with `IOptions<AnthropicPricingOptions>` resolution.
- Modify: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiUsageClient.cs` — replace `Pricing` dictionary with `IOptions<OpenAiPricingOptions>` resolution.
- Create: `src/AiObservatory.Ingest/appsettings.json` — `Ingest:Anthropic` and `Ingest:OpenAi` pricing sections.
- Modify: `src/AiObservatory.Ingest/Program.cs` — register + validate both options types.
- Modify: `src/AiObservatory.Ingest/AiObservatory.Ingest.csproj` — add `Microsoft.Extensions.Options.DataAnnotations` package reference.
- Modify: `Directory.Packages.props` — add `Microsoft.Extensions.Options.DataAnnotations` version.
- Modify: `tests/AiObservatory.Ingest.Tests/Services/AnthropicUsageClientTests.cs` — `CreateSut` builds `IOptions<AnthropicPricingOptions>` instead of relying on the old hardcoded dictionary.
- Create: `tests/AiObservatory.Ingest.Tests/Services/OpenAiUsageClientTests.cs` — new coverage for prefix-match + fallback resolution (no existing test file covered `OpenAiUsageClient.ComputeCost`).

---

### Task 1: Add the `Microsoft.Extensions.Options.DataAnnotations` package

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`

**Interfaces:**
- Produces: package reference available to `AiObservatory.Ingest` for `ValidateDataAnnotations()` in Task 6.

- [ ] **Step 1: Add the central package version**

In `Directory.Packages.props`, add to the `<!-- Ingest -->` group (after the existing `Microsoft.Extensions.Http` line):

```xml
<PackageVersion Include="Microsoft.Extensions.Options.DataAnnotations" Version="10.0.9" />
```

- [ ] **Step 2: Reference it from the Ingest project**

In `src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`, add to the existing `<ItemGroup>` of `PackageReference`s (alphabetical position after `Microsoft.Extensions.Http`):

```xml
<PackageReference Include="Microsoft.Extensions.Options.DataAnnotations" />
```

- [ ] **Step 3: Restore and verify it builds**

Run: `dotnet build src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add Directory.Packages.props src/AiObservatory.Ingest/AiObservatory.Ingest.csproj
git commit -m "build: add Microsoft.Extensions.Options.DataAnnotations to Ingest"
```

---

### Task 2: Anthropic pricing options + resolution logic

**Files:**
- Create: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicPricingOptions.cs`
- Modify: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicUsageClient.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/Services/AnthropicUsageClientTests.cs`

**Interfaces:**
- Produces: `AnthropicPricingOptions { List<AnthropicPricingEntry> Pricing; PricingRates4 FallbackPricing; }`, `AnthropicPricingEntry(string ModelPrefix, decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, LocalDate? EffectiveFrom, LocalDate? EffectiveTo)`, `PricingRates4(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)`.
- Consumes: none (first task to touch this file).

- [ ] **Step 1: Create the options file**

```csharp
using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public class AnthropicPricingOptions
{
    public const string SectionName = "Ingest:Anthropic";

    public List<AnthropicPricingEntry> Pricing { get; set; } = [];
    public PricingRates4 FallbackPricing { get; set; } = new(3.0m, 15.0m, 0.30m, 3.75m);
}

public sealed record AnthropicPricingEntry(
    string ModelPrefix,
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite,
    LocalDate? EffectiveFrom = null,
    LocalDate? EffectiveTo = null);

public sealed record PricingRates4(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
```

- [ ] **Step 2: Update the failing test to build options explicitly**

Replace `CreateSut` in `tests/AiObservatory.Ingest.Tests/Services/AnthropicUsageClientTests.cs`:

```csharp
using System.Net;
using AiObservatory.Ingest.Services.Anthropic;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public class AnthropicUsageClientTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static readonly AnthropicPricingOptions TestPricing = new()
    {
        Pricing =
        [
            new AnthropicPricingEntry("claude-sonnet-5", 2.0m, 10.0m, 0.20m, 2.50m, EffectiveTo: new LocalDate(2026, 8, 31)),
            new AnthropicPricingEntry("claude-sonnet-5", 3.0m, 15.0m, 0.30m, 3.75m, EffectiveFrom: new LocalDate(2026, 9, 1)),
        ],
        FallbackPricing = new PricingRates4(3.0m, 15.0m, 0.30m, 3.75m),
    };

    private static AnthropicUsageClient CreateSut(LocalDate bucketDate, string model)
    {
        var json = $$"""
            {
              "data": [
                {
                  "starting_at": "{{bucketDate:yyyy-MM-dd}}T00:00:00Z",
                  "ending_at": "{{bucketDate.PlusDays(1):yyyy-MM-dd}}T00:00:00Z",
                  "results": [
                    {
                      "model": "{{model}}",
                      "input_tokens": 1000000,
                      "output_tokens": 1000000,
                      "cache_read_input_tokens": 0,
                      "cache_creation_input_tokens": 0
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://api.anthropic.com") };
        return new AnthropicUsageClient(http, NullLogger<AnthropicUsageClient>.Instance, Options.Create(TestPricing));
    }

    [Fact]
    public async Task GetUsageAsync_applies_sonnet5_intro_rate_within_window()
    {
        var date = new LocalDate(2026, 8, 31);
        var sut = CreateSut(date, "claude-sonnet-5");

        var records = await sut.GetUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(12.0m); // $2 input + $10 output per 1M tokens
    }

    [Fact]
    public async Task GetUsageAsync_applies_standard_rate_after_intro_window()
    {
        var date = new LocalDate(2026, 9, 1);
        var sut = CreateSut(date, "claude-sonnet-5");

        var records = await sut.GetUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(18.0m); // $3 input + $15 output per 1M tokens
    }

    [Fact]
    public async Task GetUsageAsync_falls_back_to_fallback_pricing_for_unknown_model()
    {
        var date = new LocalDate(2026, 7, 1);
        var sut = CreateSut(date, "claude-unknown-model");

        var records = await sut.GetUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(18.0m); // fallback: $3 input + $15 output per 1M tokens
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail (compile error — constructor mismatch)**

Run: `dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~AnthropicUsageClientTests`
Expected: FAIL to build — `AnthropicUsageClient` has no 3-arg constructor yet.

- [ ] **Step 4: Update `AnthropicUsageClient` to consume the options**

In `src/AiObservatory.Ingest/Services/Anthropic/AnthropicUsageClient.cs`, replace the class declaration, the `Pricing`/`Sonnet5IntroPricing*` fields, and `ComputeCost`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;
// ReSharper disable NotAccessedPositionalProperty.Local; unused fields kept for shape-fidelity with the API response.

namespace AiObservatory.Ingest.Services.Anthropic;

// Calls GET https://api.anthropic.com/v1/organizations/usage_report/messages
// Requires an API key with workspace admin access (ANTHROPIC_BILLING_KEY env var).
// See https://docs.anthropic.com/en/api/usage for the current response schema.
public class AnthropicUsageClient(HttpClient http, ILogger<AnthropicUsageClient> logger, IOptions<AnthropicPricingOptions> pricingOptions) : IAnthropicUsageClient
{
    // Requesting more pages than this for a single day's usage indicates the pagination
    // token is not advancing (e.g. an API change) — bail rather than loop unbounded.
    private const int MaxPages = 100;

    public async Task<IReadOnlyList<AnthropicUsageRecord>> GetUsageAsync(
        LocalDate date, CancellationToken ct = default)
    {
        var startInstant = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var endInstant = date.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var startStr = startInstant.ToString();
        var endStr = endInstant.ToString();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var allRecords = new List<AnthropicUsageRecord>();

        string? nextPage = null;
        bool hasMore = true;
        int page = 0;

        while (hasMore)
        {
            if (++page > MaxPages)
            {
                logger.LogWarning("Anthropic usage pagination exceeded {MaxPages} pages for {Date}; stopping", MaxPages, date);
                break;
            }

            var url = $"/v1/organizations/usage_report/messages?starting_at={startStr}&ending_at={endStr}&bucket_width=1d&group_by[]=model";
            if (!string.IsNullOrEmpty(nextPage))
            {
                // The response's next_page token is passed back as the `page` request parameter.
                url += $"&page={nextPage}";
            }

            var response = await http.GetFromJsonAsync<AnthropicUsageApiResponse>(url, options, ct);
            foreach (var bucket in response?.Data ?? [])
            {
                foreach (var result in bucket.Results ?? [])
                {
                    if (string.IsNullOrEmpty(result.Model)) { continue; }

                    var cost = ComputeCost(result.Model, date, result.InputTokens, result.OutputTokens,
                        result.CacheReadInputTokens, result.CacheCreationInputTokens);

                    allRecords.Add(new AnthropicUsageRecord(
                        Date: date,
                        Model: result.Model,
                        InputTokens: result.InputTokens,
                        OutputTokens: result.OutputTokens,
                        CacheReadInputTokens: result.CacheReadInputTokens,
                        CacheCreationInputTokens: result.CacheCreationInputTokens,
                        CostUsd: cost,
                        RawJson: JsonSerializer.Serialize(result, options)));
                }
            }

            hasMore = response?.HasMore == true && !string.IsNullOrEmpty(response.NextPage);
            nextPage = response?.NextPage;
        }

        return allRecords;
    }

    private decimal ComputeCost(string model, LocalDate usageDate, long input, long output, long cacheRead, long cacheWrite)
    {
        // Longest matching prefix wins; among ties for a given date, a dated (bounded)
        // entry beats an always-on one — this is how the Sonnet-5 intro-pricing window
        // is expressed as data instead of a special-cased branch.
        var match = pricingOptions.Value.Pricing
            .Where(e => model.StartsWith(e.ModelPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => (e.EffectiveFrom is null || usageDate >= e.EffectiveFrom)
                     && (e.EffectiveTo is null || usageDate <= e.EffectiveTo))
            .OrderByDescending(e => e.ModelPrefix.Length)
            .ThenByDescending(e => e.EffectiveFrom is not null || e.EffectiveTo is not null)
            .FirstOrDefault();

        if (match is null)
        {
            // Unknown model — surface it instead of silently mis-costing at the fallback rate.
            logger.LogWarning("No Anthropic pricing entry for model '{Model}'; using fallback rates. Add an explicit entry to keep cost accurate.", model);
        }

        var (ir, or, crr, cwr) = match is null
            ? pricingOptions.Value.FallbackPricing
            : new PricingRates4(match.Input, match.Output, match.CacheRead, match.CacheWrite);

        return input / 1_000_000m * ir + output / 1_000_000m * or
             + cacheRead / 1_000_000m * crr + cacheWrite / 1_000_000m * cwr;
    }

    private sealed record AnthropicUsageApiResponse(List<AnthropicUsageBucket>? Data, bool? HasMore, string? NextPage);
    private sealed record AnthropicUsageBucket(string? StartingAt, string? EndingAt, List<AnthropicUsageResult>? Results);
    private sealed record AnthropicUsageResult(
        string? Model,
        long InputTokens,
        long OutputTokens,
        long CacheReadInputTokens,
        long CacheCreationInputTokens);
}
```

Note: `PricingRates4` is a `record`, so `var (ir, or, crr, cwr) = someRecord` deconstruction works via its generated positional deconstructor — no manual `Deconstruct` needed.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~AnthropicUsageClientTests`
Expected: 3 tests PASS (`applies_sonnet5_intro_rate_within_window`, `applies_standard_rate_after_intro_window`, `falls_back_to_fallback_pricing_for_unknown_model`).

- [ ] **Step 6: Commit**

```bash
git add src/AiObservatory.Ingest/Services/Anthropic/AnthropicPricingOptions.cs src/AiObservatory.Ingest/Services/Anthropic/AnthropicUsageClient.cs tests/AiObservatory.Ingest.Tests/Services/AnthropicUsageClientTests.cs
git commit -m "feat: move Anthropic model pricing from hardcoded dictionary to config"
```

---

### Task 3: OpenAI pricing options + resolution logic

**Files:**
- Create: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiPricingOptions.cs`
- Modify: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiUsageClient.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Services/OpenAiUsageClientTests.cs`

**Interfaces:**
- Produces: `OpenAiPricingOptions { List<OpenAiPricingEntry> Pricing; PricingRates3 FallbackPricing; }`, `OpenAiPricingEntry(string ModelPrefix, decimal Input, decimal Output, decimal CacheRead, LocalDate? EffectiveFrom, LocalDate? EffectiveTo)`, `PricingRates3(decimal Input, decimal Output, decimal CacheRead)`.
- Consumes: none (independent of Task 2 — separate provider, separate files).

- [ ] **Step 1: Create the options file**

```csharp
using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public class OpenAiPricingOptions
{
    public const string SectionName = "Ingest:OpenAi";

    public List<OpenAiPricingEntry> Pricing { get; set; } = [];
    public PricingRates3 FallbackPricing { get; set; } = new(2.50m, 10.00m, 1.25m);
}

public sealed record OpenAiPricingEntry(
    string ModelPrefix,
    decimal Input,
    decimal Output,
    decimal CacheRead,
    LocalDate? EffectiveFrom = null,
    LocalDate? EffectiveTo = null);

public sealed record PricingRates3(decimal Input, decimal Output, decimal CacheRead);
```

- [ ] **Step 2: Write the new (failing) test file**

```csharp
using System.Net;
using AiObservatory.Ingest.Services.OpenAi;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public class OpenAiUsageClientTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static readonly OpenAiPricingOptions TestPricing = new()
    {
        Pricing =
        [
            new OpenAiPricingEntry("gpt-4.1", 2.00m, 8.00m, 0.50m),
            new OpenAiPricingEntry("gpt-4.1-mini", 0.40m, 1.60m, 0.10m),
        ],
        FallbackPricing = new PricingRates3(2.50m, 10.00m, 1.25m),
    };

    private static OpenAiUsageClient CreateSut(LocalDate bucketDate, string model)
    {
        var startTime = bucketDate.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var endTime = bucketDate.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "data": [
                {
                  "start_time": {{startTime}},
                  "end_time": {{endTime}},
                  "results": [
                    {
                      "model": "{{model}}",
                      "input_tokens": 1000000,
                      "output_tokens": 1000000,
                      "input_cached_tokens": 0,
                      "num_model_requests": 1
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://api.openai.com") };
        return new OpenAiUsageClient(http, NullLogger<OpenAiUsageClient>.Instance, Options.Create(TestPricing));
    }

    [Fact]
    public async Task GetDailyUsageAsync_resolves_longest_matching_prefix()
    {
        var date = new LocalDate(2026, 7, 1);
        var sut = CreateSut(date, "gpt-4.1-mini-2025-04-14");

        var records = await sut.GetDailyUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(2.0m); // $0.40 input + $1.60 output per 1M tokens (gpt-4.1-mini, not gpt-4.1)
    }

    [Fact]
    public async Task GetDailyUsageAsync_falls_back_to_fallback_pricing_for_unknown_model()
    {
        var date = new LocalDate(2026, 7, 1);
        var sut = CreateSut(date, "gpt-unknown-model");

        var records = await sut.GetDailyUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(12.5m); // fallback: $2.50 input + $10 output per 1M tokens
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail (compile error — constructor mismatch)**

Run: `dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~OpenAiUsageClientTests`
Expected: FAIL to build — `OpenAiUsageClient` has no 3-arg constructor yet.

- [ ] **Step 4: Update `OpenAiUsageClient` to consume the options**

Replace the class declaration and `ComputeCost` in `src/AiObservatory.Ingest/Services/OpenAi/OpenAiUsageClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;
// ReSharper disable NotAccessedPositionalProperty.Local; unused fields kept for shape-fidelity with the API response.

namespace AiObservatory.Ingest.Services.OpenAi;

// Calls GET https://api.openai.com/v1/organization/usage/completions
// Requires an admin API key (OPENAI_ADMIN_KEY env var) with the
// openai.usage.read permission (create one at platform.openai.com/api-keys).
// See https://platform.openai.com/docs/api-reference/usage for the schema.
public class OpenAiUsageClient(HttpClient http, ILogger<OpenAiUsageClient> logger, IOptions<OpenAiPricingOptions> pricingOptions) : IOpenAiUsageClient
{
    public async Task<IReadOnlyList<OpenAiUsageRecord>> GetDailyUsageAsync(
        LocalDate date, CancellationToken ct = default)
    {
        var startTime = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var endTime = date.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var allRecords = new List<OpenAiUsageRecord>();

        string? nextPage = null;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"/v1/organization/usage/completions?start_time={startTime}&end_time={endTime}&bucket_width=1d&group_by[]=model&limit=100";
            if (!string.IsNullOrEmpty(nextPage))
            {
                url += $"&page={nextPage}";
            }

            var response = await http.GetFromJsonAsync<OpenAiUsageApiResponse>(url, options, ct);

            foreach (var bucket in response?.Data ?? [])
            {
                foreach (var result in bucket.Results ?? [])
                {
                    if (string.IsNullOrEmpty(result.Model)) { continue; }

                    var cost = ComputeCost(result.Model, date, result.InputTokens, result.OutputTokens, result.InputCachedTokens);

                    allRecords.Add(new OpenAiUsageRecord(
                        Date: date,
                        Model: result.Model,
                        InputTokens: result.InputTokens,
                        OutputTokens: result.OutputTokens,
                        CachedInputTokens: result.InputCachedTokens,
                        CostUsd: cost,
                        RawJson: JsonSerializer.Serialize(result, options)));
                }
            }

            hasMore = response?.HasMore == true && !string.IsNullOrEmpty(response.NextPage);
            nextPage = response?.NextPage;
        }

        return allRecords;
    }

    private decimal ComputeCost(string model, LocalDate usageDate, long input, long output, long cachedInput)
    {
        // Longest matching prefix wins. The OpenAI usage API returns ids like
        // "gpt-4o-mini-2024-07-18"; StartsWith + longest key resolves to the specific
        // variant rather than the base model (see git history for the Contains bug this
        // replaced). Among ties for a given date, a dated entry beats an always-on one.
        var match = pricingOptions.Value.Pricing
            .Where(e => model.StartsWith(e.ModelPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => (e.EffectiveFrom is null || usageDate >= e.EffectiveFrom)
                     && (e.EffectiveTo is null || usageDate <= e.EffectiveTo))
            .OrderByDescending(e => e.ModelPrefix.Length)
            .ThenByDescending(e => e.EffectiveFrom is not null || e.EffectiveTo is not null)
            .FirstOrDefault();

        // Surface an unrecognised model instead of silently billing it at the fallback rate.
        if (match is null)
        {
            logger.LogWarning("No OpenAI pricing entry for model '{Model}'; using fallback rates. Add an explicit entry to keep cost accurate.", model);
        }

        var (ir, or, cr) = match is null
            ? pricingOptions.Value.FallbackPricing
            : new PricingRates3(match.Input, match.Output, match.CacheRead);

        // Cached tokens are billed at the cache read rate; non-cached at the full input rate
        var billableInput = Math.Max(0, input - cachedInput);
        return billableInput / 1_000_000m * ir
             + output / 1_000_000m * or
             + cachedInput / 1_000_000m * cr;
    }

    private sealed record OpenAiUsageApiResponse(List<OpenAiUsageBucket>? Data, bool? HasMore, string? NextPage);
    private sealed record OpenAiUsageBucket(long StartTime, long EndTime, List<OpenAiUsageResult>? Results);
    private sealed record OpenAiUsageResult(
        string? Model,
        long InputTokens,
        long OutputTokens,
        long InputCachedTokens,
        long NumModelRequests);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~OpenAiUsageClientTests`
Expected: 2 tests PASS (`resolves_longest_matching_prefix`, `falls_back_to_fallback_pricing_for_unknown_model`).

- [ ] **Step 6: Commit**

```bash
git add src/AiObservatory.Ingest/Services/OpenAi/OpenAiPricingOptions.cs src/AiObservatory.Ingest/Services/OpenAi/OpenAiUsageClient.cs tests/AiObservatory.Ingest.Tests/Services/OpenAiUsageClientTests.cs
git commit -m "feat: move OpenAI model pricing from hardcoded dictionary to config"
```

---

### Task 4: appsettings.json + Program.cs wiring

**Files:**
- Create: `src/AiObservatory.Ingest/appsettings.json`
- Modify: `src/AiObservatory.Ingest/Program.cs`

**Interfaces:**
- Consumes: `AnthropicPricingOptions.SectionName` ("Ingest:Anthropic"), `OpenAiPricingOptions.SectionName` ("Ingest:OpenAi") from Tasks 2/3; `AnthropicUsageClient`/`OpenAiUsageClient` constructors from Tasks 2/3 (already DI-resolvable once `IOptions<T>` is registered — no explicit `new` calls exist in `Program.cs` today, both are constructed via `AddHttpClient<TInterface, TImpl>`).

- [ ] **Step 1: Create `appsettings.json` with today's rate values as config**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "Ingest": {
    "Anthropic": {
      "Pricing": [
        { "ModelPrefix": "claude-3-5-sonnet", "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75 },
        { "ModelPrefix": "claude-3-5-haiku", "Input": 0.8, "Output": 4.0, "CacheRead": 0.08, "CacheWrite": 1.00 },
        { "ModelPrefix": "claude-opus-4", "Input": 15.0, "Output": 75.0, "CacheRead": 1.50, "CacheWrite": 18.75 },
        { "ModelPrefix": "claude-sonnet-4", "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75 },
        { "ModelPrefix": "claude-sonnet-5", "Input": 2.0, "Output": 10.0, "CacheRead": 0.20, "CacheWrite": 2.50, "EffectiveTo": "2026-08-31" },
        { "ModelPrefix": "claude-sonnet-5", "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75, "EffectiveFrom": "2026-09-01" },
        { "ModelPrefix": "claude-haiku-4", "Input": 0.8, "Output": 4.0, "CacheRead": 0.08, "CacheWrite": 1.00 },
        { "ModelPrefix": "claude-3-opus", "Input": 15.0, "Output": 75.0, "CacheRead": 1.50, "CacheWrite": 18.75 },
        { "ModelPrefix": "claude-3-sonnet", "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75 },
        { "ModelPrefix": "claude-3-haiku", "Input": 0.25, "Output": 1.25, "CacheRead": 0.03, "CacheWrite": 0.3125 }
      ],
      "FallbackPricing": { "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75 }
    },
    "OpenAi": {
      "Pricing": [
        { "ModelPrefix": "gpt-4.1", "Input": 2.00, "Output": 8.00, "CacheRead": 0.50 },
        { "ModelPrefix": "gpt-4.1-mini", "Input": 0.40, "Output": 1.60, "CacheRead": 0.10 },
        { "ModelPrefix": "gpt-4.1-nano", "Input": 0.10, "Output": 0.40, "CacheRead": 0.025 },
        { "ModelPrefix": "gpt-4o", "Input": 2.50, "Output": 10.00, "CacheRead": 1.25 },
        { "ModelPrefix": "gpt-4o-mini", "Input": 0.15, "Output": 0.60, "CacheRead": 0.075 },
        { "ModelPrefix": "o1", "Input": 15.0, "Output": 60.00, "CacheRead": 7.50 },
        { "ModelPrefix": "o1-mini", "Input": 1.10, "Output": 4.40, "CacheRead": 0.55 },
        { "ModelPrefix": "o3", "Input": 2.00, "Output": 8.00, "CacheRead": 0.50 },
        { "ModelPrefix": "o3-mini", "Input": 1.10, "Output": 4.40, "CacheRead": 0.55 },
        { "ModelPrefix": "o4-mini", "Input": 1.10, "Output": 4.40, "CacheRead": 0.275 }
      ],
      "FallbackPricing": { "Input": 2.50, "Output": 10.00, "CacheRead": 1.25 }
    }
  }
}
```

- [ ] **Step 2: Register and validate both options types in `Program.cs`**

In `src/AiObservatory.Ingest/Program.cs`, add these `using` statements after the existing ones:

```csharp
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.OpenAi;
```

(`Services.Anthropic` and `Services.OpenAi` are already imported — verify, do not duplicate.)

Then, immediately after the existing `services.Configure<IngestOptions>(...)` line (before the `// Anthropic —` comment block), add:

```csharp
        services.AddOptions<AnthropicPricingOptions>()
            .Bind(cfg.GetSection(AnthropicPricingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Pricing.Count > 0, $"{AnthropicPricingOptions.SectionName}:Pricing must have at least one entry")
            .ValidateOnStart();

        services.AddOptions<OpenAiPricingOptions>()
            .Bind(cfg.GetSection(OpenAiPricingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Pricing.Count > 0, $"{OpenAiPricingOptions.SectionName}:Pricing must have at least one entry")
            .ValidateOnStart();
```

- [ ] **Step 3: Verify the Worker SDK picks up `appsettings.json` and the app starts**

Run: `dotnet build src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`
Expected: `Build succeeded.` — confirm `bin/Debug/net10.0/appsettings.json` exists after build (Worker SDK copies it as content by convention, same as `Microsoft.NET.Sdk.Web`).

Run: `Test-Path src/AiObservatory.Ingest/bin/Debug/net10.0/appsettings.json` (PowerShell, from the repo root)
Expected: `True`

- [ ] **Step 4: Commit**

```bash
git add src/AiObservatory.Ingest/appsettings.json src/AiObservatory.Ingest/Program.cs
git commit -m "feat: wire Anthropic/OpenAI pricing config with startup validation"
```

---

### Task 5: Full solution build + test verification

**Files:** none (verification only)

**Interfaces:** none

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build AiObservatory.slnx`
Expected: `Build succeeded.` with no warnings about `NotAccessedPositionalProperty` regressions and no new warnings.

- [ ] **Step 2: Run the full Ingest test project**

Run: `dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj`
Expected: all tests PASS, including the 3 `AnthropicUsageClientTests` and 2 `OpenAiUsageClientTests` from Tasks 2–3.

- [ ] **Step 3: Run the rest of the solution's tests (Data.Tests will fail without local Postgres — expected, not a regression per project convention)**

Run: `dotnet test AiObservatory.slnx`
Expected: all projects PASS except `AiObservatory.Data.Tests`, which fails with a Postgres connection-refused error if no local instance is running — pre-existing, unrelated to this change.

- [ ] **Step 4: Manually sanity-check `ComputeCost` resolution order by inspecting the appsettings values against the removed dictionaries**

Confirm every `(prefix, rates)` pair from the original `AnthropicUsageClient.Pricing`/`OpenAiUsageClient.Pricing` dictionaries (see Task 2/3 diffs) has a matching entry in `appsettings.json` from Task 4, with no value transcription errors (spot-check `claude-3-haiku` = `0.25/1.25/0.03/0.3125` and `o4-mini` = `1.10/4.40/0.275`).

- [ ] **Step 5: Commit (only if Step 4 uncovered a fix — otherwise skip, nothing to commit)**

```bash
git add -A
git commit -m "fix: correct transcribed pricing value in appsettings.json"
```
