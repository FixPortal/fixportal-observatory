using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class BilledReportingEndpointsTests : IAsyncLifetime
{
    private readonly AiObservatoryApiFactory _factory = new();

    public async ValueTask InitializeAsync() => await _factory.InitializeAsync();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Reporting_aggregates_every_signed_entry_and_resolves_vendor_names_server_side()
    {
        var ct = TestContext.Current.CancellationToken;
        Guid anthropicId;
        Guid openAiId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            anthropicId = await db.SpendVendors.Where(v => v.Key == "anthropic").Select(v => v.Id).SingleAsync(ct);
            openAiId = await db.SpendVendors.Where(v => v.Key == "openai").Select(v => v.Id).SingleAsync(ct);
            var categoryId = await db.SpendCategories.Select(c => c.Id).FirstAsync(ct);
            var recordedAt = Instant.FromUtc(2026, 8, 2, 12, 0);

            db.SpendEntries.AddRange(
                Enumerable
                    .Range(0, 5001)
                    .Select(_ => Spend(anthropicId, categoryId, new LocalDate(2026, 8, 1), 1m, recordedAt))
            );
            db.SpendEntries.Add(Spend(anthropicId, categoryId, new LocalDate(2026, 8, 2), -10m, recordedAt));
            db.SpendEntries.Add(Spend(openAiId, categoryId, new LocalDate(2026, 8, 2), 20m, recordedAt));
            await db.SaveChangesAsync(ct);
        }

        using var client = _factory.CreateReadOnlyClient();
        var response = await client.GetAsync("/api/spend/reporting?from=2026-08-01&to=2026-08-02", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("entryCount").GetInt32().Should().Be(5003);
        body.GetProperty("totalGbp").GetDecimal().Should().Be(5011m);
        body.GetProperty("dailyAverageGbp").GetDecimal().Should().Be(2505.5m);
        body.GetProperty("projectedMonthlyGbp").GetDecimal().Should().Be(75165m);
        body.GetProperty("topVendorName").GetString().Should().Be("Anthropic");
        body.GetProperty("topVendorGbp").GetDecimal().Should().Be(4991m);
        body.GetProperty("dailySeries")
            .EnumerateArray()
            .Select(Point)
            .Should()
            .Equal(("2026-08-01", 5001m), ("2026-08-02", 10m));
        body.GetProperty("vendorSeries")
            .EnumerateArray()
            .Select(VendorPoint)
            .Should()
            .BeEquivalentTo([("Anthropic", 4991m), ("OpenAI", 20m)]);
        body.GetProperty("vendorSeries")
            .EnumerateArray()
            .Single(point => point.GetProperty("name").GetString() == "Anthropic")
            .TryGetProperty("provider", out var provider)
            .Should()
            .BeTrue();
        provider.GetString().Should().Be("anthropic");
    }

    private static (string Date, decimal Amount) Point(JsonElement point) =>
        (point.GetProperty("date").GetString()!, point.GetProperty("amountGbp").GetDecimal());

    private static (string Name, decimal Amount) VendorPoint(JsonElement point) =>
        (point.GetProperty("name").GetString()!, point.GetProperty("amountGbp").GetDecimal());

    private static SpendEntry Spend(
        Guid vendorId,
        Guid categoryId,
        LocalDate occurredOn,
        decimal amountGbp,
        Instant recordedAt
    ) =>
        new()
        {
            VendorId = vendorId,
            CategoryId = categoryId,
            OccurredOn = occurredOn,
            Amount = amountGbp,
            AmountGbp = amountGbp,
            Currency = "GBP",
            FxRate = 1m,
            Source = SpendSource.Manual,
            RecordedAt = recordedAt,
            ObservedAt = recordedAt,
            CostBasis = CostBasis.Billed,
        };
}
