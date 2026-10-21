using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.GitHub;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiObservatory.Ingest.Tests.Services;

/// <summary>
/// Which repositories a cycle polls: the configured allowlist when set, otherwise every
/// non-archived repo discovered in the configured organisation.
/// </summary>
public class GitHubRepositoryResolutionTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 9, 15, 12, 0);
    private static readonly LocalDate PollDate = new(2026, 9, 15);
    private static readonly GitHubBackfillStatus FullyBackfilled = new(true, true, true, true);

    [Fact]
    public async Task An_explicit_allowlist_overrides_organisation_discovery()
    {
        // The allowlist is the escape hatch for pinning a subset, or for watching repos
        // outside the org that enumeration cannot reach. It must win outright.
        var client = QuietClient();
        var sut = Service(client, Options(allowlist: ["fix-portal/pinned"], org: "FixPortal"));

        await sut.IngestAsync(PollDate, PollDate, TestContext.Current.CancellationToken);

        await client.DidNotReceive().ListOrganizationRepositoriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client
            .Received(1)
            .GetPullRequestsAsync("fix-portal/pinned", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_no_allowlist_every_discovered_repository_is_polled()
    {
        var client = QuietClient();
        client
            .ListOrganizationRepositoriesAsync("FixPortal", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(["FixPortal/one", "FixPortal/two"]);
        var sut = Service(client, Options(allowlist: [], org: "FixPortal"));

        await sut.IngestAsync(PollDate, PollDate, TestContext.Current.CancellationToken);

        await client
            .Received(1)
            .GetPullRequestsAsync("fixportal/one", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
        await client
            .Received(1)
            .GetPullRequestsAsync("fixportal/two", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_enumeration_reports_the_source_unavailable_rather_than_polling_nothing()
    {
        // The whole point. An empty repo list would run a cycle that polls nothing, fails
        // nothing and reports success, leaving the source "fresh" while observing no
        // repositories — the shape of the twelve-day outage this ingest already suffered.
        var client = QuietClient();
        client
            .ListOrganizationRepositoriesAsync("FixPortal", Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("403 Forbidden"));
        var sut = Service(client, Options(allowlist: [], org: "FixPortal"));

        var act = async () => await sut.IngestAsync(PollDate, PollDate, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<SourceUnavailableException>()).WithMessage("*403 Forbidden*");
    }

    [Fact]
    public async Task An_organisation_with_no_repositories_reports_unavailable()
    {
        var client = QuietClient();
        client
            .ListOrganizationRepositoriesAsync("FixPortal", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>([]);
        var sut = Service(client, Options(allowlist: [], org: "FixPortal"));

        var act = async () => await sut.IngestAsync(PollDate, PollDate, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SourceUnavailableException>();
    }

    [Fact]
    public async Task Neither_an_allowlist_nor_an_organisation_reports_unavailable()
    {
        var sut = Service(QuietClient(), Options(allowlist: [], org: null));

        var act = async () => await sut.IngestAsync(PollDate, PollDate, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SourceUnavailableException>();
    }

    private static IOptions<IngestOptions> Options(string[] allowlist, string? org) =>
        Microsoft.Extensions.Options.Options.Create(
            new IngestOptions { GitHubRepoAllowlist = allowlist, GitHubActivityOrg = org }
        );

    /// <summary>A client that answers every per-repo call with nothing, so these tests are
    /// about which repos are asked for, not what comes back.</summary>
    private static IGitHubActivityClient QuietClient()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        client.GetPullRequestsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                Arg.Any<string>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));
        return client;
    }

    private static GitHubIngestionService Service(IGitHubActivityClient client, IOptions<IngestOptions> options)
    {
        var repository = Substitute.For<IGitHubActivityRepository>();
        repository.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        return new GitHubIngestionService(
            client,
            repository,
            options,
            NullLogger<GitHubIngestionService>.Instance,
            new FakeClock(FixedNow)
        );
    }
}
