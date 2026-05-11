using System.Security.Cryptography;

namespace SIDM.VideoGrabber.Hls;

/// <summary>
/// AES-128 helpers for HLS. The actual cipher operations defer to .NET's
/// <see cref="Aes"/>; this class exists for the HLS-specific bits — IV
/// derivation from the media sequence number, and the standard CBC + PKCS7
/// configuration the HLS spec mandates for METHOD=AES-128.
/// </summary>
public static class HlsCrypto
{
    /// <summary>
    /// Per RFC 8216 §5.2: when EXT-X-KEY carries no explicit IV, the IV for a
    /// segment is the segment's media sequence number, big-endian, left-padded
    /// to 16 bytes (i.e. the low 8 bytes hold the sequence number and the high
    /// 8 bytes are zero).
    /// </summary>
    public static byte[] DeriveIvFromSequence(long mediaSequenceNumber)
    {
        var iv = new byte[16];
        // High 8 bytes stay zero; write the sequence number into the low 8.
        for (var i = 0; i < 8; i++)
        {
            iv[15 - i] = (byte)((mediaSequenceNumber >> (8 * i)) & 0xff);
        }
        return iv;
    }

    /// <summary>
    /// Decrypts an HLS segment encrypted with METHOD=AES-128. The HLS spec
    /// uses CBC mode with PKCS7 padding.
    /// </summary>
    public static byte[] DecryptAes128(byte[] ciphertext, byte[] key, byte[] iv)
    {
        if (key.Length != 16) throw new ArgumentException("AES-128 key must be 16 bytes.", nameof(key));
        if (iv.Length != 16) throw new ArgumentException("AES IV must be 16 bytes.", nameof(iv));

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}
