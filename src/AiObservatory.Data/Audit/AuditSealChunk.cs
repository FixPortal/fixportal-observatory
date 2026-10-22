namespace AiObservatory.Data.Audit;

public sealed record AuditSealChunk(int Index, string Sha256, ReadOnlyMemory<byte> Bytes);
