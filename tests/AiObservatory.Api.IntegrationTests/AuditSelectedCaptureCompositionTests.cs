using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AiObservatory.Api.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;
using Npgsql;
using AuditOptions = AiObservatory.Api.Audit.AuditOptions;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>Real CLI, HTTP and PostgreSQL composition; Windows Job termination is qualified separately.</summary>
[Trait("Category", "Integration")]
public sealed class AuditSelectedCaptureCompositionTests
{
    // Catches protocol drift between the real server and client, lost renewal, false completeness,
    // date-prefiltered Codex baselines, generic-claim fallback and collateral capture/run mutations.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedCli_SealsObservedCompleteOrLimitedFactsWithoutChangingOtherEvidence(bool limited)
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = RepositoryRoot();
        var node = NodeExecutable();
        var directory = Directory.CreateTempSubdirectory("aiobs-selected-composition-").FullName;
        var connection = OwnedConnection();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connection, o => o.UseNodaTime())
            .Options;
        await using var owner = new AiObservatoryDbContext(options);
        try
        {
            MakePrivate(directory);
            var fixture = await PrepareAsync(connection, ct);
            var expires = SystemClock.Instance.GetCurrentInstant() + Duration.FromMinutes(5);
            await using var configured = Factory(connection);
            Bind(configured, fixture, expires);
            var trace = new CaptureTrace();
            await using var server = configured.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(trace))
            );
            server.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            server.StartServer();
            var address = server
                .Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            var uri = new Uri(address);
            IPAddress.IsLoopback(IPAddress.Parse(uri.Host)).Should().BeTrue();
            var audit = server.Services.GetRequiredService<IOptions<AuditOptions>>().Value;
            audit.Enabled.Should().BeTrue();
            audit.WorkerEnabled.Should().BeFalse();
            server
                .Services.GetRequiredService<IOptions<AuditSelectedInvocationOptions>>()
                .Value.Enabled.Should()
                .BeFalse();

            var roots = await WriteSourcesAsync(directory, ct);
            var identity = Path.Combine(directory, "identity");
            Directory.CreateDirectory(identity);
            var configPath = await WriteConfigurationAsync(directory, address, fixture, expires, roots, limited, ct);
            var before = await UnrelatedFactsAsync(owner, fixture.RequestId, ct);
            var result = await RunCliAsync(node, repository, configPath, ct);
            result.ExitCode.Should().Be(limited ? 3 : 0);
            result.Output.Should().Be(limited ? "capture_incomplete\n" : "capture_complete\n");
            result.Error.Should().BeEmpty();
            (await UnrelatedFactsAsync(owner, fixture.RequestId, ct)).Should().Be(before);

            var requestPath = $"/api/audits/capture-requests/{fixture.RequestId:D}";
            trace
                .Snapshot()
                .Should()
                .Equal(
                    new Exchange("POST", "/api/audits/capture-requests/selected/claim", 200),
                    new Exchange("POST", "/api/audits/capture-requests/selected/renew", 200),
                    new Exchange("PUT", requestPath + "/chunks/0", 204),
                    new Exchange("POST", "/api/audits/capture-requests/selected/renew", 200),
                    new Exchange("POST", requestPath + "/seal", 200)
                );
            await AssertCaptureAsync(server.Services, fixture, limited, ct);
            Directory.GetFiles(identity).Select(Path.GetFileName).Should().Equal("identity.json");
        }
        finally
        {
            // Both hosts and the normally exited child have settled before the owned database/files are removed.
            await owner.Database.EnsureDeletedAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AiObservatoryApiFactory Factory(string connection)
    {
        var factory = new AiObservatoryApiFactory();
        factory.SetDbConnection(connection);
        factory.ConfigurationOverrides["Audit:Enabled"] = "true";
        factory.ConfigurationOverrides["Audit:WorkerEnabled"] = "false";
        factory.ConfigurationOverrides["AuditSelectedInvocation:Enabled"] = "false";
        // Both satisfy AuditOptions validation. A <30s lease forces the actual CLI's upload renewal branch.
        factory.ConfigurationOverrides["Audit:RunLeaseDuration"] = "00:00:25";
        factory.ConfigurationOverrides["Audit:RunLeaseRenewalInterval"] = "00:00:05";
        return factory;
    }

    private static void Bind(AiObservatoryApiFactory factory, Fixture fixture, Instant expires)
    {
        factory.ConfigurationOverrides["AuditSelectedInvocation:RunId"] = fixture.RunId.ToString("D");
        factory.ConfigurationOverrides["AuditSelectedInvocation:InvocationId"] = Guid.NewGuid().ToString("D");
        factory.ConfigurationOverrides["AuditSelectedInvocation:FullScopeDigest"] = fixture.FullDigest;
        factory.ConfigurationOverrides["AuditSelectedInvocation:ExpectedRevision"] = "0";
        factory.ConfigurationOverrides["AuditSelectedInvocation:ExpiresAtUtc"] = InstantPattern.ExtendedIso.Format(
            expires
        );
    }

    private static async Task<Fixture> PrepareAsync(string connection, CancellationToken ct)
    {
        await using var factory = Factory(connection);
        await factory.InitializeAsync();
        await using var services = factory.Services.CreateAsyncScope();
        var store = services.ServiceProvider.GetRequiredService<AuditStore>();
        var credentials = services.ServiceProvider.GetRequiredService<AuditCredentialService>();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var selected = await credentials.IssueAsync(Guid.Empty, ct);
        var sibling = await credentials.IssueAsync(Guid.Empty, ct);
        var scope = new AuditScope(
            new(2026, 10, 19),
            new(2026, 10, 21),
            [
                new(selected.MachineId, "claude-local"),
                new(selected.MachineId, "codex-local"),
                new(sibling.MachineId, "claude-local"),
                new(sibling.MachineId, "codex-local"),
            ],
            [CheckCode.SourceAccounting],
            [],
            true
        );
        var now = SystemClock.Instance.GetCurrentInstant();
        var runId = await store.CreateRunAsync(scope, [], "synthetic-selected-composition", true, now, ct);
        var request = await db
            .Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM \"AuditCaptureRequests\" WHERE \"MachineId\"={selected.MachineId}"
            )
            .SingleAsync(ct);
        var siblingRequest = await db
            .Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM \"AuditCaptureRequests\" WHERE \"MachineId\"={sibling.MachineId}"
            )
            .SingleAsync(ct);
        // An existing sibling upload is a tangible no-reset/no-delete witness; no generic claim is performed.
        var bytes = Encoding.UTF8.GetBytes("synthetic-unrelated-upload\n");
        var hash = AuditCanonicalJson.ContentHash(bytes);
        var token = Guid.NewGuid();
        var expiry = now + Duration.FromMinutes(5);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AuditEvidenceChunks\" (\"RequestId\",\"Index\",\"Sha256\",\"Bytes\",\"RecordCount\") VALUES ({siblingRequest},0,{hash},{bytes},1)",
            ct
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditCaptureRequests\" SET \"State\"='uploading',\"CredentialId\"={sibling.CredentialId},\"LeaseToken\"={token},\"LeaseExpiresAt\"={expiry},\"UploadedBytes\"={bytes.Length},\"RecordCount\"=1,\"Revision\"=1 WHERE \"Id\"={siblingRequest}",
            ct
        );
        await store.CreateRunAsync(scope, [], "synthetic-unrelated-run", false, now, ct);
        var subset = scope with { Sources = scope.Sources.Where(s => s.MachineId == selected.MachineId).ToArray() };
        var fullDigest = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(scope));
        var subsetDigest = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(subset));
        fullDigest.Should().NotBe(subsetDigest);
        return new(
            runId,
            request,
            selected.MachineId,
            selected.Secret,
            subset,
            fullDigest,
            subsetDigest,
            store.InstanceId
        );
    }

    private static async Task<string[]> WriteSourcesAsync(string directory, CancellationToken ct)
    {
        var roots = new[] { Path.Combine(directory, "claude"), Path.Combine(directory, "codex") };
        foreach (var root in roots)
        {
            Directory.CreateDirectory(root);
        }
        await File.WriteAllTextAsync(
            Path.Combine(roots[0], "session.jsonl"),
            """
            {"type":"assistant","sessionId":"synthetic-a","timestamp":"2026-10-20T12:00:00Z","message":{"id":"synthetic-message","model":"synthetic-model","usage":{"input_tokens":12,"output_tokens":2}}}
            """,
            ct
        );
        await File.WriteAllTextAsync(
            Path.Combine(roots[1], "session.jsonl"),
            """
            {"type":"session_meta","timestamp":"2026-10-18T12:00:00Z","payload":{"id":"synthetic-b","model":"synthetic-model"}}
            {"type":"event_msg","timestamp":"2026-10-18T12:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"cached_input_tokens":2,"output_tokens":3,"reasoning_output_tokens":1,"total_tokens":13}}}}
            {"type":"event_msg","timestamp":"2026-10-20T12:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":30,"cached_input_tokens":6,"output_tokens":9,"reasoning_output_tokens":4,"total_tokens":39}}}}
            """,
            ct
        );
        return roots;
    }

    private static async Task<string> WriteConfigurationAsync(
        string directory,
        string origin,
        Fixture fixture,
        Instant expiry,
        string[] roots,
        bool limited,
        CancellationToken ct
    )
    {
        using var scope = JsonDocument.Parse(AuditCanonicalJson.Serialize(fixture.Scope));
        var configuration = new
        {
            origin,
            key = fixture.Key,
            allowLoopbackHttp = true,
            approval = new
            {
                schemaVersion = 1,
                runId = fixture.RunId,
                expectedRevision = 0,
                fullScopeDigest = fixture.FullDigest,
                requestId = fixture.RequestId,
                machineId = fixture.MachineId,
                requestScopeDigest = fixture.SubsetDigest,
                authorityExpiresAt = InstantPattern.ExtendedIso.Format(expiry),
                instanceId = fixture.InstanceId,
            },
            requestScope = scope.RootElement,
            identityDirectory = Path.Combine(directory, "identity"),
            sources = new[]
            {
                new
                {
                    sourceId = "claude-local",
                    tool = "claude",
                    roots = new[] { roots[0] },
                },
                new
                {
                    sourceId = "codex-local",
                    tool = "codex",
                    roots = new[] { roots[1] },
                },
            },
            caps = new
            {
                maxDirectoryEntries = 100,
                maxDepth = 4,
                maxQueuedPaths = 30,
                maxFiles = 20,
                maxFileBytes = 100000,
                maxReadBytes = 500000,
                maxLines = 100,
                maxLineBytes = 10000,
                maxRecords = limited ? 1 : 50,
                maxDiagnostics = 20,
                maxEncodedBytes = 100000,
                maxChunkBytes = 10000,
                maxHttpAttempts = 20,
                maxUploadBytes = 500000,
                maxDurationMs = 60000,
                sealReserveAttempts = 1,
                sealReserveBytes = 10000,
                sealReserveMs = 1000,
                maxOwnedWriteBytes = 100000,
                maxOwnedEntries = 100,
                maxPathBytes = 4096,
                maxMetadataBytes = 10000,
            },
        };
        var path = Path.Combine(directory, "selected.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration), ct);
        return path;
    }

    private static async Task AssertCaptureAsync(
        IServiceProvider provider,
        Fixture fixture,
        bool limited,
        CancellationToken ct
    )
    {
        await using var services = provider.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var store = services.ServiceProvider.GetRequiredService<AuditStore>();
        var id = await db
            .Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM \"AuditEvidenceCaptures\" WHERE \"RequestId\"={fixture.RequestId}"
            )
            .SingleAsync(ct);
        var capture = await store.ReadCaptureAsync(id, ct);
        capture.Should().NotBeNull();
        var manifest = AuditCanonicalJson.Deserialize<CaptureManifest>(capture.Manifest);
        manifest.CaptureId.Should().Be(id);
        manifest.RequestId.Should().Be(fixture.RequestId);
        manifest.MachineId.Should().Be(fixture.MachineId);
        AuditCanonicalJson.Serialize(manifest.Scope).Should().Equal(AuditCanonicalJson.Serialize(fixture.Scope));
        manifest.DiscoveredFiles.Should().Be(2);
        manifest.ReadFiles.Should().Be(2);
        manifest.SkippedFiles.Should().Be(0);
        manifest.ChangedFiles.Should().Be(0);
        manifest.ExtractorVersion.Should().Be("1.1.0");
        manifest
            .SourceFormats.Should()
            .Equal(
                new SourceFormat("claude-local", "claude-jsonl", "2"),
                new SourceFormat("codex-local", "codex-jsonl", "1")
            );
        if (limited)
        {
            manifest.Diagnostics.Should().Equal(new DiagnosticCount("capture_limit", 1));
        }
        else
        {
            manifest.Diagnostics.Should().BeEmpty();
            var records = capture
                .Chunks.SelectMany(chunk =>
                    Encoding.UTF8.GetString(chunk).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                )
                .Select(line => AuditCanonicalJson.Deserialize<SourceUsageRecord>(Encoding.UTF8.GetBytes(line)))
                .ToArray();
            var cumulative = records
                .Where(r => r.SourceId == "codex-local" && r.Kind == UsageRecordKind.CumulativeUsage)
                .OrderBy(r => r.Sequence)
                .ToArray();
            // This exact wire assertion must fail when only the pre-window cumulative observation is lost.
            cumulative
                .Select(r => (r.Timestamp, r.Kind, r.Counters))
                .Should()
                .Equal(
                    (
                        new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
                        UsageRecordKind.CumulativeUsage,
                        new CodexCounters(10, 2, 3, 1, 13)
                    ),
                    (
                        new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
                        UsageRecordKind.CumulativeUsage,
                        new CodexCounters(30, 6, 9, 4, 39)
                    )
                );
            cumulative[0].SessionId.Should().NotBeNullOrEmpty();
            cumulative[1].SessionId.Should().Be(cumulative[0].SessionId);
            var baseline = (CodexCounters)cumulative[0].Counters!;
            var observed = (CodexCounters)cumulative[1].Counters!;
            // Assert the retained facts support this delta; no audit execution is requested.
            new CodexCounters(
                observed.InputTokens - baseline.InputTokens,
                observed.CachedInputTokens - baseline.CachedInputTokens,
                observed.OutputTokens - baseline.OutputTokens,
                observed.ReasoningOutputTokens - baseline.ReasoningOutputTokens,
                observed.TotalTokens - baseline.TotalTokens
            )
                .Should()
                .Be(new CodexCounters(20, 4, 6, 3, 26));
            records.Should().HaveCount(4);
            records.Select(r => r.SourceId).Distinct().Should().BeEquivalentTo("claude-local", "codex-local");
        }
        manifest.RecordCount.Should().Be(limited ? 1 : 4);
        (await store.GetRunAsync(fixture.RunId, ct))!.Revision.Should().Be(0);
        var state = await db
            .Database.SqlQuery<string>(
                $"SELECT \"State\" AS \"Value\" FROM \"AuditCaptureRequests\" WHERE \"Id\"={fixture.RequestId}"
            )
            .SingleAsync(ct);
        state.Should().Be("sealed");
    }

    private static async Task<string> UnrelatedFactsAsync(
        AiObservatoryDbContext db,
        Guid selected,
        CancellationToken ct
    ) =>
        string.Join(
            "\n",
            await db
                .Database.SqlQuery<string>(
                    $"""
                    SELECT kind || ':' || data AS "Value" FROM (
                        SELECT 'run' AS kind,row_to_json(r)::text AS data FROM "AuditRuns" r
                        UNION ALL SELECT 'request',row_to_json(r)::text FROM "AuditCaptureRequests" r WHERE "Id"<>{selected}
                        UNION ALL SELECT 'chunk',row_to_json(c)::text FROM "AuditEvidenceChunks" c WHERE "RequestId"<>{selected}
                        UNION ALL SELECT 'capture',row_to_json(c)::text FROM "AuditEvidenceCaptures" c WHERE "RequestId"<>{selected}
                        UNION ALL SELECT 'link',row_to_json(l)::text FROM "AuditRunCaptures" l
                    ) facts ORDER BY kind,data
                    """
                )
                .ToArrayAsync(ct)
        );

    private static async Task<CliResult> RunCliAsync(
        string node,
        string repository,
        string config,
        CancellationToken ct
    )
    {
        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = repository,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(repository, "clients", "observatory-audit.mjs"));
        start.ArgumentList.Add("selected-capture");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(config);
        start.ArgumentList.Add("--supervised-stdin");
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                start.Environment[name] = value;
            }
        }
        using var process = Process.Start(start)!;
        var input = process.StandardInput;
        using var stop = ct.Register(() => RequestStop(input));
        var stdout = ReadOutputAsync(process.StandardOutput);
        var stderr = ReadOutputAsync(process.StandardError);
        // Cancellation requests cooperative stop through this child's pipe, then still joins normal exit.
        // Runner loss closes the parent pipe; the CLI treats that EOF as the same stop request.
        await process.WaitForExitAsync(CancellationToken.None);
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static void RequestStop(StreamWriter input)
    {
        try
        {
            input.Write('!');
            input.Flush();
        }
        catch (IOException)
        {
            // The owned child may already have closed its pipe while cancellation was delivered.
        }
        catch (ObjectDisposedException)
        {
            // A normally settled child needs no further stop delivery.
        }
        finally
        {
            try
            {
                input.Close();
            }
            catch (IOException)
            {
                // Closing a pipe whose reader has settled is also a completed stop request.
            }
            catch (ObjectDisposedException)
            {
                // The owned pipe was already closed.
            }
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader)
    {
        var buffer = new char[256];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            if (text.Length < 512)
            {
                text.Append(buffer, 0, Math.Min(count, 512 - text.Length));
            }
        }
        return text.ToString();
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "clients", "observatory-audit.mjs")))
        {
            root = root.Parent;
        }
        return root?.FullName ?? throw new InvalidOperationException("Repository CLI is required.");
    }

    private static string NodeExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }
            var candidate = new FileInfo(Path.Combine(directory, name));
            if (candidate.Exists)
            {
                return candidate.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate.FullName;
            }
        }
        throw new InvalidOperationException("Node 24+ must be installed on PATH.");
    }

    private static string OwnedConnection()
    {
        var supplied =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? throw new InvalidOperationException("The PostgreSQL assembly fixture must initialize first.");
        return new NpgsqlConnectionStringBuilder(supplied)
        {
            Database = $"aiobs_test_selected_composition_{Guid.NewGuid():N}",
        }.ConnectionString;
    }

    private static void MakePrivate(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(
                new FileSystemAccessRule(
                    identity.User!,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                )
            );
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed record Fixture(
        Guid RunId,
        Guid RequestId,
        Guid MachineId,
        string Key,
        AuditScope Scope,
        string FullDigest,
        string SubsetDigest,
        string InstanceId
    );

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private sealed record Exchange(string Method, string Path, int Status);

    private sealed class CaptureTrace : IStartupFilter
    {
        private readonly List<Exchange> _exchanges = [];

        public Exchange[] Snapshot()
        {
            lock (_exchanges)
            {
                return _exchanges.ToArray();
            }
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, continuation) =>
                    {
                        await continuation(context);
                        lock (_exchanges)
                        {
                            if (_exchanges.Count >= 20)
                            {
                                throw new InvalidOperationException("Synthetic HTTP attempt ceiling exceeded.");
                            }
                            _exchanges.Add(
                                new(context.Request.Method, context.Request.Path.Value!, context.Response.StatusCode)
                            );
                        }
                    }
                );
                next(app);
            };
    }
}
