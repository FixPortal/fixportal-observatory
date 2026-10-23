using AiObservatory.Data.Audit;
using NodaTime;

namespace AiObservatory.Api.Audit;

public sealed record SelectedInvocationAuthority(bool Enabled, SelectedRunBinding Binding, Guid InvocationId, Instant ExpiresAt);
public sealed record SelectedInvocationRequest(int SchemaVersion, long ExpectedRevision, string FullScopeDigest);
