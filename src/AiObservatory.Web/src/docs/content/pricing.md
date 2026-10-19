# Pricing

Observatory rates tokens against stored price catalogs to produce list-price estimates and subscription notional values. This page covers which events are rated, where the catalogs come from, how a price is chosen, what makes a price unknown, and when stored prices are recalculated.

## What is rated and what is not

Only two cost bases are rated by Observatory. Everything else keeps the cost its source gave it, or has none.

| Cost basis          | Rated from a catalog?          | Where the cost comes from                                                                                                                              |
| ------------------- | ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| List-price estimate | Yes, at the date of use        | Tokens rated against the catalog. Produced by the Anthropic and OpenAI usage sources, Gemini review sessions and DeepSeek sessions seen through Codex. |
| Notional            | Yes, at the catalog's own date | Tokens from subscription or local telemetry, rated as if they had been API calls. Not money spent.                                                     |
| Provider estimated  | No                             | The cost the provider or the tool reported (Claude Code analytics, Grok, PI).                                                                          |
| Billed              | No                             | Spend ledger evidence from a billing API or a manual entry.                                                                                            |
| None                | No                             | No price applies and none is recorded, for example Copilot organization counts or Claude Code rows whose report carries no estimate.                   |
| Unknown             | No                             | Older rows whose cost cannot be established.                                                                                                           |

If a client sends `costUsd` with a list-price estimate or notional event, the server ignores it and rates the tokens itself. The meaning of each basis is in [Concepts](/docs/concepts).

## Catalogs

A catalog is a table of per-million-token rates, keyed by model name and by whatever else changes the rate.

| Source id                      | Provider  | How it is obtained                                                                                                                                                              |
| ------------------------------ | --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `openai-pricing`               | OpenAI    | Fetched daily from OpenAI's pricing page (Markdown).                                                                                                                            |
| `claude-pricing`               | Anthropic | Fetched daily from Anthropic's pricing page (Markdown).                                                                                                                         |
| `kimi-pricing`                 | Moonshot  | Fetched daily from Kimi's documentation, only when `PRICING_RETIRED_SOURCES_ENABLED=true`.                                                                                      |
| `xai-pricing`                  | xAI       | Fetched daily from xAI's pricing page, only when `PRICING_RETIRED_SOURCES_ENABLED=true`.                                                                                        |
| `meta-openrouter-pricing`      | Meta      | Fetched daily from OpenRouter's model list, only when `PRICING_RETIRED_SOURCES_ENABLED=true`.                                                                                   |
| `gemini-developer-api-pricing` | Google    | Bundled with the release and made active on each daily pass. Not fetched.                                                                                                       |
| `zai-pricing`                  | z.AI      | Bundled with the release and made active on each daily pass. Not fetched.                                                                                                       |
| `deepseek-pricing`             | DeepSeek  | Bundled with the release and made active on each daily pass. Not fetched.                                                                                                       |
| `google-cloud-catalog`         | Google    | A bundled catalog is stored, but the fetched source is off and no source supplies the SKU dimensions it needs. See [Providers](/docs/providers#known-unavailable-capabilities). |

Fetched URLs are fixed in the code: `developers.openai.com`, `platform.claude.com`, `platform.kimi.ai`, `docs.x.ai` and `openrouter.ai`. There is no setting to change them.

The retired lanes (Kimi, xAI, Meta) are dormant by default. Their status reads `notConfigured`, nothing is fetched, and the last stored catalog keeps pricing their history. Set `PRICING_RETIRED_SOURCES_ENABLED=true` for the ingest worker to fetch them again.

## How catalogs are kept up to date

Pricing runs in the ingest worker. If the worker is not running, nothing refreshes and events are rated from whatever catalog is already stored.

Each pass, at worker start and then every 24 hours:

1. **Bundled catalogs load.** Each provider has a catalog file inside the release. For the fetched providers it is stored only if the provider has no active catalog yet, so a fresh database can price immediately. For Gemini, z.AI and DeepSeek the bundled file replaces the active catalog, so updating those means shipping a new release.
2. **Fetched sources refresh.** A fetched source is skipped when its last success was less than a day ago. Otherwise the page is downloaded and parsed.
3. **The catalog is activated** if it is new, and every stored estimate for that provider is rated again; see [Repricing](#repricing).

### What makes a fetched page acceptable

Each parser is strict about its own page. A page is rejected, and the previous catalog stays in force, if it:

- is not explicitly USD, or has a non-positive rate,
- changes its table columns or heading structure, has an empty or partial table, or has a duplicate or overlapping model,
- lacks a model or lane the parser requires (OpenAI's page, for instance, must still contain every required current model lane).

The download is also bounded: HTTPS only, a fixed host allowlist, no more than three redirects (each re-checked against the allowlist), a 20-second timeout, a 2 MiB limit and strict UTF-8.

### Snapshots

Every accepted catalog is stored as a snapshot with the raw page text, the normalised catalog, a SHA-256 content hash, the retrieval time and the source URL. Only one snapshot per source is active. Older ones are kept.

- The hash covers the page and the normalised content but not fetch-time stamps, so a re-fetch of an unchanged page is discarded instead of stored again.
- If a source returns to an earlier document (A, then B, then A), the earlier snapshot is reactivated rather than duplicated.
- A failed refresh records the sanitised error against the source and leaves the active snapshot in place. A timeout, a malformed page, a partial table or a changed layout therefore never removes prices.

## Effective dates

Each catalog entry has an effective-from date, and a flag for whether the provider declared it.

- **Provider-declared** dates gate strictly. A price the provider announced for a future date is stored early but never applies before that date.
- **Assumed** dates are the day the page was fetched. If no entry strictly covers a usage date, the earliest assumed entry is treated as reaching back without limit. Without this, history older than the first fetch could never be priced.
- Among snapshots, the active one is tried first, then the most recently retrieved. If the active snapshot no longer lists a model, rating falls through to an older retained snapshot that still does, so a provider retiring a model does not strand its history.

## How an event is rated

1. A usage row with all token counts exactly zero is priced at zero.
2. The provider chooses the calculator and the catalog. Google is split: rows labelled `Gemini Developer API`, and all Google notional rows, use the Gemini Developer API catalog.
3. The pricing date depends on the basis. A list-price estimate uses the occurrence date of the usage (a UTC date). A notional value uses the date the catalog was retrieved, so notional values follow today's rates, not the rates on the day of use.
4. The model name is matched case-insensitively. OpenAI, Anthropic, Moonshot and Gemini Developer API match by prefix against each entry's model prefix and aliases; the longest matching prefix wins, then the latest effective date. xAI matches an exact model id or a declared alias, and Meta, z.AI and DeepSeek match an exact model id only, so a variant such as `grok-4.6-build` is never priced at a similar model's rates.
5. The rate is selected using dimensions recorded with the event (see below), and the cost is the sum of each token kind times its rate, per million tokens.
6. The cache saving is stored with the cost: what the cached tokens would have cost at the plain input rate, minus what they did cost.

If any step cannot be completed, the event gets no cost; see [Unknown prices](#unknown-prices).

## What a price needs

Dimensions are recorded in each event's raw payload by the source that saw the request. A list-price estimate that lacks a required dimension is not priced, because a guess can be wrong by a large factor. A notional value falls back to the standard public lane, because notional rows are aggregates that cannot carry per-request flags.

| Provider                     | Required for a list-price estimate                                                                             | Notional default                                                                                                         |
| ---------------------------- | -------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| OpenAI                       | `processing`, `context` and `region`                                                                           | `standard`, `short`, `global`                                                                                            |
| Anthropic                    | `service_tier`, `speed` and `inference_geo`; when cache writes exist, the exact five-minute and one-hour split | Standard tier and speed, global geography; cache writes priced as five-minute if the split is missing or does not add up |
| Moonshot                     | `high_speed` and `batch` flags                                                                                 | Standard lane; the model name alone decides high speed                                                                   |
| Google, Gemini Developer API | `tier` and `context` (`short` or `long`)                                                                       | `standard`, `short`                                                                                                      |
| Google, Cloud catalog        | SKU id, region, modality, tier, cache lane and context threshold                                               | Not applicable; not enabled                                                                                              |
| xAI                          | `long_context` flag                                                                                            | Standard lane                                                                                                            |
| Meta, z.AI, DeepSeek         | Only the model name                                                                                            | Only the model name                                                                                                      |

How each calculator turns tokens into cost:

- **OpenAI.** Input, output, cached input and cache write, each at its own rate. If the event has cached or cache-write tokens and the entry has no rate for them, there is no price. Reasoning tokens are not added.
- **Anthropic.** The standard lane uses the entry's input and output rates. Batch (`service_tier` of `batch`) and fast (`speed` of `fast`) use their own rates; batch and fast together is not priced. A `us` inference geography multiplies the whole cost by the entry's US multiplier, and a model without one is not priced. Cache read and cache write rates scale with the lane. Cache writes are charged at the five-minute and one-hour rates according to the recorded split.
- **Moonshot.** Standard or high-speed lane. The model's `-highspeed` suffix and the `high_speed` flag must agree or the event is not priced. Batch uses the entry's batch multiplier. Cache writes are billed as ordinary input. Notional rows normalise a few local model aliases first.
- **Google, Gemini Developer API.** Input, cached input, and output plus thinking tokens at the output rate.
- **xAI.** Standard or long-context lane. Cache writes are billed as ordinary input. Reasoning tokens are not added, because the vendor bills them inside output.
- **Meta and z.AI.** Input, output and cache read. Cache writes are billed as input. If an event has cache-read tokens and the entry has no cache-read rate, there is no price rather than a wrong one.
- **DeepSeek.** Cache-miss input (input plus cache writes), cache-hit input and output, always at the standard peak rate. DeepSeek halves its rates off-peak and on Chinese public holidays, but a local snapshot is one total per day and model, so time of day cannot be attributed. The result is an upper bound for token charges. Search and fetch calls billed per request are not in token telemetry, so the DeepSeek total can still be understated. Treat the invoice as the truth for DeepSeek.

## Unknown prices

An event gets no cost, and reads `Not reported` in the interface, when any of these holds:

- no calculator exists for its provider (Copilot has none),
- no catalog covers its date,
- the model matches no catalog entry,
- a required dimension is missing and the basis is not notional,
- a token kind has no rate in the entry.

There is no generic fallback price. A zero is never invented. In the Model breakdown the cost cell reads `Not reported` when no row of a model has a cost, and a model with some priced and some unpriced rows shows the known cost with a line such as `2 observations not reported` beside it. Cost per million tokens is `Not reported` when any row is unpriced. See [Usage and insights](/docs/usage-insights).

The server logs one warning for each distinct provider, model and missing-dimension combination: `No exact <provider> price for model '<model>'; missing or unmatched dimensions: ...`. Repeats are suppressed, and at 4,096 distinct combinations a final message says further warnings are suppressed. Model names are cut at 160 characters in the log.

To fix an unknown price: for an unmapped model, wait for the catalog to list it or ship one that does; for missing dimensions, fix the producer so it records them; for a provider without a calculator, there is no remedy except correcting the cost by hand.

## Repricing

Stored estimates are recalculated, so prices stay current as catalogs change.

- **When.** Whenever a catalog is activated, and at the end of each daily bundled-catalog load, for the provider in question.
- **Which events.** Only list-price estimate and notional events. Billed, provider-estimated, none and unknown events are never touched, and an event that cannot be priced for lack of dimensions is not forced.
- **Unresolvable events keep their price.** If a model has been dropped from every retained catalog, an event that already has a cost keeps its last known cost and basis. The worker logs one line per provider and model: `Repricing skipped <n> event(s) ...`.
- **Atomic.** A pass reads one generation of catalogs and updates events one transaction at a time, and daily aggregates are updated with them.
- **Notional values move.** Because notional values use the catalog's retrieval date, every catalog change rewrites them at the new rates. List-price estimates change only for dates the new catalog's entries cover.

## Correcting a cost by hand

`PATCH /api/events/{eventKey}/cost` sets a cost on a stored event. The event becomes `Provider estimated`, which removes it from repricing, and it keeps the corrected figure when the same local snapshot is posted again without a cost. A source that posts an explicit cost later takes authority back. The request needs the event's provider and, for non-legacy rows, its source id. See [API](/docs/api).

## Checking pricing health

- Pricing sources appear in the [Data sources](/docs/data-sources) panel under display names such as `OpenAI pricing`, `Claude pricing`, `Kimi pricing`, `xAI pricing` and `OpenRouter pricing` (the ids are in `GET /api/sources/status`), with a one-day expected interval. A source is `stale` after two days without a success.
- A fetched source that errors shows its sanitised message. The previous catalog keeps pricing meanwhile.
- The bundled-only catalogs (Gemini Developer API, z.AI, DeepSeek) have no status row.
- Retired sources show `notConfigured` unless you switched them on.

## References

- `src/AiObservatory.Data/Pricing/` (calculators, `UsagePriceResolver.cs`, `PricingSnapshotStore.cs`, `PricingRepricingService.cs`, `Catalogs/`)
- `src/AiObservatory.Ingest/Pricing/` (`PricingRefreshWorkerService.cs`, `BundledPricingCatalogLoader.cs`, `FirstPartyDocumentFetcher.cs`, the per-provider sources)
- `src/AiObservatory.Ingest/Program.cs` (`PRICING_RETIRED_SOURCES_ENABLED`)
- `src/AiObservatory.Api/Endpoints/EventsEndpoints.cs`
- `docs/truth-and-pricing.md`
- OpenAI pricing: https://developers.openai.com/api/docs/pricing.md
- Anthropic pricing: https://platform.claude.com/docs/en/about-claude/pricing.md
- Kimi documentation: https://platform.kimi.ai/docs/llms.txt
- xAI pricing: https://docs.x.ai/developers/pricing.md
- OpenRouter models: https://openrouter.ai/api/v1/models
- Gemini Developer API pricing: https://ai.google.dev/gemini-api/docs/pricing
- DeepSeek pricing: https://api-docs.deepseek.com/quick_start/pricing
- z.AI pricing: https://docs.z.ai/guides/overview/pricing
