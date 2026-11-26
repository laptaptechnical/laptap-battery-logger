// src/UpdateKeys.cs
// The public keys a release signature may verify against (plan §5.3): the owner's primary and backup P-256 keys,
// each as 32-byte X and Y in hex. tools/keys.ps1 prints this array; paste its output here. The private keys never
// leave the owner's PC (primary, DPAPI) and the office-safe USB (backup, passphrase-encrypted).
// The owner's primary + backup public keys, from tools\keys.ps1 new (2026-11-26).
using System;
using System.Collections.Generic;
using System.Globalization;

public static class UpdateKeys
{
    // { X hex, Y hex } per key.
    public static readonly string[][] Keys = new string[][]
    {
        new string[] { "57c31ac9d6eae18e3b4f0728648f935a43c57bdd48c06c91e5b7a2fb134765bd", "3ed6d908274ef9b8bfde587ec9be558137bb717a4f88fa286afe685d7462f185" },
        new string[] { "c955405085829a68b3dd484562ee82487dd55b34f0072e8e369b64b04dc3d720", "9cb518a39adbb693686fe808b055a1c536efedc776166aaaa27b7e7be2d30581" },
    };

    public static List<byte[]> Blobs()
    {
        List<byte[]> l = new List<byte[]>();
        foreach (string[] k in Keys) l.Add(BuildBlob(k[0], k[1]));
#if TESTHOOKS
        // Tests only: "X:Y;X:Y" in hex from env.
        string t = Environment.GetEnvironmentVariable("LAPTAP_BL_TEST_PUBKEYS");
        if (!string.IsNullOrEmpty(t))
            foreach (string part in t.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] xy = part.Split(':');
                if (xy.Length == 2) l.Add(BuildBlob(xy[0], xy[1]));
            }
#endif
        return l;
    }

    // BCRYPT_ECCPUBLIC_BLOB for P-256: magic "ECS1" (0x31534345), cbKey = 32, then X‖Y (big-endian coordinates).
    public static byte[] BuildBlob(string xHex, string yHex)
    {
        byte[] x = Hex(xHex), y = Hex(yHex);
        if (x.Length != 32 || y.Length != 32) throw new ArgumentException("P-256 public key needs 32-byte X and Y");
        byte[] b = new byte[8 + 64];
        BitConverter.GetBytes(0x31534345).CopyTo(b, 0);
        BitConverter.GetBytes(32).CopyTo(b, 4);
        x.CopyTo(b, 8); y.CopyTo(b, 40);
        return b;
    }

    public static byte[] Hex(string h)
    {
        h = (h ?? "").Trim();
        if (h.Length % 2 != 0) throw new FormatException("odd hex length");
        byte[] b = new byte[h.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(h.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return b;
    }
}
