using System.Security.Cryptography;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public sealed class AuditCredentialService(AuditStore store)
{
    public async Task<IssuedAuditCredential> IssueAsync(Guid machineId, CancellationToken ct)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(secret);
        var issued =
            machineId == Guid.Empty
                ? await store.RegisterMachineAsync(hash, ct)
                : await store.RotateMachineCredentialAsync(machineId, hash, ct);
        return new(
            issued.MachineId,
            issued.CredentialId,
            $"{issued.CredentialId:D}.{Convert.ToHexStringLower(secret)}"
        );
    }

    public async Task<AuditCredentialView?> VerifyAsync(string provided, CancellationToken ct)
    {
        if (provided.Length != 101 || provided[36] != '.' || !Guid.TryParseExact(provided[..36], "D", out var id))
        {
            return null;
        }
        if (provided.AsSpan(37).ContainsAnyExcept("0123456789abcdefABCDEF"))
        {
            return null;
        }
        var secret = Convert.FromHexString(provided.AsSpan(37));
        var hash = SHA256.HashData(secret);
        var candidate = await store.FindCredentialAsync(id, ct);
        var matches = CryptographicOperations.FixedTimeEquals(hash, candidate?.SecretHash ?? new byte[32]);
        return matches && candidate is { Revoked: false } ? candidate : null;
    }
}
