# Adding a provider

> Compile-time extension guide for maintainers as of 2026-08-25. It covers source acquisition and catalog metadata, not runtime plugins or schema changes.

Add the smallest adapter that reports a real upstream fact. Do not fabricate tokens, prices, or a provider identity merely to fit a shared shape.

## Extension sequence

1. Add a `Provider` member in `src/AiObservatory.Data/Entities/Provider.cs` when the provider emits usage **or** carries a pricing snapshot: `PricingSnapshotCandidate`, `PricingSnapshotStore`, and price calculators are enum-keyed. Persistence is string-backed, so this needs no migration. A billing-only adapter can remain on `BillingObservation.ProviderKey` without a fake enum member.
2. Add stable IDs in `src/AiObservatory.Data/Entities/ObservationProvenance.cs` and `src/AiObservatory.Data/Pricing/PricingSnapshotCandidate.cs`. Add display/source metadata in `src/AiObservatory.Web/src/config/providers.ts`; add CSS tokens only for a new known identity. Unknown providers use the existing fallback.
3. Implement usage clients and sources under `src/AiObservatory.Ingest/Services/<Provider>/`, and pricing adapters under `src/AiObservatory.Ingest/Pricing/`; `IUsageSource` and `IPricingSource` remain in `src/AiObservatory.Ingest/Sources/`. A billing-only adapter still implements scheduled `IUsageSource` and writes `BillingObservation`; it must not invent token rows.
4. For provider-specific pricing, add the catalog and `IProviderPriceCalculator` implementation in `src/AiObservatory.Data/Pricing/`, register that calculator in `src/AiObservatory.Data/ServiceCollectionExtensions.cs`, and add the provider source/catalog validation mappings in `PricingSnapshotStore`. If the provider needs cold-start coverage, add its mapping in `BundledPricingCatalogLoader` at `src/AiObservatory.Ingest/Pricing/BundledPricingCatalogLoader.cs`. Register `SourceDefinition` or `PricingSourceDefinition` and the implementation in `src/AiObservatory.Ingest/Program.cs`. Do not edit `ProviderPollingWorkerService` or `PricingRefreshWorkerService`.
5. Add client/parser fixtures, incomplete-pagination or download rejection, source-status/composition coverage, unknown-dimension/null pricing, correction/idempotency, and frontend registry fallback coverage where applicable.
6. Add setup access and limitations to the [provider matrix](provider-setup.md).

The seams are compile-time adapters only. There is no runtime plugin loader, schema change, dashboard component switch, central-worker switch, or generic pricing-rules framework.

## Focused checks

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj
```

```powershell
npm --prefix src/AiObservatory.Web test -- --run src/config/providers.test.ts
```
