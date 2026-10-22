using System.Diagnostics;
using System.Text;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Serialization;

public sealed class ContractMetadataTests(ITestOutputHelper output)
{
    /// <summary>Repeated nullable facts must not recreate reflection metadata for every value.</summary>
    [Fact]
    public void WarmValidation_BoundsAllocationForRepeatedNullableFacts()
    {
        var rates = Enumerable.Repeat(Rate(), 1024).ToArray();
        AuditContractValidation.Validate(rates[0]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        AuditContractValidation.Validate(rates);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine(
            $"Nullable-rate validation: {allocated} allocated bytes; {watch.Elapsed.TotalMilliseconds:F3} ms."
        );
        // A generous per-fact allocation budget permits boxing/iteration while rejecting repeated
        // construction and attribute analysis of the same nullable-property metadata. No timing gate.
        allocated.Should().BeLessThan(2048L * rates.Length);
    }

    /// <summary>Metadata reuse cannot change canonical nulls, optional-field omission, or numeric encoding.</summary>
    [Fact]
    public void NullableRate_RetainsCanonicalBytes()
    {
        const string expected = """
            {"aliases":[],"contextThreshold":null,"dimensions":{"batch":false,"contextTokens":null,"inferenceGeo":null,"region":null,"sourceServiceTier":null,"speed":null,"tier":"standard"},"effectiveDateIsProviderDeclared":false,"effectiveFrom":"2026-10-21","effectiveTo":null,"id":"rate","meter":"input","model":"synthetic-model","regionMultiplier":null,"unitPrice":"1.25","unitTokens":1000000}
            """;
        var bytes = Encoding.UTF8.GetBytes(expected);
        AuditCanonicalJson.Serialize(Rate()).Should().Equal(bytes);
        AuditCanonicalJson.Serialize(AuditCanonicalJson.Deserialize<FrozenPriceRate>(bytes)).Should().Equal(bytes);
    }

    /// <summary>Equal property names on different contracts retain their own nullable annotations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SamePropertyName_DoesNotShareNullabilityAcrossTypes(bool requiredFirst)
    {
        var nullable = CanonicalJsonTests.Record() with { Model = null };
        var required = Rate() with { Model = null! };
        Action rejectRequired = () => AuditCanonicalJson.Serialize(required);
        if (requiredFirst)
        {
            rejectRequired.Should().Throw<AuditValidationException>().Which.Code.Should().Be("required_value");
        }
        AuditCanonicalJson.Serialize(nullable).Should().NotBeEmpty();
        rejectRequired.Should().Throw<AuditValidationException>().Which.Code.Should().Be("required_value");
        AuditCanonicalJson.Serialize(nullable).Should().NotBeEmpty();
    }

    /// <summary>Concurrent readers cannot publish incomplete metadata or weaken required-value validation.</summary>
    [Fact]
    public async Task ConcurrentValidation_PreservesNullableAndRequiredSemantics()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable
            .Range(0, 16)
            .Select(async _ =>
            {
                await start.Task;
                for (var iteration = 0; iteration < 16; iteration++)
                {
                    var valid = CanonicalJsonTests.Record() with { Model = null };
                    var bytes = AuditCanonicalJson.Serialize(valid);
                    AuditCanonicalJson.Deserialize<SourceUsageRecord>(bytes).Model.Should().BeNull();
                    Action invalid = () => AuditCanonicalJson.Serialize(Rate() with { Model = null! });
                    invalid.Should().Throw<AuditValidationException>().Which.Code.Should().Be("required_value");
                }
            })
            .ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);
    }

    private static FrozenPriceRate Rate() =>
        new(
            "rate",
            "synthetic-model",
            [],
            new(2026, 10, 21),
            null,
            false,
            PriceMeter.Input,
            new(ServiceTier.Standard, false, null, null),
            null,
            1_000_000,
            1.25m,
            null
        );
}
