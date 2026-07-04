using System.Security.Cryptography;
using System.Text;

namespace Everywhere.Mcp.Connector;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §7 Phase 2 → 6 — value-at-rest
/// encryption for the JSON credential store.
///
/// Design: AES-256-GCM with a per-install random key stored in a sibling
/// file (0600). This gives:
/// <list type="bullet">
///   <item>Cross-platform (no DPAPI/keychain dependency).</item>
///   <item>File-system copy of connections.json alone yields ciphertext.</item>
///   <item>Zero interaction — user never enters a passphrase.</item>
/// </list>
///
/// Threat model:
/// - Protects against: casual disk snapshot exfil, cloud backup accidental
///   sync of connections.json without the keyring.
/// - Does NOT protect against: attacker with full filesystem read on the
///   user's home directory (they get both keyring.bin and the ciphertext).
///   For that we'd need OS keychain integration — deferred, matches what
///   Cebian's existing LLM-key store already does.
///
/// Format on disk (per encrypted string): base64("enc:v1:" + nonce(12) +
/// ciphertext + tag(16)). Any value not starting with the prefix is
/// treated as legacy plaintext and re-encrypted on next write —
/// zero-downtime migration for anyone upgrading from Phase 2.
/// </summary>
public sealed class CredentialEncryptor
{
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public CredentialEncryptor(byte[] key)
    {
        if (key is null || key.Length != 32)
            throw new ArgumentException("AES-256-GCM key must be 32 bytes", nameof(key));
        _key = key;
    }

    /// <summary>Load or create the on-disk keyring next to
    /// <c>connections.json</c>. Concurrent daemon starts race on
    /// keyring creation — we take a file lock to serialize.</summary>
    public static CredentialEncryptor LoadOrCreate(string keyringPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyringPath) ?? ".");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (File.Exists(keyringPath))
            {
                var raw = File.ReadAllBytes(keyringPath);
                if (raw.Length == 32) return new CredentialEncryptor(raw);
                // Malformed keyring — rename aside and regenerate.
                var stashed = keyringPath + $".corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                try { File.Move(keyringPath, stashed); } catch { }
            }

            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var tmp = keyringPath + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, key);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (File.Exists(keyringPath))
                {
                    File.Replace(tmp, keyringPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmp, keyringPath);
                }
                return new CredentialEncryptor(key);
            }
            catch (IOException)
            {
                // Another writer beat us — retry read path.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        throw new IOException($"failed to acquire connector keyring at {keyringPath}");
    }

    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }
        var payload = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, payload, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, payload, NonceSize + cipher.Length, TagSize);
        return Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>Returns the plaintext value. Legacy (unencrypted) values
    /// are returned as-is so callers migrating from Phase 2 keep reading
    /// pre-existing files transparently.</summary>
    public string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        var b64 = stored.Substring(Prefix.Length);
        byte[] payload;
        try { payload = Convert.FromBase64String(b64); }
        catch (FormatException) { return stored; }
        if (payload.Length < NonceSize + TagSize) return stored;

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipher = new byte[payload.Length - NonceSize - TagSize];
        Buffer.BlockCopy(payload, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(payload, NonceSize, cipher, 0, cipher.Length);
        Buffer.BlockCopy(payload, NonceSize + cipher.Length, tag, 0, TagSize);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            // Tag mismatch — key rotated or file tampered. Surface as
            // decryption failure; caller treats it as a missing credential.
            throw new InvalidOperationException("connector credential decryption failed — keyring rotated?");
        }
        return Encoding.UTF8.GetString(plain);
    }

    public bool IsEncrypted(string stored)
        => !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);
}
