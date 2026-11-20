using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

// XChaCha20-Poly1305: HChaCha20 subkey + RFC 8439 ChaCha20-Poly1305.
// Interoperates with the vendored Monocypher 4.0.2 native loader.
internal static class Program
{
    const int HeaderSize = 56;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("AGMETA02");
    static uint Rol(uint x, int n) => (x << n) | (x >> (32 - n));
    static void Q(uint[] s, int a, int b, int c, int d)
    {
        unchecked
        {
            s[a] += s[b]; s[d] = Rol(s[d] ^ s[a], 16);
            s[c] += s[d]; s[b] = Rol(s[b] ^ s[c], 12);
            s[a] += s[b]; s[d] = Rol(s[d] ^ s[a], 8);
            s[c] += s[d]; s[b] = Rol(s[b] ^ s[c], 7);
        }
    }
    static byte[] HChaCha(byte[] key, ReadOnlySpan<byte> nonce)
    {
        uint[] s = new uint[16];
        s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
        for (int i = 0; i < 8; i++) s[i + 4] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        for (int i = 0; i < 4; i++) s[i + 12] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(i * 4));
        for (int i = 0; i < 10; i++)
        {
            Q(s,0,4,8,12); Q(s,1,5,9,13); Q(s,2,6,10,14); Q(s,3,7,11,15);
            Q(s,0,5,10,15); Q(s,1,6,11,12); Q(s,2,7,8,13); Q(s,3,4,9,14);
        }
        byte[] result = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), s[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16 + i * 4), s[i + 12]);
        }
        Array.Clear(s); return result;
    }
    static byte[] Seal(byte[] plain, byte[] key)
    {
        var result = new byte[checked(plain.Length + HeaderSize)];
        Magic.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), (ulong)plain.Length);
        RandomNumberGenerator.Fill(result.AsSpan(16,24));
        byte[] subkey = HChaCha(key, result.AsSpan(16,16));
        Span<byte> nonce = stackalloc byte[12]; nonce.Clear(); result.AsSpan(32,8).CopyTo(nonce.Slice(4));
        try
        {
            using var aead = new ChaCha20Poly1305(subkey);
            aead.Encrypt(nonce, plain, result.AsSpan(56), result.AsSpan(40,16), result.AsSpan(0,40));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(subkey); }
    }
    static byte[] Open(byte[] sealedBytes, byte[] key)
    {
        if (sealedBytes.Length < HeaderSize || !sealedBytes.AsSpan(0,8).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt64LittleEndian(sealedBytes.AsSpan(8)) != (ulong)(sealedBytes.Length - HeaderSize)) throw new InvalidDataException();
        var plain = new byte[sealedBytes.Length - HeaderSize];
        byte[] subkey = HChaCha(key, sealedBytes.AsSpan(16,16));
        Span<byte> nonce = stackalloc byte[12]; nonce.Clear(); sealedBytes.AsSpan(32,8).CopyTo(nonce.Slice(4));
        try
        {
            using var aead = new ChaCha20Poly1305(subkey);
            aead.Decrypt(nonce, sealedBytes.AsSpan(56), sealedBytes.AsSpan(40,16), plain, sealedBytes.AsSpan(0,40));
            return plain;
        }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
        finally { CryptographicOperations.ZeroMemory(subkey); }
    }
    static void SelfTest()
    {
        byte[] vector = HChaCha(Enumerable.Range(0,32).Select(i=>(byte)i).ToArray(), Convert.FromHexString("000000090000004a0000000031415927"));
        if (Convert.ToHexString(vector) != "82413B4227B27BFED30E42508A877D73A0F9E4D58A74A853C12EC41326D3ECDC") throw new Exception("HChaCha20 vector failed.");
        int checks = 1;
        foreach (int size in new[] { 0, 1, 15, 16, 17, 1024, 1048576 })
        {
            var key = RandomNumberGenerator.GetBytes(32); var plain = RandomNumberGenerator.GetBytes(size);
            var sealedBytes = Seal(plain,key);
            if (!Open(sealedBytes,key).SequenceEqual(plain)) throw new Exception("Roundtrip failed."); checks++;
            foreach (int offset in new[] { 0, 8, 16, 40, sealedBytes.Length-1 })
            {
                var bad = (byte[])sealedBytes.Clone(); bad[offset] ^= 1;
                bool rejected = false;
                try { Open(bad,key); } catch (Exception e) when (e is InvalidDataException || e is CryptographicException) { rejected = true; }
                if (!rejected) throw new Exception("Tamper accepted."); checks++;
            }
        }
        Console.WriteLine("AEGIS_METADATA_CRYPTO_PASS checks=" + checks);
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "self-test") { SelfTest(); return 0; }
            if (args.Length != 2 || args[0] != "seal") throw new ArgumentException("Usage: seal <metadata-path>; key supplied on stdin.");
            Console.InputEncoding = new UTF8Encoding(false);
            byte[] key = Convert.FromBase64String((Console.ReadLine() ?? "").Trim().TrimStart('\uFEFF'));
            if (key.Length != 32) throw new ArgumentException("Invalid key size.");
            byte[] plain = File.ReadAllBytes(args[1]);
            try
            {
                if (plain.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(plain) != 0xFAB11BAF || BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4)) != 39)
                    throw new InvalidDataException("Expected fresh Unity 6000.3 metadata v39; refusing unknown or already sealed data.");
                byte[] packed = Seal(plain,key), verified = Open(packed,key);
                if (!CryptographicOperations.FixedTimeEquals(plain,verified)) throw new CryptographicException("Self-verification failed.");
                CryptographicOperations.ZeroMemory(verified);
                string temp = args[1] + ".agtmp";
                File.WriteAllBytes(temp,packed); File.Move(temp,args[1],true);
                Console.WriteLine("AEGIS_METADATA_SEALED bytes=" + packed.Length);
            }
            finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key); }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
    }
}
