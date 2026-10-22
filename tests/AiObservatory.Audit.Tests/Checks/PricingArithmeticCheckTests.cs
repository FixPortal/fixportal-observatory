using AiObservatory.Audit.Contracts;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Checks;

public sealed class PricingArithmeticCheckTests
{
    [Theory]
    [InlineData("OpenAi", 1000, 100, 200, 0, 0, "0.0038")]
    [InlineData("Anthropic", 1000, 100, 200, 100, 40, "0.00451")]
    public void IndependentMeters_HandCalculatedCostDetectsCorruptPosting(
        string provider,
        long inputTokens,
        long output,
        long read,
        long writes,
        long hour,
        string expected
    )
    {
        var input = Fixture(provider, inputTokens, output, read, writes, hour);
        var result = new AuditVerifier().Verify(input);
        var finding = result.Findings.Single(f => f.RuleCode == "pricing_cost_difference");
        finding.Expected!.Value.Should().Be(expected);
        finding.Actual!.Value.Should().Be("99");
        finding.EvidenceRefs.Should().Contain(input.Events[0].RowId.ToString("D"));
    }

    [Theory]
    [InlineData(null, null, "cache_duration_not_frozen", null)]
    [InlineData(-1, -1, "cache_duration_not_corroborated", "0.00025")]
    [InlineData(100, 0, "cache_duration_not_corroborated", "0.00025")]
    [InlineData(0, 100, null, "0.0004")]
    public void CacheAllocation_RequiresBothObservedSubsets(int? five, int? hour, string? diagnostic, string? expected)
    {
        var input = Fixture("Anthropic", 0, 0, 0, 100, 100);
        CacheDurationObservation? observation = null;
        if (five != null)
        {
            observation = new(five < 0 ? null : five, hour < 0 ? null : hour);
        }
        input = input with { Events = [input.Events[0] with { CacheDurations = observation }] };
        var result = new AuditVerifier().Verify(input);
        if (diagnostic != null)
        {
            result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == diagnostic);
        }
        if (expected != null)
        {
            result.Findings.Single(f => f.RuleCode == "pricing_cost_difference").Expected!.Value.Should().Be(expected);
        }
        else
        {
            result.Findings.Should().BeEmpty();
        }
    }

    [Fact]
    public void DeclaredCatalogMatch_DoesNotAuthenticateHistoricalPublication()
    {
        var input = Fixture("OpenAi", 1000, 100, 200, 0, 0);
        input = input with { Events = [input.Events[0] with { CostUsd = 0.0038m }] };
        var result = new AuditVerifier().Verify(input);
        result.Findings.Should().BeEmpty();
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "historical_rate_evidence_missing");
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void UnsupportedBillingAndProvider_AreExplicit()
    {
        var input = Fixture("Google", 1, 1, 0, 0, 0);
        input = input with
        {
            Scope = input.Scope with
            {
                RequestedChecks = [CheckCode.PricingArithmetic, CheckCode.BillingReconciliation],
            },
        };
        var result = new AuditVerifier().Verify(input);
        result.Checks.Should().OnlyContain(c => c.Verdict == CheckVerdict.Unsupported);
    }

    [Theory]
    [InlineData("priority", "long", 10, "0.011")]
    [InlineData("flex", "standard", 1, "0.002")]
    public void ProcessingAndContext_SelectExactDeclaredLane(
        string processing,
        string context,
        int rate,
        string expected
    )
    {
        var input = Fixture("OpenAi", 1000, 100, 0);
        var dimensions = input.Events[0].PricingApplication!.Dimensions with
        {
            Processing = processing,
            Context = context,
        };
        var catalog = input.PricingCatalogs[0];
        catalog = catalog with
        {
            Rates = catalog
                .Rates.Select(r =>
                    r with
                    {
                        Dimensions = dimensions,
                        UnitPrice = r.Meter == PriceMeter.Input ? rate : 10m,
                    }
                )
                .ToArray(),
        };
        input = input with
        {
            PricingCatalogs = [catalog],
            Events =
            [
                input.Events[0] with
                {
                    PricingApplication = input.Events[0].PricingApplication! with { Dimensions = dimensions },
                },
            ],
        };
        var finding = new AuditVerifier().Verify(input).Findings.Single();
        finding.Expected!.Value.Should().Be(expected);
    }

    [Fact]
    public void NonNotionalContradictoryCacheDuration_IsNotEligibleForFallback()
    {
        var input = Fixture("Anthropic", 0, 0, 0, 100, 100);
        input = input with
        {
            Events =
            [
                input.Events[0] with
                {
                    Provenance = new(SourceKind.ProviderApi, UsageScope.Api, AccountingBasis.ListPriceEstimate),
                    CacheDurations = new(100, 0),
                },
            ],
        };
        var result = new AuditVerifier().Verify(input);
        result.Findings.Should().BeEmpty();
        result.Coverage.ComparedGroups.Should().Be(0);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "cache_duration_not_corroborated");
    }

    [Theory]
    [InlineData("missing", "pricing_application_missing")]
    [InlineData("manual", "manual_correction_not_catalog")]
    [InlineData("unknown", "pricing_dimensions_unknown")]
    [InlineData("missingCatalog", "applied_catalog_missing")]
    [InlineData("stale", "applied_rate_missing")]
    public void UnqualifiedPriceProvenance_RemainsIncomplete(string state, string diagnostic)
    {
        var input = Fixture();
        var e = input.Events[0];
        e = state switch
        {
            "missing" => e with { PricingApplication = null },
            "manual" => e with
            {
                CorrectedAt = e.ObservedAt,
                PricingApplication = e.PricingApplication! with { Kind = "operatorCorrection" },
            },
            "unknown" => e with
            {
                PricingApplication = e.PricingApplication! with
                {
                    Dimensions = e.PricingApplication.Dimensions with { Tier = ServiceTier.Unknown },
                },
            },
            "missingCatalog" => e with
            {
                PricingApplication = e.PricingApplication! with { SnapshotHash = new('b', 64) },
            },
            _ => e with
            {
                PricingApplication = e.PricingApplication! with { PricingDate = CheckFixture.Day.AddDays(-1) },
            },
        };
        var result = new AuditVerifier().Verify(input with { Events = [e] });
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == diagnostic);
    }

    [Fact]
    public void CorruptOneRate_IdentifiesPostingAndExactEquation()
    {
        var input = Fixture();
        var catalog = input.PricingCatalogs[0];
        input = input with
        {
            PricingCatalogs =
            [
                catalog with
                {
                    Rates = catalog
                        .Rates.Select(r => r.Meter == PriceMeter.CacheRead ? r with { UnitPrice = 3m } : r)
                        .ToArray(),
                },
            ],
        };
        var result = new AuditVerifier().Verify(input);
        result.Findings.Single().Expected!.Value.Should().Be("0.0042");
    }

    [Fact]
    public void CorruptTypedHourSubset_ProducesExactTokenFindingForThePosting()
    {
        var input = Fixture("Anthropic", 0, 0, 0, 10, 11);
        input = input with { Events = [input.Events[0] with { CacheDurations = new(0, 11) }] };
        var result = new AuditVerifier().Verify(input);
        var finding = result.Findings.Single();
        finding.RuleCode.Should().Be("cache_write_subset_invalid");
        finding.Field.Should().Be("cacheWrite1hTokens");
        finding.Expected!.Value.Should().Be("10");
        finding.Actual!.Value.Should().Be("11");
        finding.Delta!.Value.Should().Be("1");
    }

    [Theory]
    [InlineData("OpenAi", "0.0002")]
    [InlineData("Anthropic", "0.00009")]
    public void CacheSavings_UsesSameIndependentMeterAllocation(string provider, string expected)
    {
        var input = Fixture(
            provider,
            1000,
            100,
            200,
            provider == "Anthropic" ? 100 : 0,
            provider == "Anthropic" ? 40 : 0
        );
        input = input with { Events = [input.Events[0] with { CacheSavingsUsd = 99m }] };
        new AuditVerifier()
            .Verify(input)
            .Findings.Single(f => f.Field == "cacheSavingsUsd")
            .Expected!.Value.Should()
            .Be(expected);
    }

    [Fact]
    public void MissingApplication_DeclaredFallbackCanCalculateWithoutClaimingHistoricalVerification()
    {
        var input = Fixture();
        var e = input.Events[0];
        input = input with
        {
            Events =
            [
                e with
                {
                    SourceDimensions = e.PricingApplication!.Dimensions,
                    PricingApplication = null,
                    CostUsd = 0.0038m,
                },
            ],
        };
        var result = new AuditVerifier().Verify(input);
        result.Coverage.ComparedGroups.Should().Be(1);
        result.Findings.Should().BeEmpty();
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "declared_catalog_fallback");
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void ClaudeDatedModel_UsesTheExplicitlyAppliedPrefixEntry()
    {
        var input = Fixture("Anthropic", 1000, 100, 200, 100, 40);
        input = input with { Events = [input.Events[0] with { Model = "model-20261020" }] };
        new AuditVerifier().Verify(input).Findings.Single().Expected!.Value.Should().Be("0.00451");
    }

    [Fact]
    public void AboveJavascriptPrecision_RemainsExactInEquation()
    {
        var input = Fixture("OpenAi", 9007199254740993, 0, 0);
        new AuditVerifier().Verify(input).Findings.Single().Expected!.Value.Should().Be("18014398509.481986");
    }

    [Theory]
    [InlineData("batch", "standard", "global", "0.5", "1", "0.002255")]
    [InlineData("standard", "fast", "global", "6", "1", "0.02706")]
    [InlineData("standard", "standard", "us", "1", "1.1", "0.004961")]
    public void ClaudeServiceLanes_UseDeclaredMetersAndGeography(
        string tier,
        string speed,
        string geo,
        string rateMultiplier,
        string regionMultiplier,
        string expected
    )
    {
        var input = Fixture("Anthropic", 1000, 100, 200, 100, 40);
        var dimensions = input.Events[0].PricingApplication!.Dimensions with
        {
            SourceServiceTier = tier,
            Speed = speed,
            InferenceGeo = geo,
            Batch = tier == "batch",
        };
        var multiplier = decimal.Parse(rateMultiplier, System.Globalization.CultureInfo.InvariantCulture);
        var region = decimal.Parse(regionMultiplier, System.Globalization.CultureInfo.InvariantCulture);
        input = input with
        {
            Events =
            [
                input.Events[0] with
                {
                    PricingApplication = input.Events[0].PricingApplication! with { Dimensions = dimensions },
                },
            ],
            PricingCatalogs =
            [
                input.PricingCatalogs[0] with
                {
                    Rates = input
                        .PricingCatalogs[0]
                        .Rates.Select(r =>
                            r with
                            {
                                Dimensions = dimensions,
                                UnitPrice = r.UnitPrice * multiplier,
                                RegionMultiplier = region,
                            }
                        )
                        .ToArray(),
                },
            ],
        };
        var result = new AuditVerifier().Verify(input);
        result.Findings.Single().Expected!.Value.Should().Be(expected);
        result.Checks.Single().PricingTraces.Should().Contain(t => t.UnitTokens == 1000000 && t.Multiplier == region);
    }

    [Fact]
    public void StalePricingSource_QualifiesCoverageWithoutSuppressingArithmetic()
    {
        var input = Fixture();
        input = input with
        {
            SourceStatuses =
            [
                new(
                    "pricing",
                    true,
                    true,
                    3600,
                    new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
                    new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
                    null,
                    null,
                    2,
                    "source_error"
                ),
            ],
        };
        var result = new AuditVerifier().Verify(input);
        result.Findings.Should().Contain(f => f.RuleCode == "pricing_cost_difference");
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "pricing_source_stale");
    }

    internal static AuditInput Fixture(
        string provider = "OpenAi",
        long tokens = 1000,
        long output = 100,
        long read = 200,
        long writes = 0,
        long hour = 0
    )
    {
        var dimensions = new PricingDimensions(ServiceTier.Standard, false, null, null)
        {
            Processing = "standard",
            Context = "standard",
            SourceServiceTier = "standard",
            Speed = "standard",
            InferenceGeo = "global",
            LongContext = false,
        };
        var e = CheckFixture.Event() with
        {
            Provider = provider,
            Model = "model",
            Tokens = new(tokens, output, read, writes, hour, 0),
            CostUsd = 99m,
            CacheSavingsUsd = null,
            PricingApplication = new("catalog", CheckFixture.Digest, "entry", dimensions, CheckFixture.Day)
            {
                Kind = "catalog",
                RuleVersion = "1",
            },
            CacheDurations = new(Math.Max(0, writes - hour), hour),
        };
        FrozenPriceRate Rate(PriceMeter meter, decimal price) =>
            new(
                meter.ToString(),
                "model",
                [],
                CheckFixture.Day,
                null,
                true,
                meter,
                dimensions,
                null,
                1_000_000,
                price,
                null
            )
            {
                CatalogEntryKey = "entry",
            };
        return CheckFixture.Input(e) with
        {
            Scope = CheckFixture.Input(e).Scope with { RequestedChecks = [CheckCode.PricingArithmetic] },
            PricingCatalogs =
            [
                new(
                    "pricing",
                    "catalog",
                    CheckFixture.Digest,
                    provider,
                    "USD",
                    e.ObservedAt,
                    [
                        Rate(PriceMeter.Input, 2m),
                        Rate(PriceMeter.Output, 20m),
                        Rate(PriceMeter.CacheRead, 1m),
                        Rate(PriceMeter.CacheWrite5m, 2.5m),
                        Rate(PriceMeter.CacheWrite1h, 4m),
                    ]
                ),
            ],
        };
    }
}
