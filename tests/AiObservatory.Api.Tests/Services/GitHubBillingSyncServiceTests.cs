using System.Text.Json;
using AiObservatory.Api.Services.GitHub;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public sealed class GitHubBillingSyncServiceTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 30, 9, 0);
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(disposable => disposable.Dispose());

    [Theory]
    [InlineData("actions", "github-actions", "ci")]
    [InlineData("packages", "github", "cloud")]
    [InlineData("code_quality", "github", "code-review")]
    [InlineData("ghas", "github", "subscription")]
    [InlineData("some_new_product", "github", "subscription")]
    public async Task MapsProductsWithoutChangingTheirBillingFacts(
        string product,
        string expectedVendor,
        string expectedCategory
    )
    {
        var writes = new List<CapturedWrite>();
        var sut = Create(ClientReturning(Item(product, "sku-a", 10m)), Writer(writes));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var call = writes.Should().ContainSingle().Which;
        call.VendorKey.Should().Be(expectedVendor);
        call.CategoryKey.Should().Be(expectedCategory);
        call.Observation.ProviderKey.Should().Be("github");
        call.Observation.SourceId.Should().Be(UsageSourceIds.GitHubBillingApi);
        call.Observation.SourceKind.Should().Be(SourceKind.ProviderApi);
        call.Observation.UsageScope.Should().Be(UsageScope.Mixed);
        call.Observation.CostBasis.Should().Be(CostBasis.Billed);
        call.Observation.OccurredOn.Should().Be(new LocalDate(2026, 7, 1));
        call.Observation.BillingPeriod.Should().Be("2026-07");
        call.Observation.Service.Should().Be(product);
        call.Observation.Sku.Should().Be("sku-a");
        call.Observation.Currency.Should().Be("USD");
        call.Observation.GrossAmount.Should().Be(10m);
        call.Observation.CreditAmount.Should().Be(0m);
        call.Observation.NetAmount.Should().Be(10m);
        call.Observation.ObservedAt.Should().Be(Now);
        using var raw = JsonDocument.Parse(call.Observation.RawPayload);
        raw.RootElement.GetProperty("product").GetString().Should().Be(product);
        raw.RootElement.GetProperty("netAmount").GetDecimal().Should().Be(10m);
    }

    [Fact]
    public async Task RecordsGrossAndDiscountAlongsideNetLikeTheGoogleArm()
    {
        // A10: gross was previously recorded as net with a zero credit, so gross-versus-credit
        // views understated GitHub and lost the included-allowance data. The ledger invariant
        // is Gross + Credit = Net, so the positive discount lands as a negative credit.
        var writes = new List<CapturedWrite>();
        var sut = Create(
            ClientReturning(Item("actions", "linux", 12.0141527m, grossAmount: 15m, discountAmount: 2.9858473m)),
            Writer(writes)
        );

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var observation = writes.Should().ContainSingle().Which.Observation;
        observation.GrossAmount.Should().Be(15m);
        observation.CreditAmount.Should().Be(-2.9858473m);
        observation.NetAmount.Should().Be(12.0141527m);
        (observation.GrossAmount + observation.CreditAmount)
            .Should()
            .Be(observation.NetAmount, "the database enforces gross + credit = net");
        using var raw = JsonDocument.Parse(observation.RawPayload);
        raw.RootElement.GetProperty("grossAmount").GetDecimal().Should().Be(15m);
        raw.RootElement.GetProperty("discountAmount").GetDecimal().Should().Be(2.9858473m);
    }

    [Fact]
    public async Task AggregatesRepositoriesButKeepsDifferentSkusApart()
    {
        var writes = new List<CapturedWrite>();
        var sut = Create(
            ClientReturning(
                Item("actions", "linux", 10m, day: 1),
                Item("actions", "linux", 15m, day: 2),
                Item("actions", "windows", 20m)
            ),
            Writer(writes)
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(2);
        writes
            .Select(write => (write.Observation.Sku, write.Observation.NetAmount))
            .Should()
            .BeEquivalentTo([("linux", 25m), ("windows", 20m)]);
    }

    [Fact]
    public async Task RetainsZeroNetEvidenceWithoutCountingItAsLedgerSpend()
    {
        var writes = new List<CapturedWrite>();
        var sut = Create(ClientReturning(Item("actions", "included", 0m)), Writer(writes));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(0);
        writes.Should().ContainSingle().Which.Observation.NetAmount.Should().Be(0m);
    }

    [Fact]
    public async Task KeepsRefundsAndCountsCorrectionsToZeroAsAChange()
    {
        var refundWrites = new List<CapturedWrite>();
        var refund = Create(ClientReturning(Item("actions", "credit", -20m)), Writer(refundWrites));

        (await refund.SyncAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        refundWrites.Single().Observation.NetAmount.Should().Be(-20m);

        var correctionWrites = new List<CapturedWrite>();
        var correction = Create(
            ClientReturning(Item("actions", "included", 0m)),
            Writer(correctionWrites, _ => BillingWriteDisposition.Corrected)
        );
        (await correction.SyncAsync(TestContext.Current.CancellationToken))
            .Should()
            .Be(1, "a correction to zero removes an earlier ledger row");
    }

    [Fact]
    public async Task ExactReplayAndNoUsageReportNothingWritten()
    {
        var replayWrites = new List<CapturedWrite>();
        var replay = Create(
            ClientReturning(Item("actions", "linux", 10m)),
            Writer(replayWrites, _ => BillingWriteDisposition.Unchanged)
        );
        (await replay.SyncAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        var emptyWrites = new List<CapturedWrite>();
        var empty = Create(ClientReturning(), Writer(emptyWrites));
        (await empty.SyncAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        emptyWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchesThePreviousAndCurrentCalendarYears()
    {
        var client = ClientReturning(Item("actions", "linux", 10m));
        var sut = Create(client, Writer([]));

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        await client.Received(1).GetUsageAsync(2025, Arg.Any<CancellationToken>());
        await client.Received(1).GetUsageAsync(2026, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectedLinesFailTheSyncAfterTheRestOfTheBillIsProcessed()
    {
        var writes = new List<CapturedWrite>();
        var writer = Writer(
            writes,
            observation =>
                observation.Sku == "linux"
                    ? throw new InvalidOperationException("test rejection")
                    : BillingWriteDisposition.Created
        );
        var sut = Create(ClientReturning(Item("actions", "linux", 10m), Item("code_quality", "quality", 20m)), writer);

        var act = () => sut.SyncAsync(TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<AggregateException>();
        thrown.Which.InnerExceptions.Should().ContainSingle().Which.Message.Should().Be("test rejection");
        writes.Select(write => write.Observation.Sku).Should().Equal("linux", "quality");
    }

    [Fact]
    public async Task CallerCancellationIsNeverTurnedIntoAPerLineSkip()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var writer = Writer([], _ => throw new OperationCanceledException(token));
        await cancellation.CancelAsync();
        var sut = Create(ClientReturning(Item("actions", "linux", 10m)), writer);

        var act = () => sut.SyncAsync(token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(token);
    }

    [Fact]
    public async Task OverlongSkusUseDistinctHashedObservationKeys()
    {
        var writes = new List<CapturedWrite>();
        var prefix = new string('x', 300);
        var sut = Create(
            ClientReturning(Item("actions", prefix + "a", 10m), Item("actions", prefix + "b", 20m)),
            Writer(writes)
        );

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var keys = writes.Select(write => write.Observation.ObservationKey).ToList();
        keys.Should().OnlyContain(key => key.Length <= 200);
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task MissingProductOrSkuStaysInsideThePerLineGuard()
    {
        // A3: System.Text.Json binds an omitted `product`/`sku` to null despite the
        // non-nullable record members, and a null ProductMap key used to throw
        // ArgumentNullException outside the try — one malformed line aborted the whole sync.
        var writes = new List<CapturedWrite>();
        var sut = Create(ClientReturning(Item(null!, null!, 10m), Item("actions", "linux", 20m)), Writer(writes));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(2);
        writes.Select(write => (write.Observation.Service, write.Observation.Sku)).Should().Contain(("", ""));
    }

    [Fact]
    public async Task PlainSegmentsKeepTheLegacyObservationKeyFormByteIdentical()
    {
        // O2: every stored row carries github:{month}:{product}:{sku} and the writer matches on
        // the key verbatim, so length-prefixing unconditionally orphaned the whole ledger and
        // inserted a second observation and spend row per month on the first sync after deploy.
        var writes = new List<CapturedWrite>();
        var sut = Create(ClientReturning(Item("actions", "linux", 10m)), Writer(writes));

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        writes.Select(write => write.Observation.ObservationKey).Should().Equal("github:2026-07:actions:linux");
    }

    [Fact]
    public async Task ColonBearingSegmentsCannotCollideObservationKeys()
    {
        // A9: a bare ':' delimiter let product="a:b"/sku="c" and product="a"/sku="b:c" share
        // one key and overwrite each other. Only colon-bearing segments are length-prefixed.
        var writes = new List<CapturedWrite>();
        var sut = Create(ClientReturning(Item("a:b", "c", 10m), Item("a", "b:c", 20m)), Writer(writes));

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        writes.Select(write => write.Observation.ObservationKey).Should().OnlyHaveUniqueItems();
    }

    private static GitHubBillingSyncService Create(GitHubBillingClient client, BillingObservationWriter writer) =>
        new(client, writer, new FakeClock(Now), NullLogger<GitHubBillingSyncService>.Instance);

    private GitHubBillingClient ClientReturning(params GitHubBillingUsageItem[] items)
    {
        var http = new HttpClient();
        _disposables.Add(http);
        var client = Substitute.For<GitHubBillingClient>(http, "FixPortal", NullLogger<GitHubBillingClient>.Instance);
        client.GetUsageAsync(2025, Arg.Any<CancellationToken>()).Returns([]);
        client.GetUsageAsync(2026, Arg.Any<CancellationToken>()).Returns(items);
        return client;
    }

    private BillingObservationWriter Writer(
        List<CapturedWrite> writes,
        Func<BillingObservation, BillingWriteDisposition>? disposition = null
    )
    {
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AiObservatoryDbContext(options);
        var http = new HttpClient();
        var cache = new MemoryCache(new MemoryCacheOptions());
        _disposables.Add(db);
        _disposables.Add(http);
        _disposables.Add(cache);
        var fx = Substitute.For<FxRateProvider>(http, cache, NullLogger<FxRateProvider>.Instance);
        var writer = Substitute.For<BillingObservationWriter>(db, fx, new FakeClock(Now));
        writer
            .RecordAsync(
                Arg.Any<BillingObservation>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                var observation = call.ArgAt<BillingObservation>(0);
                writes.Add(new CapturedWrite(observation, call.ArgAt<string>(1), call.ArgAt<string>(2)));
                return disposition?.Invoke(observation) ?? BillingWriteDisposition.Created;
            });
        return writer;
    }

    private static GitHubBillingUsageItem Item(
        string product,
        string sku,
        decimal netAmount,
        int month = 7,
        int day = 1,
        decimal? grossAmount = null,
        decimal discountAmount = 0m
    ) => new(new DateOnly(2026, month, day), product, sku, grossAmount ?? netAmount, discountAmount, netAmount);

    private sealed record CapturedWrite(BillingObservation Observation, string VendorKey, string CategoryKey);
}
