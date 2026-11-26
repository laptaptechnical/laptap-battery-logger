// tools/KeyTools.cs
// Owner-side signing tools (plan §5.3). Loaded by tools/keys.ps1 and tools/release.ps1 (Add-Type) and compiled into
// tests/HelperTests.exe. Never part of the logger exe.
// - P-256 keys via CngKey (exportable private blob), signatures = 64-byte P1363 r‖s over SHA-256 of the exact bytes.
// - Backup file: PBKDF2-SHA256 (600,000 rounds, 16-byte salt) → 64 bytes = AES-256 key ‖ HMAC-SHA256 key;
//   AES-256-CBC with a random IV; encrypt-then-MAC over salt‖IV‖ciphertext; on open the HMAC is checked first,
//   in constant time, before anything is decrypted. Rfc2898DeriveBytes with SHA256 needs .NET 4.7.2+ (owner's PC).
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class KeyTools
{
    public const int Iterations = 600000;
    static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("LBK1");
    static readonly byte[] TestMagic = Encoding.ASCII.GetBytes("LBTESTKEY1");

    // New exportable P-256 key → BCRYPT_ECCPRIVATE_BLOB (magic ECS2, cbKey 32, X‖Y‖D).
    public static byte[] NewKey()
    {
        CngKeyCreationParameters p = new CngKeyCreationParameters();
        p.ExportPolicy = CngExportPolicies.AllowPlaintextExport;
        p.KeyUsage = CngKeyUsages.Signing;
        using (CngKey k = CngKey.Create(CngAlgorithm.ECDsaP256, null, p))
            return k.Export(CngKeyBlobFormat.EccPrivateBlob);
    }

    public static byte[] PublicX(byte[] priv) { byte[] x = new byte[32]; Buffer.BlockCopy(priv, 8, x, 0, 32); return x; }
    public static byte[] PublicY(byte[] priv) { byte[] y = new byte[32]; Buffer.BlockCopy(priv, 40, y, 0, 32); return y; }

    public static byte[] PublicBlob(byte[] priv)
    {
        using (CngKey k = CngKey.Import(priv, CngKeyBlobFormat.EccPrivateBlob))
            return k.Export(CngKeyBlobFormat.EccPublicBlob);
    }

    public static byte[] Sign(byte[] priv, byte[] data)
    {
        byte[] hash;
        using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(data);
        using (CngKey k = CngKey.Import(priv, CngKeyBlobFormat.EccPrivateBlob))
        using (ECDsaCng e = new ECDsaCng(k))
            return e.SignHash(hash);
    }

    public static string Hex(byte[] b)
    {
        StringBuilder sb = new StringBuilder();
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    // The exact C# that goes into src/UpdateKeys.cs (keys.ps1 prints this).
    public static string CSharpKeys(byte[][] privOrPub)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("    public static readonly string[][] Keys = new string[][]\r\n    {\r\n");
        foreach (byte[] k in privOrPub)
            sb.Append("        new string[] { \"").Append(Hex(PublicX(k))).Append("\", \"").Append(Hex(PublicY(k))).Append("\" },\r\n");
        sb.Append("    };\r\n");
        return sb.ToString();
    }

    // ---- backup file (passphrase) ----
    static void Derive(string pass, byte[] salt, out byte[] aesKey, out byte[] macKey)
    {
        using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(pass, salt, Iterations, HashAlgorithmName.SHA256))
        {
            byte[] k = kdf.GetBytes(64);
            aesKey = new byte[32]; macKey = new byte[32];
            Buffer.BlockCopy(k, 0, aesKey, 0, 32); Buffer.BlockCopy(k, 32, macKey, 0, 32);
        }
    }

    // "LBK1" ‖ salt(16) ‖ IV(16) ‖ ciphertext ‖ HMAC(32) over salt‖IV‖ciphertext.
    public static byte[] ProtectBackup(byte[] priv, string pass)
    {
        if (string.IsNullOrEmpty(pass)) throw new ArgumentException("a passphrase is needed");
        byte[] salt = new byte[16], iv = new byte[16];
        using (RNGCryptoServiceProvider r = new RNGCryptoServiceProvider()) { r.GetBytes(salt); r.GetBytes(iv); }
        byte[] aesKey, macKey; Derive(pass, salt, out aesKey, out macKey);
        byte[] ct;
        using (Aes a = Aes.Create())
        {
            a.Key = aesKey; a.IV = iv; a.Mode = CipherMode.CBC; a.Padding = PaddingMode.PKCS7;
            using (ICryptoTransform t = a.CreateEncryptor()) ct = t.TransformFinalBlock(priv, 0, priv.Length);
        }
        byte[] body = Concat(salt, iv, ct), mac;
        using (HMACSHA256 h = new HMACSHA256(macKey)) mac = h.ComputeHash(body);
        return Concat(BackupMagic, body, mac);
    }

    public static byte[] UnprotectBackup(byte[] file, string pass)
    {
        if (file == null || file.Length < 4 + 16 + 16 + 16 + 32) throw new CryptographicException("Not a backup key file");
        for (int i = 0; i < 4; i++) if (file[i] != BackupMagic[i]) throw new CryptographicException("Not a backup key file");
        int bodyLen = file.Length - 4 - 32;
        byte[] body = new byte[bodyLen], mac = new byte[32];
        Buffer.BlockCopy(file, 4, body, 0, bodyLen); Buffer.BlockCopy(file, 4 + bodyLen, mac, 0, 32);
        byte[] salt = new byte[16], iv = new byte[16], ct = new byte[bodyLen - 32];
        Buffer.BlockCopy(body, 0, salt, 0, 16); Buffer.BlockCopy(body, 16, iv, 0, 16); Buffer.BlockCopy(body, 32, ct, 0, ct.Length);
        byte[] aesKey, macKey; Derive(pass, salt, out aesKey, out macKey);
        byte[] want;
        using (HMACSHA256 h = new HMACSHA256(macKey)) want = h.ComputeHash(body);
        if (!FixedEquals(want, mac)) throw new CryptographicException("Backup key file failed its integrity check (HMAC): wrong passphrase or a damaged file");
        using (Aes a = Aes.Create())
        {
            a.Key = aesKey; a.IV = iv; a.Mode = CipherMode.CBC; a.Padding = PaddingMode.PKCS7;
            using (ICryptoTransform t = a.CreateDecryptor()) return t.TransformFinalBlock(ct, 0, ct.Length);
        }
    }

    public static bool FixedEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int d = 0;
        for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
        return d == 0;
    }

    // ---- test keys (plain, clearly marked; only for the local end-to-end test, never for a real release) ----
    public static byte[] ToTestKeyFile(byte[] priv) { return Concat(TestMagic, priv); }
    public static byte[] FromTestKeyFile(byte[] file)
    {
        for (int i = 0; i < TestMagic.Length; i++) if (file.Length <= TestMagic.Length || file[i] != TestMagic[i]) throw new CryptographicException("Not a test key file");
        byte[] k = new byte[file.Length - TestMagic.Length];
        Buffer.BlockCopy(file, TestMagic.Length, k, 0, k.Length);
        return k;
    }

    static byte[] Concat(params byte[][] parts)
    {
        int n = 0; foreach (byte[] p in parts) n += p.Length;
        byte[] r = new byte[n]; int o = 0;
        foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }
}
