using AiObservatory.Api.Endpoints;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

/// <summary>
/// The IDE event body bound is enforced on two separate paths: the stream-level cap in
/// <c>ReadBoundedAsync</c> (what a POST hits first, mapped to 413) and the length check in
/// <c>ParseEvent</c>. Each needs its own boundary test; a POST over the cap never reaches
/// ParseEvent.
/// </summary>
public sealed class IdeEventBodyBoundTests
{
    private const int Bound = 65_536;

    private static readonly Instant Now = Instant.FromUtc(2026, 8, 4, 12, 1);

    // Trailing whitespace keeps the envelope valid under the closed contract (no unknown members).
    private static byte[] ValidEnvelopePaddedTo(int length)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing.decided.v1.json"));
        var padded = new byte[length];
        Array.Fill(padded, (byte)' ');
        bytes.CopyTo(padded, 0);
        return padded;
    }

    [Fact]
    public void ParseEvent_AcceptsABodyOfExactlyTheBound()
    {
        var envelope = IdeEndpoints.ParseEvent(ValidEnvelopePaddedTo(Bound), Now);

        envelope.EventType.Should().Be("routing.decided");
    }

    [Fact]
    public void ParseEvent_RejectsABodyOneByteOverTheBound()
    {
        var parse = () => IdeEndpoints.ParseEvent(ValidEnvelopePaddedTo(Bound + 1), Now);

        parse.Should().Throw<InvalidDataException>().WithMessage("*bound*");
    }

    [Fact]
    public async Task ReadBoundedAsync_ReturnsABodyOfExactlyTheBound()
    {
        using var stream = new MemoryStream(new byte[Bound]);

        var body = await IdeEndpoints.ReadBoundedAsync(stream, TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body.Should().HaveCount(Bound);
    }

    [Fact]
    public async Task ReadBoundedAsync_ReturnsNullForABodyOneByteOverTheBound()
    {
        using var stream = new MemoryStream(new byte[Bound + 1]);

        var body = await IdeEndpoints.ReadBoundedAsync(stream, TestContext.Current.CancellationToken);

        body.Should().BeNull();
    }
}
