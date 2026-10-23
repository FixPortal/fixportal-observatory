using System.Collections;
using System.Data.Common;
using System.Text;
using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;

namespace AiObservatory.Data.Tests.Audit;

public sealed partial class AuditStoreTests
{
    private sealed class MetadataBeforeRequests(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        private bool _called;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (!_called && command.CommandText.Contains("FROM \"AuditCaptureRequests\"", StringComparison.Ordinal))
            {
                _called = true;
                await action(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task SelectedMetadata_ConcurrentUpdateDoesNotMixSnapshots()
    {
        var machine = await Machine();
        var id = await _store.CreateRunAsync(Scope(machine.MachineId), [], "metadata-consistency", true, Now, Ct);
        var binding = await Binding(id);
        await using var writer = new AiObservatoryDbContext(_options);
        var observer = new MetadataBeforeRequests(async ct =>
        {
            await writer.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"Revision\"=11 WHERE \"Id\"={id}",
                ct
            );
            await writer.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"State\"='expired' WHERE \"Id\" IN (SELECT unnest(\"CaptureRequestIds\") FROM \"AuditRuns\" WHERE \"Id\"={id})",
                ct
            );
        });
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(observer).Options;
        await using var db = new AiObservatoryDbContext(options);
        var metadata = await new AuditStore(db, _clock).ReadSelectedRunMetadataAsync(binding, Ct);
        metadata.Should().NotBeNull();
        metadata.CurrentRevision.Should().Be(0);
        metadata.Requests.Should().ContainSingle().Which.State.Should().Be("pending");
        var fresh = await _store.ReadSelectedRunMetadataAsync(binding, Ct);
        fresh.Should().NotBeNull();
        fresh.CurrentRevision.Should().Be(11);
        fresh.Requests.Should().ContainSingle().Which.State.Should().Be("expired");
    }

    [Fact]
    public async Task SelectedMetadata_QueryFailurePropagatesInsteadOfEmptySuccess()
    {
        var machine = await Machine();
        var id = await _store.CreateRunAsync(Scope(machine.MachineId), [], "metadata-query-failure", true, Now, Ct);
        var binding = await Binding(id);
        var before = await MetadataFacts();
        var observer = new MetadataBeforeRequests(_ => throw new InvalidOperationException("metadata-query-failure"));
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(observer).Options;
        await using var db = new AiObservatoryDbContext(options);
        var action = () => new AuditStore(db, _clock).ReadSelectedRunMetadataAsync(binding, Ct);
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("metadata-query-failure");
        (await MetadataFacts()).Should().Be(before);
    }

    [Fact]
    public async Task SelectedMetadata_SharedAssignedRequestIsStillDescribed()
    {
        var machine = await Machine();
        var scope = Scope(machine.MachineId);
        var id = await _store.CreateRunAsync(scope, [], "metadata-shared-first", true, Now, Ct);
        var other = await _store.CreateRunAsync(scope, [], "metadata-shared-second", false, Now, Ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"CaptureRequestIds\"=(SELECT \"CaptureRequestIds\" FROM \"AuditRuns\" WHERE \"Id\"={id}) WHERE \"Id\"={other}",
            Ct
        );
        var before = await MetadataFacts();
        var metadata = await _store.ReadSelectedRunMetadataAsync(await Binding(id), Ct);
        metadata.Should().NotBeNull();
        metadata.Requests.Should().ContainSingle();
        (await MetadataFacts()).Should().Be(before);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(205)]
    public async Task SelectedMetadata_LinkedCaptureBoundsAreCompleteOrUnavailable(int count)
    {
        var machine = await Machine();
        var scope = Scope(machine.MachineId);
        var id = await _store.CreateRunAsync(scope, [], "metadata-links", false, Now, Ct);
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var bytes = AuditCanonicalJson.Serialize(scope);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "AuditCaptureRequests" ("Id","MachineId","Scope","State","CreatedAt","Deadline","Revision","UploadedBytes","RecordCount")
            SELECT x,{machine.MachineId},{bytes},'sealed',{Now},{Now},0,0,0 FROM unnest({ids}) x
            """,
            Ct
        );
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "AuditEvidenceCaptures" ("Id","RequestId","MachineId","Generation","Manifest","ManifestHash","SealKey","SealedAt","Revision")
            SELECT x,x,{machine.MachineId},'generation-a',{bytes},{new string(
                'a',
                64
            )},'metadata',{Now},0 FROM unnest({ids}) x
            """,
            Ct
        );
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AuditRunCaptures\" (\"RunId\",\"CaptureId\") SELECT {id},x FROM unnest({ids}) x",
            Ct
        );
        var before = await MetadataFacts();
        var observer = new MetadataReadObserver();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(observer).Options;
        await using var db = new AiObservatoryDbContext(options);
        var store = new AuditStore(db, _clock);
        var binding = await Binding(id);
        if (count == 200)
        {
            var result = await store.ReadSelectedRunMetadataAsync(binding, Ct);
            result.Should().NotBeNull();
            result.CaptureIds.Should().BeEquivalentTo(ids);
            result.CaptureRequestIds.Should().BeEmpty();
            result.Requests.Should().BeEmpty();
        }
        else
        {
            var action = () => store.ReadSelectedRunMetadataAsync(binding, Ct);
            (await action.Should().ThrowAsync<AuditStoreException>())
                .Which.Code.Should()
                .Be("selected_metadata_unavailable");
        }
        observer
            .Reads.Where(r => r.Columns.SequenceEqual(new[] { "CaptureId" }))
            .Should()
            .ContainSingle()
            .Which.Rows.Should()
            .BeLessThanOrEqualTo(201);
        (await MetadataFacts()).Should().Be(before);
    }

    [Fact]
    public async Task SelectedMetadata_OpaquePrivatePayloadsNeverMaterialize()
    {
        var lease = await Capture();
        var capture = await _store.SealCaptureAsync(lease, Manifest(lease, [], 0), "metadata-private", Ct);
        var id = await _store.CreateRunAsync(lease.Scope, [capture], "metadata-private", false, Now, Ct);
        (await PublishFrozen((await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!)).Should().BeTrue();
        var sentinel = Encoding.UTF8.GetBytes("PRIVATE_PAYLOAD_SENTINEL" + new string('x', 128 * 1024));
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"Result\"={sentinel},\"SanitizedSummary\"={sentinel} WHERE \"Id\"={id}",
            Ct
        );
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditEvidenceCaptures\" SET \"Manifest\"={sentinel} WHERE \"Id\"={capture}",
            Ct
        );
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditServerSnapshots\" SET \"Bytes\"={sentinel} WHERE \"RunId\"={id}",
            Ct
        );
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AuditEvidenceChunks\" (\"RequestId\",\"Index\",\"Sha256\",\"Bytes\",\"RecordCount\") VALUES ({lease.RequestId},0,{new string('a', 64)},{sentinel},1)",
            Ct
        );
        var before = await MetadataFacts();
        var observer = new MetadataReadObserver();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(observer).Options;
        await using var db = new AiObservatoryDbContext(options);
        var metadata = await new AuditStore(db, _clock).ReadSelectedRunMetadataAsync(await Binding(id), Ct);
        metadata.Should().NotBeNull();
        metadata.ResultAvailable.Should().BeTrue("availability is a retained-data fact, not payload verification");
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, observer.Commands));
        observer
            .Reads.SelectMany(r => r.Columns)
            .Should()
            .NotContain(
                new[]
                {
                    "Result",
                    "SanitizedSummary",
                    "Manifest",
                    "Bytes",
                    "WorkerId",
                    "LeaseToken",
                    "CredentialId",
                    "SecretHash",
                    "RequestHash",
                }
            );
        observer.Reads.SelectMany(r => r.ByteLengths).Should().NotContain(sentinel.Length);
        (await MetadataFacts()).Should().Be(before);
    }

    private sealed class MetadataTransactionObserver : DbTransactionInterceptor
    {
        public string? Setting { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default
        )
        {
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT current_setting('transaction_read_only') || ':' || current_setting('transaction_isolation')";
            Setting = (string?)await command.ExecuteScalarAsync(cancellationToken);
            return result;
        }
    }

    private sealed class MetadataReadObservation(string[] columns)
    {
        public string[] Columns { get; } = columns;
        public List<int> ByteLengths { get; } = [];
        public List<int> GuidArrayLengths { get; } = [];
        public int Rows { get; set; }

        public T Observe<T>(T value)
        {
            if (value is byte[] bytes)
            {
                ByteLengths.Add(bytes.Length);
            }
            if (value is Guid[] ids)
            {
                GuidArrayLengths.Add(ids.Length);
            }
            return value;
        }
    }

    private sealed class MetadataReadObserver : DbCommandInterceptor
    {
        public List<MetadataReadObservation> Reads { get; } = [];
        public List<string> Commands { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            Commands.Add(command.CommandText);
            var observation = new MetadataReadObservation(
                Enumerable.Range(0, result.FieldCount).Select(result.GetName).ToArray()
            );
            Reads.Add(observation);
            return ValueTask.FromResult<DbDataReader>(new MetadataObservingReader(result, observation));
        }
    }

    private sealed class MetadataObservingReader(DbDataReader inner, MetadataReadObservation observation) : DbDataReader
    {
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);

        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        public override char GetChar(int ordinal) => inner.GetChar(ordinal);

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);

        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);

        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);

        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);

        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();

        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);

        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);

        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);

        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);

        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);

        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);

        public override string GetName(int ordinal) => inner.GetName(ordinal);

        public override int GetOrdinal(string name) => inner.GetOrdinal(name);

        public override string GetString(int ordinal) => inner.GetString(ordinal);

        public override object GetValue(int ordinal) => observation.Observe(inner.GetValue(ordinal));

        public override int GetValues(object[] values)
        {
            var count = inner.GetValues(values);
            foreach (var value in values.Take(count))
            {
                observation.Observe(value);
            }
            return count;
        }

        public override T GetFieldValue<T>(int ordinal) => observation.Observe(inner.GetFieldValue<T>(ordinal));

        public override async Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
            observation.Observe(await inner.GetFieldValueAsync<T>(ordinal, cancellationToken));

        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);

        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
            inner.IsDBNullAsync(ordinal, cancellationToken);

        public override bool NextResult() => inner.NextResult();

        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
            inner.NextResultAsync(cancellationToken);

        public override bool Read()
        {
            var found = inner.Read();
            observation.Rows += found ? 1 : 0;
            return found;
        }

        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            var found = await inner.ReadAsync(cancellationToken);
            observation.Rows += found ? 1 : 0;
            return found;
        }

        public override void Close() => inner.Close();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task SelectedMetadata_ReadOnlyConsistentTransaction()
    {
        var id = await Run();
        var observer = new MetadataTransactionObserver();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(observer).Options;
        await using var db = new AiObservatoryDbContext(options);
        (await new AuditStore(db, _clock).ReadSelectedRunMetadataAsync(await Binding(id), Ct)).Should().NotBeNull();
        observer.Setting.Should().Be("on:repeatable read");
    }

    [Fact]
    public async Task SelectedMetadata_MaximumValidIdentifiersAndProvenanceFitsInternalBound()
    {
        var scope = Scope() with
        {
            Sources = Enumerable
                .Range(0, 32)
                .Select(index => new SourceSelection(
                    Guid.NewGuid(),
                    new string('a', 252) + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
                ))
                .ToArray(),
            RequestedChecks = [CheckCode.SourceAccounting, CheckCode.DailyAttribution, CheckCode.EventAggregation],
            ExcludedChecks =
            [
                CheckCode.PricingArithmetic,
                CheckCode.PresentationTotals,
                CheckCode.BillingReconciliation,
            ],
            AllowPartial = false,
        };
        var original = await _store.CreateRunAsync(scope, [], "metadata-max-original", false, Now, Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        var input = new AuditInput
        {
            SchemaVersion = AuditVerifierIdentity.SchemaVersion,
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
            Scope = scope,
        };
        var inputBytes = AuditCanonicalJson.Serialize(input);
        (await _store.TryStoreSnapshotAsync(lease, inputBytes, AuditComputationInput.ContentDigest(input), Ct))
            .Should()
            .BeTrue();
        var full = scope with { PredecessorRunId = original, EvaluationOfRunId = original };
        var id = await _store.CreateRunAsync(full, [], "metadata-max-evaluation", false, Now, Ct);
        var bytes = AuditCanonicalJson.Serialize(full);
        bytes.Length.Should().BeLessThan(64 * 1024);
        var observer = new MetadataReadObserver();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(observer).Options;
        await using var db = new AiObservatoryDbContext(options);
        var result = await new AuditStore(db, _clock).ReadSelectedRunMetadataAsync(await Binding(id), Ct);
        result.Should().NotBeNull();
        result.FullScopeDigest.Should().Be(AuditCanonicalJson.ContentHash(bytes));
        observer.Reads.SelectMany(r => r.ByteLengths).Should().Contain(bytes.Length);
        observer.Reads.SelectMany(r => r.ByteLengths).Should().OnlyContain(length => length <= 64 * 1024);
    }

    [Fact]
    public async Task SelectedMetadata_CorruptUnicodeIdentifierIsUnavailable()
    {
        var id = await Run();
        var binding = await Binding(id);
        var valid = Encoding.UTF8.GetString(AuditCanonicalJson.Serialize(Scope()));
        var corrupt = Encoding.UTF8.GetBytes(valid.Replace("claude-local", "界", StringComparison.Ordinal));
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"Scope\"={corrupt} WHERE \"Id\"={id}",
            Ct
        );
        var before = await MetadataFacts();
        var action = () => _store.ReadSelectedRunMetadataAsync(binding, Ct);
        (await action.Should().ThrowAsync<AuditStoreException>())
            .Which.Code.Should()
            .Be("selected_metadata_unavailable");
        (await MetadataFacts()).Should().Be(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedMetadata_OversizedOpaqueScopeNeverMaterializes(bool requestScope)
    {
        var machine = await Machine();
        var id = await _store.CreateRunAsync(Scope(machine.MachineId), [], "metadata-oversized", true, Now, Ct);
        var binding = await Binding(id);
        var bytes = Encoding.UTF8.GetBytes("PRIVATE_SCOPE_SENTINEL" + new string('x', 64 * 1024));
        if (requestScope)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"Scope\"={bytes} WHERE \"Id\" IN (SELECT unnest(\"CaptureRequestIds\") FROM \"AuditRuns\" WHERE \"Id\"={id})",
                Ct
            );
        }
        else
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"Scope\"={bytes} WHERE \"Id\"={id}",
                Ct
            );
        }
        var observer = new MetadataReadObserver();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(observer).Options;
        await using var db = new AiObservatoryDbContext(options);
        Task Read() => new AuditStore(db, _clock).ReadSelectedRunMetadataAsync(binding, Ct);
        var failure = await Xunit.Record.ExceptionAsync(Read);
        observer
            .Reads.SelectMany(r => r.ByteLengths)
            .Should()
            .OnlyContain(
                length => length <= 64 * 1024,
                "the provider must not transfer an over-limit opaque scope even when the reader later refuses it"
            );
        failure.Should().BeOfType<AuditStoreException>().Which.Code.Should().Be("selected_metadata_unavailable");
    }

    [Fact]
    public async Task SelectedMetadata_RequestArrayOverLimitNeverMaterializes()
    {
        var id = await Run();
        var binding = await Binding(id);
        var ids = Enumerable.Range(0, 33).Select(_ => Guid.NewGuid()).ToArray();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"CaptureRequestIds\"={ids} WHERE \"Id\"={id}",
            Ct
        );
        var observer = new MetadataReadObserver();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(observer).Options;
        await using var db = new AiObservatoryDbContext(options);
        Task Read() => new AuditStore(db, _clock).ReadSelectedRunMetadataAsync(binding, Ct);
        var failure = await Xunit.Record.ExceptionAsync(Read);
        observer.Reads.SelectMany(r => r.GuidArrayLengths).Should().OnlyContain(length => length <= 32);
        failure.Should().BeOfType<AuditStoreException>().Which.Code.Should().Be("selected_metadata_unavailable");
    }

    [Fact]
    public async Task SelectedMetadata_MaximumOwnedRequestArrayIsComplete()
    {
        var machines = new List<MachineRegistration>();
        for (var index = 0; index < 32; index++)
        {
            machines.Add(await Machine());
        }
        var scope = Scope() with
        {
            Sources = machines.Select(m => new SourceSelection(m.MachineId, "claude-local")).ToArray(),
        };
        var id = await _store.CreateRunAsync(scope, [], "metadata-max-requests", true, Now, Ct);
        var metadata = await _store.ReadSelectedRunMetadataAsync(await Binding(id), Ct);
        metadata.Should().NotBeNull();
        metadata.CaptureRequestIds.Should().HaveCount(32);
        metadata.Requests.Should().HaveCount(32);
        metadata.Requests.Select(r => r.MachineId).Should().BeEquivalentTo(machines.Select(m => m.MachineId));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("summary")]
    [InlineData("evidence")]
    [InlineData("capture")]
    [InlineData("snapshot")]
    public async Task SelectedMetadata_ChangedRevisionAndExpiredRunDescribeCurrentMetadata(string expiry)
    {
        var lease = await Capture();
        var capture = await _store.SealCaptureAsync(lease, Manifest(lease, [], 0), "metadata-seal", Ct);
        var id = await _store.CreateRunAsync(lease.Scope, [capture], "metadata-completed", false, Now, Ct);
        var binding = await Binding(id);
        (await PublishFrozen((await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!)).Should().BeTrue();
        if (expiry == "summary")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"SummaryExpiresAt\"={Now} WHERE \"Id\"={id}",
                Ct
            );
        }
        else if (expiry == "evidence")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"EvidenceExpiresAt\"={Now} WHERE \"Id\"={id}",
                Ct
            );
        }
        else if (expiry == "capture")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditEvidenceCaptures\" SET \"ExpiresAt\"={Now} WHERE \"Id\"={capture}",
                Ct
            );
        }
        else if (expiry == "snapshot")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditServerSnapshots\" SET \"ExpiresAt\"={Now} WHERE \"RunId\"={id}",
                Ct
            );
        }
        var before = await MetadataFacts();

        var metadata = await _store.ReadSelectedRunMetadataAsync(binding, Ct);

        metadata.Should().NotBeNull();
        metadata.CurrentRevision.Should().Be(2);
        metadata.Attempts.Should().Be(1);
        metadata.State.Should().Be("completed");
        metadata.ResultAvailable.Should().Be(expiry == "none");
        metadata.CaptureIds.Should().BeEquivalentTo(new[] { capture });
        metadata.CaptureRequestIds.Should().BeEmpty("historical capture provenance is not assigned run requests");
        metadata.Requests.Should().BeEmpty();
        (await MetadataFacts()).Should().Be(before);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("instance")]
    [InlineData("digest")]
    [InlineData("negative_revision")]
    public async Task SelectedMetadata_MissingOrMismatchedBindingFailsClosed(string failure)
    {
        var id = await Run();
        var binding = await Binding(id);
        if (failure == "missing")
        {
            binding = binding with { RunId = Guid.NewGuid() };
        }
        else if (failure == "digest")
        {
            binding = binding with { ScopeDigest = new string('f', 64) };
        }
        else if (failure == "negative_revision")
        {
            binding = binding with { ExpectedRevision = -1 };
        }
        var store = failure == "instance" ? new AuditStore(_db, _clock, instanceId: "other") : _store;
        var before = await MetadataFacts();
        if (failure is "missing" or "instance")
        {
            (await store.ReadSelectedRunMetadataAsync(binding, Ct)).Should().BeNull();
        }
        else
        {
            var read = () => store.ReadSelectedRunMetadataAsync(binding, Ct);
            (await read.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("selected_binding_conflict");
        }
        (await MetadataFacts()).Should().Be(before);
    }

    [Fact]
    public async Task SelectedMetadata_SealedCandidateIsNotLinkedCapture()
    {
        var machine = await Machine();
        var id = await _store.CreateRunAsync(Scope(machine.MachineId), [], "metadata-candidate", true, Now, Ct);
        var lease = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        await _store.SealCaptureAsync(lease, Manifest(lease, [], 0), "metadata-candidate-seal", Ct);
        var before = await MetadataFacts();
        var metadata = await _store.ReadSelectedRunMetadataAsync(await Binding(id), Ct);
        metadata.Should().NotBeNull();
        metadata.Requests.Should().ContainSingle().Which.State.Should().Be("sealed");
        metadata.CaptureIds.Should().BeEmpty();
        metadata.State.Should().Be("collecting");
        metadata.CurrentRevision.Should().Be(0);
        (await MetadataFacts()).Should().Be(before);
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task SelectedMetadata_NoPolicyTrackingOrDomainWrites(EntityState state)
    {
        var id = await Run();
        var binding = await Binding(id);
        var usage = new UsageEvent
        {
            SourceId = "metadata-unsaved",
            OccurredAt = Now,
            IngestedAt = Now,
        };
        _db.UsageEvents.Add(usage);
        if (state != EntityState.Added)
        {
            await _db.SaveChangesAsync(Ct);
            _db.Entry(usage).State = state;
        }
        await _store.GetRetentionAsync(Ct); // Real existing read leaves an unsaved audit policy entry.
        var tracked = _db.ChangeTracker.Entries().Select(e => (e.Entity, e.State)).ToArray();
        var before = await MetadataFacts();

        (await _store.ReadSelectedRunMetadataAsync(binding, Ct)).Should().NotBeNull();

        foreach (var (entity, priorState) in tracked)
        {
            _db.Entry(entity).State.Should().Be(priorState);
        }
        _db.ChangeTracker.Entries().Should().HaveCount(tracked.Length);
        _db.Entry(usage).State.Should().Be(state);
        (await MetadataFacts()).Should().Be(before);
        (
            await _db
                .Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"AuditRetentionSettings\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("machine")]
    [InlineData("scope")]
    [InlineData("duplicate")]
    public async Task SelectedMetadata_CorruptOwnedRequestIsUnavailableWithoutWrites(string failure)
    {
        var machine = await Machine();
        var id = await _store.CreateRunAsync(Scope(machine.MachineId), [], "metadata-request", true, Now, Ct);
        var request = await _db
            .Database.SqlQuery<Guid>(
                $"SELECT unnest(\"CaptureRequestIds\") AS \"Value\" FROM \"AuditRuns\" WHERE \"Id\"={id}"
            )
            .SingleAsync(Ct);
        if (failure == "missing")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"CaptureRequestIds\"={new[] { Guid.NewGuid() }} WHERE \"Id\"={id}",
                Ct
            );
        }
        else if (failure == "machine")
        {
            var other = await Machine();
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"MachineId\"={other.MachineId} WHERE \"Id\"={request}",
                Ct
            );
        }
        else if (failure == "scope")
        {
            var scope = AuditCanonicalJson.Serialize(Scope(machine.MachineId) with { AllowPartial = false });
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"Scope\"={scope} WHERE \"Id\"={request}",
                Ct
            );
        }
        else
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"CaptureRequestIds\"={new[] { request, request }} WHERE \"Id\"={id}",
                Ct
            );
        }
        var before = await MetadataFacts();
        var binding = await Binding(id);
        var read = () => _store.ReadSelectedRunMetadataAsync(binding, Ct);
        (await read.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("selected_metadata_unavailable");
        (await MetadataFacts()).Should().Be(before);
    }

    private Task<string> MetadataFacts() =>
        _db
            .Database.SqlQueryRaw<string>(
                """
                SELECT jsonb_build_object(
                  'runs',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditRuns" r),
                  'requests',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditCaptureRequests" r),
                  'links',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."RunId",r."CaptureId") FROM "AuditRunCaptures" r),
                  'captures',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditEvidenceCaptures" r),
                  'snapshots',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditServerSnapshots" r),
                  'chunks',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."RequestId",r."Index") FROM "AuditEvidenceChunks" r),
                  'policy',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."InstanceId") FROM "AuditRetentionSettings" r),
                  'usage',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "UsageEvents" r)
                )::text AS "Value"
                """
            )
            .SingleAsync(Ct);

    [Fact]
    public async Task SelectedMetadata_ExactRunAndOwnedRequestsOnly()
    {
        var first = await Machine();
        var second = await Machine();
        var scope = Scope(first.MachineId) with
        {
            Sources = [new(first.MachineId, "claude-local"), new(second.MachineId, "codex-local")],
        };
        var id = await _store.CreateRunAsync(scope, [], "metadata-selected", true, Now, Ct);
        await _store.CreateRunAsync(Scope(first.MachineId), [], "metadata-unrelated", true, Now, Ct);
        var binding = new SelectedRunBinding(
            id,
            AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(scope)),
            0
        );

        var metadata = await _store.ReadSelectedRunMetadataAsync(binding, Ct);

        metadata.Should().NotBeNull();
        metadata.RunId.Should().Be(id);
        metadata.CurrentRevision.Should().Be(0);
        metadata.State.Should().Be("collecting");
        metadata.FullScopeDigest.Should().Be(binding.ScopeDigest);
        metadata.Requests.Should().HaveCount(2);
        metadata.Requests.Select(r => r.MachineId).Should().BeEquivalentTo(new[] { first.MachineId, second.MachineId });
        metadata.CaptureRequestIds.Should().BeEquivalentTo(metadata.Requests.Select(r => r.RequestId));
        metadata.CaptureIds.Should().BeEmpty();
        metadata.ResultAvailable.Should().BeFalse();
        (
            await _db
                .Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"AuditRetentionSettings\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }
}
