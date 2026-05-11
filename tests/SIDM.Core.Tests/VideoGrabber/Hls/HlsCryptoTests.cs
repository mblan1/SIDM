using System.Security.Cryptography;
using SIDM.VideoGrabber.Hls;

namespace SIDM.Core.Tests.VideoGrabber.Hls;

public class HlsCryptoTests
{
    [Fact]
    public void DeriveIvFromSequence_for_zero_is_all_zeros()
    {
        HlsCrypto.DeriveIvFromSequence(0).Should().Equal(new byte[16]);
    }

    [Fact]
    public void DeriveIvFromSequence_packs_into_the_low_8_bytes_big_endian()
    {
        var iv = HlsCrypto.DeriveIvFromSequence(0x0102030405060708L);

        var expected = new byte[16] { 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        iv.Should().Equal(expected);
    }

    [Fact]
    public void DeriveIvFromSequence_for_a_typical_sequence_number()
    {
        // mediaSequence = 100 → 0x64 in the last byte, everything else zero.
        var iv = HlsCrypto.DeriveIvFromSequence(100);

        iv[15].Should().Be(0x64);
        iv.Take(15).Should().AllSatisfy(b => b.Should().Be(0));
    }

    [Fact]
    public void Decrypt_round_trips_against_dotnet_encryption()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var iv = HlsCrypto.DeriveIvFromSequence(42);
        var plaintext = RandomNumberGenerator.GetBytes(8192);

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            using var enc = aes.CreateEncryptor();
            ciphertext = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        var recovered = HlsCrypto.DecryptAes128(ciphertext, key, iv);
        recovered.Should().Equal(plaintext);
    }

    [Fact]
    public void Decrypt_rejects_wrong_size_key()
    {
        Action act = () => HlsCrypto.DecryptAes128(new byte[16], new byte[15], new byte[16]);
        act.Should().Throw<ArgumentException>().Where(e => e.ParamName == "key");
    }

    [Fact]
    public void Decrypt_rejects_wrong_size_iv()
    {
        Action act = () => HlsCrypto.DecryptAes128(new byte[16], new byte[16], new byte[15]);
        act.Should().Throw<ArgumentException>().Where(e => e.ParamName == "iv");
    }
}
