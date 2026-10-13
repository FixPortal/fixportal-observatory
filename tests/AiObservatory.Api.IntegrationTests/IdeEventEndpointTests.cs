using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

[Collection("ApiFactory")]
public sealed class IdeEventEndpointTests(AiObservatoryApiFactory factory)
{
    [Trait("Category", "Integration")]
    [Fact]
    public async Task RecordsAnIdenticalDeliveryOnceAndRejectsConflictingReuse()
    {
        var bytes = await File.ReadAllBytesAsync(
            // GetFullPath(relative, base) keeps the base authoritative even if a segment
            // were rooted; Path.Combine would silently drop the earlier arguments.
            Path.GetFullPath(Path.Combine("Fixtures", "routing.decided.v1.json"), AppContext.BaseDirectory),
            TestContext.Current.CancellationToken
        );
        using var client = factory.CreateIdeClient();

        var first = await client.PostAsync("/api/ide/v1/events", Json(bytes), TestContext.Current.CancellationToken);
        var second = await client.PostAsync("/api/ide/v1/events", Json(bytes), TestContext.Current.CancellationToken);
        var changed = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(bytes).Replace("assisted-v1", "assisted-v2", StringComparison.Ordinal)
        );
        var conflict = await client.PostAsync(
            "/api/ide/v1/events",
            Json(changed),
            TestContext.Current.CancellationToken
        );

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var rows = await db.IdeEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
        rows[0].EventType.Should().Be("routing.decided");
    }

    [Trait("Category", "Integration")]
    [Theory]
    [InlineData("\"partnerId\":{\"value\":\"753cb584-cd0b-4e16-9f08-6c0ce130a84a\"},")]
    [InlineData("\"missionId\":{\"value\":\"11111111-1111-1111-1111-111111111111\"},")]
    public async Task MissingIdentityMemberIsRejectedCleanlyInsteadOfCrashing(string missingMember)
    {
        var json = await File.ReadAllTextAsync(
            // GetFullPath(relative, base) keeps the base authoritative even if a segment
            // were rooted; Path.Combine would silently drop the earlier arguments.
            Path.GetFullPath(Path.Combine("Fixtures", "routing.decided.v1.json"), AppContext.BaseDirectory),
            TestContext.Current.CancellationToken
        );
        var bytes = Encoding.UTF8.GetBytes(json.Replace(missingMember, "", StringComparison.Ordinal));
        using var client = factory.CreateIdeClient();

        var response = await client.PostAsync("/api/ide/v1/events", Json(bytes), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The stream-level cap in ReadBoundedAsync is a separate path from the length check in
    /// ParseEvent: a POST over the bound is answered 413 before parsing, a POST of exactly the
    /// bound is accepted. Padding is insignificant JSON whitespace, so the body stays valid
    /// under the closed contract.
    /// </summary>
    [Trait("Category", "Integration")]
    [Theory]
    [InlineData(65_536, HttpStatusCode.Created)]
    [InlineData(65_537, HttpStatusCode.RequestEntityTooLarge)]
    public async Task BodyIsBoundedAtSixtyFiveThousandFiveHundredThirtySixBytes(int length, HttpStatusCode expected)
    {
        var json = await File.ReadAllTextAsync(
            Path.GetFullPath("Fixtures/routing.decided.v1.json", AppContext.BaseDirectory),
            TestContext.Current.CancellationToken
        );
        var key = $"routing:bound-{Guid.NewGuid():N}";
        var unique = json.Replace("routing:11111111-1111-1111-1111-111111111111", key, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(unique.TrimEnd());
        bytes.Length.Should().BeLessThan(length);
        var padded = new byte[length];
        Array.Fill(padded, (byte)' ');
        bytes.CopyTo(padded, 0);
        using var client = factory.CreateIdeClient();

        try
        {
            var response = await client.PostAsync(
                "/api/ide/v1/events",
                Json(padded),
                TestContext.Current.CancellationToken
            );

            response.StatusCode.Should().Be(expected);
        }
        finally
        {
            // The collection shares one database and the replay test counts every IDE event row,
            // so this test removes the row it created.
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await db.IdeEvents.Where(row => row.IdempotencyKey == key).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private static ByteArrayContent Json(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
