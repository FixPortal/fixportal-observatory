using System.Security.Cryptography;
using System.Text;

namespace AiObservatory.Data.Security;

/// <summary>
/// AES-256-GCM protection for <see cref="Entities.NotificationSettings.SlackWebhookUrl"/> at
/// rest. The webhook URL is a bearer credential -- possession alone permits posting to the
/// channel -- so it is stored encrypted and only decrypted on the way out of the database.
/// The key comes from the <see cref="KeyEnvironmentVariable"/> environment variable (any
/// passphrase, hashed to the AES key with SHA-256), matching the house rule that secrets live
/// in infra config rather than the database. When the variable is unset the value passes
/// through unchanged, so existing self-hosted deployments keep working until they opt in by
/// setting it; values written before then stay readable because <see cref="Unprotect"/> returns
/// anything without the <see cref="EncryptedPrefix"/> marker as-is. An encrypted value that can
/// no longer be decrypted (the key unset, or rotated away from the one the value was encrypted
/// with) reads back as <see cref="UndecryptableSentinel"/> rather than throwing inside EF
/// materialisation -- see <see cref="UnprotectValue"/>.
/// </summary>
public sealed class SlackWebhookProtector
{
    public const string KeyEnvironmentVariable = "SLACK_WEBHOOK_PROTECTION_KEY";
    public const string EncryptedPrefix = "enc:v1:";

    /// <summary>
    /// Read-path marker for a stored value that cannot be decrypted. Never a valid webhook URL
    /// (the API requires an https://hooks.slack.com/ shape), so it cannot be confused with a
    /// real -- or a corrupt -- URL to post to; the Slack notifier refuses it loudly.
    /// </summary>
    public const string UndecryptableSentinel = "<undecryptable>";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SlackWebhookProtector(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
    }

    /// <summary>Encrypts for storage. Output is prefixed so it is self-identifying on read.</summary>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipherBytes = new byte[plainBytes.Length];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        // nonce || tag || ciphertext -- everything Unprotect needs, in one self-contained blob.
        var blob = new byte[NonceSize + TagSize + cipherBytes.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipherBytes.CopyTo(blob, NonceSize + TagSize);
        return EncryptedPrefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a stored value. Values without the prefix predate encryption being switched on
    /// and are returned unchanged; tampered or wrong-key ciphertext throws
    /// <see cref="CryptographicException"/> rather than surfacing a corrupt URL to post to.
    /// </summary>
    public string Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return stored;
        }

        var blob = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipherBytes = blob.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    // EF value-converter entry points. The environment is read per call: settings reads/writes
    // are rare, and per-call resolution keeps the design-time factory (no key) working.
    public static string? ProtectValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return FromEnvironment()?.Protect(value) ?? value;
    }

    /// <summary>
    /// Read side of the value converter, so it runs INSIDE EF materialisation and must not
    /// throw for an undecryptable value: a throw there fails the whole NotificationSettings
    /// row read, taking every reader down with it (the settings GET, the PUT that loads the
    /// row before it could clear the value, and email alerting alongside Slack) and leaving
    /// hand-run SQL as the only remedy. An encrypted value that cannot be decrypted -- the
    /// key unset, or no longer the one the value was encrypted with -- comes back as
    /// <see cref="UndecryptableSentinel"/> so the row still materialises; the loud failure
    /// lives in the Slack notifier, the one place the plaintext is actually needed. The
    /// stored ciphertext is untouched, so restoring the key recovers the URL.
    /// </summary>
    public static string? UnprotectValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var protector = FromEnvironment();
        if (protector is null)
        {
            return value.StartsWith(EncryptedPrefix, StringComparison.Ordinal) ? UndecryptableSentinel : value;
        }

        try
        {
            return protector.Unprotect(value);
        }
        catch (Exception exception)
            when (exception is CryptographicException or FormatException or ArgumentOutOfRangeException)
        {
            // A key that no longer matches degrades exactly like a missing one; the sentinel
            // can never be mistaken for a corrupt URL to post to. FormatException (payload is
            // not base64) and ArgumentOutOfRangeException (decoded blob shorter than
            // nonce+tag) are Unprotect failures BEFORE AES-GCM runs -- a corrupted column
            // value, not a key problem -- but they surface inside EF materialisation just the
            // same, so they degrade to the sentinel too.
            return UndecryptableSentinel;
        }
    }

    /// <summary>True when a materialised value is the <see cref="UndecryptableSentinel"/>.</summary>
    public static bool IsUndecryptable(string? value) =>
        string.Equals(value, UndecryptableSentinel, StringComparison.Ordinal);

    private static SlackWebhookProtector? FromEnvironment()
    {
        var passphrase = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        return string.IsNullOrEmpty(passphrase) ? null : new SlackWebhookProtector(passphrase);
    }
}
