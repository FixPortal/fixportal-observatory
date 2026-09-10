using System.Text.Json;
using AiObservatory.Api.Services.GitHub;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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
                Item("actions", "linux", 10m, day: 1, grossAmount: 12m, discountAmount: 2m),
                Item("actions", "linux", 15m, day: 2, grossAmount: 19m, discountAmount: 4m),
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
        var linux = writes.Single(write => write.Observation.Sku == "linux").Observation;
        linux.GrossAmount.Should().Be(31m);
        linux.CreditAmount.Should().Be(-6m);
        using var raw = JsonDocument.Parse(linux.RawPayload);
        raw.RootElement.GetProperty("grossAmount").GetDecimal().Should().Be(31m);
        raw.RootElement.GetProperty("discountAmount").GetDecimal().Should().Be(6m);
        raw.RootElement.GetProperty("netAmount").GetDecimal().Should().Be(25m);
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
    public async Task MissingProductOrSkuLandsUnderTheUnknownSentinel()
    {
        // A3: System.Text.Json binds an omitted `product`/`sku` to null despite the
        // non-nullable record members, and a null ProductMap key used to throw
        // ArgumentNullException outside the try — one malformed line aborted the whole sync.
        // The coalesce target is "unknown", NOT "": the production writer rejects blank
        // Service/Sku, so an empty coalesce would die in the per-line catch and escalate to a
        // source failure. The sentinel passes validation, which is what lets this row land.
        var writes = new List<CapturedWrite>();
        var sut = Create(ClientReturning(Item(null!, null!, 10m), Item("actions", "linux", 20m)), Writer(writes));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(2);
        writes
            .Select(write => (write.Observation.Service, write.Observation.Sku))
            .Should()
            .Contain(("unknown", "unknown"));
    }

    [Fact]
    public async Task ConstructsNetFromGrossMinusDiscountAndKeepsGitHubsTripleInRawPayload()
    {
        // M18: the writer enforces Gross + Credit = Net exactly, and three independent sums of
        // GitHub's figures are not guaranteed to balance — a mismatching triple used to throw
        // out of Validate and fail the whole source. The net is constructed instead, GitHub's
        // reported figures stay in RawPayload, and the divergence is logged.
        var writes = new List<CapturedWrite>();
        var logger = new CapturingLogger();
        var sut = Create(
            ClientReturning(Item("actions", "linux", 12.01m, grossAmount: 15m, discountAmount: 2.98m)),
            Writer(writes),
            logger
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var observation = writes.Should().ContainSingle().Which.Observation;
        observation.GrossAmount.Should().Be(15m);
        observation.CreditAmount.Should().Be(-2.98m);
        observation
            .NetAmount.Should()
            .Be(12.02m, "the constructed gross-minus-discount satisfies the ledger invariant");
        (observation.GrossAmount + observation.CreditAmount).Should().Be(observation.NetAmount);
        using var raw = JsonDocument.Parse(observation.RawPayload);
        raw.RootElement.GetProperty("netAmount").GetDecimal().Should().Be(12.01m, "GitHub's own figure is preserved");
        logger
            .Messages.Should()
            .ContainSingle(m => m.Contains("12.01") && m.Contains("12.02") && m.Contains("constructed"));
    }

    [Theory]
    // gross, discount, reportedNet → expected gross, credit, net on the observation
    [InlineData(0, 0, 10, 10, 0, 10)] // absent gross/discount: reported net kept, gross reconstructed
    [InlineData(0, 2, 10, 12, -2, 10)] // absent gross with a reported discount: rebuilt around the net
    [InlineData(15, 3, 12, 15, -3, 12)] // balanced triple: unchanged behaviour
    public async Task AnAbsentGrossAmountKeepsTheReportedNetInsteadOfZeroingTheSpend(
        int grossAmount,
        int discountAmount,
        int reportedNet,
        int expectedGross,
        int expectedCredit,
        int expectedNet
    )
    {
        // The writer treats a zero net as "no spend" and deletes any existing spend row for the
        // key, so a line whose grossAmount GitHub omitted must not construct net = gross -
        // discount = 0: the reported net is kept and the gross reconstructed around it.
        var writes = new List<CapturedWrite>();
        var sut = Create(
            ClientReturning(
                Item("actions", "linux", reportedNet, grossAmount: grossAmount, discountAmount: discountAmount)
            ),
            Writer(writes)
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var observation = writes.Should().ContainSingle().Which.Observation;
        observation.GrossAmount.Should().Be(expectedGross);
        observation.CreditAmount.Should().Be(expectedCredit);
        observation.NetAmount.Should().Be(expectedNet);
        (observation.GrossAmount + observation.CreditAmount)
            .Should()
            .Be(observation.NetAmount, "the database enforces gross + credit = net");
        using var raw = JsonDocument.Parse(observation.RawPayload);
        raw.RootElement.GetProperty("grossAmount")
            .GetDecimal()
            .Should()
            .Be(grossAmount, "GitHub's own figure is preserved verbatim in RawPayload");
        raw.RootElement.GetProperty("netAmount").GetDecimal().Should().Be(reportedNet);
    }

    [Fact]
    public async Task APriorYearOutageDoesNotStopTheCurrentYearBeingFetched()
    {
        // M19: the loop fetches currentYear - 1 first, and a 403/404 there used to abort the
        // sync before the current year was ever requested — zero GitHub spend, forever. Only a
        // current-year failure is a source failure now.
        var client = ClientReturning(Item("actions", "linux", 10m));
        client
            .GetUsageAsync(2025, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IReadOnlyList<GitHubBillingUsageItem>>(
                    new GitHubBillingUnavailableException("prior year invisible to this token")
                )
            );
        var writes = new List<CapturedWrite>();
        var sut = Create(client, Writer(writes));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        writes.Should().ContainSingle();
        await client.Received(1).GetUsageAsync(2026, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACurrentYearOutageIsStillASourceFailure()
    {
        var client = ClientReturning();
        client
            .GetUsageAsync(2026, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IReadOnlyList<GitHubBillingUsageItem>>(
                    new GitHubBillingUnavailableException("current year invisible to this token")
                )
            );
        var sut = Create(client, Writer([]));

        var act = () => sut.SyncAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<GitHubBillingUnavailableException>())
            .Which.Message.Should()
            .Contain("current year");
    }

    [Fact]
    public async Task EveryYearUnavailableIsStillASourceFailure()
    {
        var client = ClientReturning();
        client
            .GetUsageAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IReadOnlyList<GitHubBillingUsageItem>>(
                    new GitHubBillingUnavailableException("token lacks billing read scope")
                )
            );
        var sut = Create(client, Writer([]));

        var act = () => sut.SyncAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GitHubBillingUnavailableException>();
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

    private static GitHubBillingSyncService Create(
        GitHubBillingClient client,
        BillingObservationWriter writer,
        ILogger<GitHubBillingSyncService>? logger = null
    ) => new(client, writer, new FakeClock(Now), logger ?? NullLogger<GitHubBillingSyncService>.Instance);

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

    private sealed class CapturingLogger : ILogger<GitHubBillingSyncService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
