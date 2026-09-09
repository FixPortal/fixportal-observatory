using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class AdversarialReviewServiceTests
{
    private readonly IAdversarialReviewRepository _repo = Substitute.For<IAdversarialReviewRepository>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 14, 10, 0));

    private AdversarialReviewService CreateSut() => new(_repo, _clock);

    private static AdversarialReviewRunRequest ValidRequest(
        string reviewer = "anthropic",
        string model = "claude-sonnet-4-6",
        int issuesRaised = 5,
        int issuesAccepted = 3,
        decimal costUsd = 0.12m,
        string runId = "2026-06-14T10:00:00Z",
        string role = "reviewer",
        string? repo = "fixportal-engine",
        string? summary = null
    ) =>
        new(
            EventType: "adversarial-review-run",
            Reviewer: reviewer,
            Model: model,
            InputTokens: 1000,
            OutputTokens: 500,
            CostUsd: costUsd,
            ReviewDurationMs: 12345,
            IssuesRaised: issuesRaised,
            IssuesAccepted: issuesAccepted,
            RunId: runId,
            Role: role,
            Repo: repo,
            Summary: summary
        );

    [Fact]
    public async Task RecordRun_valid_request_stores_run_and_returns_created()
    {
        var newId = Guid.NewGuid();
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((newId, Existed: false));

        var result = await CreateSut().RecordRunAsync(ValidRequest(), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(201);
        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r =>
                    r != null
                    && r.Reviewer == "anthropic"
                    && r.Model == "claude-sonnet-4-6"
                    && r.CostPerAcceptedFinding == 0.12m / 3m
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecordRun_existing_participant_returns_ok_corrected_in_place()
    {
        var existingId = Guid.NewGuid();
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((existingId, Existed: true));

        var result = await CreateSut().RecordRunAsync(ValidRequest(), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task RecordRun_passes_chunk_count_through_to_repository()
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut().RecordRunAsync(ValidRequest() with { ChunkCount = 21 }, CancellationToken.None);

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.ChunkCount == 21),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecordRun_rejects_non_positive_chunk_count()
    {
        var result = await CreateSut().RecordRunAsync(ValidRequest() with { ChunkCount = 0 }, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(5, 3, 0.12, 0.04)]
    [InlineData(5, 1, 0.10, 0.10)]
    [InlineData(0, 0, 0.05, null)]
    [InlineData(3, 0, 0.05, null)]
    public async Task RecordRun_computes_cost_per_accepted_finding_correctly(
        int raised,
        int accepted,
        double costRaw,
        double? expectedRaw
    )
    {
        var cost = (decimal)costRaw;
        decimal? expected = expectedRaw.HasValue ? (decimal)expectedRaw.Value : null;

        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut()
            .RecordRunAsync(
                ValidRequest(issuesRaised: raised, issuesAccepted: accepted, costUsd: cost),
                CancellationToken.None
            );

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.CostPerAcceptedFinding == expected),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null!)]
    public async Task RecordRun_missing_reviewer_returns_bad_request(string? reviewer)
    {
        var req = ValidRequest(reviewer: reviewer!);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_accepts_a_reviewer_slug_outside_the_original_four_vendors()
    {
        // The reviewer set is whichever CLIs the operator runs; a hardcoded vendor allowlist
        // would 400 a new reviewer until a code change shipped. Shape is validated instead.
        var newId = Guid.NewGuid();
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((newId, Existed: false));

        var result = await CreateSut().RecordRunAsync(ValidRequest(reviewer: "moonsh0t"), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(201);
        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Reviewer == "moonsh0t"),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData("bad reviewer")] // whitespace is prose, not a slug
    [InlineData("vendor_x")] // underscore is not in the slug alphabet
    [InlineData("vendor/x")] // path separator
    [InlineData("-anthropic")] // must start with a letter
    [InlineData("anthropic!")]
    public async Task RecordRun_rejects_a_reviewer_that_is_not_a_vendor_slug(string reviewer)
    {
        var result = await CreateSut().RecordRunAsync(ValidRequest(reviewer: reviewer), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null!)]
    public async Task RecordRun_missing_run_id_returns_bad_request(string? runId)
    {
        var req = ValidRequest(runId: runId!);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_issues_accepted_exceeds_raised_is_allowed()
    {
        // Cross-examination can credit a reviewer for findings they did not
        // raise in Phase 1 (unanimous consensus). IssuesAccepted > IssuesRaised
        // is valid and must not be rejected.
        var newId = Guid.NewGuid();
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((newId, Existed: false));

        var req = ValidRequest(issuesRaised: 0, issuesAccepted: 2);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(201);
        await _repo.Received(1).RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_negative_cost_returns_bad_request()
    {
        var req = ValidRequest(costUsd: -0.01m);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_normalises_reviewer_to_lowercase()
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut().RecordRunAsync(ValidRequest(reviewer: "Anthropic"), CancellationToken.None);

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Reviewer == "anthropic"),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecordRun_stamps_recorded_at_from_clock()
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut().RecordRunAsync(ValidRequest(), CancellationToken.None);

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.RecordedAt == _clock.GetCurrentInstant()),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData("sonnet", "claude-sonnet-4-6")]
    [InlineData("opus", "claude-opus-4-8")]
    [InlineData("claude-sonnet", "claude-sonnet-4-6")]
    [InlineData("gpt-5.4", "gpt-5.4")] // unknown-to-map passes through
    [InlineData("gemini-2.5-pro", "gemini-2.5-pro")] // passes through
    public async Task RecordRun_normalises_model_id(string input, string expected)
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut().RecordRunAsync(ValidRequest(model: input), CancellationToken.None);

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Model == expected),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData("sonnet")] // non-anthropic reviewer must NOT get the alias resolved
    [InlineData("opus")]
    [InlineData("haiku")]
    public async Task RecordRun_does_not_alias_bare_model_for_non_anthropic_reviewer(string model)
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut().RecordRunAsync(ValidRequest(reviewer: "openai", model: model), CancellationToken.None);

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Model == model),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    public async Task RecordRun_judge_with_non_zero_issues_returns_bad_request(int raised, int accepted)
    {
        var req = ValidRequest(role: "judge", model: "claude-opus-4-8", issuesRaised: raised, issuesAccepted: accepted);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_judge_with_zero_issues_is_accepted()
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        var result = await CreateSut()
            .RecordRunAsync(
                ValidRequest(
                    role: "judge",
                    model: "claude-opus-4-8",
                    issuesRaised: 0,
                    issuesAccepted: 0,
                    costUsd: 0.83m
                ),
                CancellationToken.None
            );

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(201);
        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Role == "judge" && r.Repo == "fixportal-engine"),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecordRun_trims_and_stores_summary()
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut()
            .RecordRunAsync(ValidRequest(summary: "  Verifying adjusted formatting  "), CancellationToken.None);

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Summary == "Verifying adjusted formatting"),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null!)]
    public async Task RecordRun_blank_summary_stored_as_null(string? summary)
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut().RecordRunAsync(ValidRequest(summary: summary), CancellationToken.None);

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Summary == null),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecordRun_caps_summary_at_80_chars()
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        await CreateSut().RecordRunAsync(ValidRequest(summary: new string('x', 200)), CancellationToken.None);

        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Summary != null && r.Summary.Length == 80),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecordRun_summary_truncation_does_not_split_a_surrogate_pair()
    {
        _repo
            .RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), Existed: false));

        // 79 ASCII + a 2-UTF-16-unit codepoint => the 80-char cut would land mid-pair.
        var input = new string('x', 79) + "\U0001F600";
        await CreateSut().RecordRunAsync(ValidRequest(summary: input), CancellationToken.None);

        // Result is exactly the 79 ASCII chars — the trailing high surrogate was
        // dropped rather than split, so no lone surrogate remains.
        await _repo
            .Received(1)
            .RecordRunAsync(
                Arg.Is<AdversarialReviewRun>(r => r != null && r.Summary == new string('x', 79)),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData("")]
    [InlineData("verifier")]
    [InlineData(null!)]
    public async Task RecordRun_invalid_role_returns_bad_request(string? role)
    {
        var result = await CreateSut().RecordRunAsync(ValidRequest(role: role!), CancellationToken.None);
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }
}
