using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using AegisGuard;

static class Program
{
    static int assertions;
    static void Check(bool value, string label) { assertions++; if (!value) throw new Exception(label); }
    static void Reject(Action action, string label)
    {
        try { action(); }
        catch (Exception e) when (e is CryptographicException || e is InvalidDataException || e is EndOfStreamException) { assertions++; return; }
        throw new Exception("Accepted invalid input: " + label);
    }
    static byte[] Pack(byte[] plain, byte[] key)
    {
        using var source = new MemoryStream(plain); using var output = new MemoryStream();
        GuardCrypto.Pack(source, output, key, "test/file"); return output.ToArray();
    }
    static void ReadAll(byte[] packed, byte[] key, string context = "test/file")
    {
        using var stream = new ProtectedReadStream(new MemoryStream(packed), key, context);
        stream.CopyTo(Stream.Null);
    }
    static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + label);
    }
    static byte[] OwnedField(ProtectedReadStream stream, string name) =>
        (byte[])typeof(ProtectedReadStream).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(stream);
    static bool Wiped(byte[] bytes) => bytes == null || bytes.All(value => value == 0);

    static void ProviderLifetimeTests(byte[] key)
    {
        var plain = new byte[GuardCrypto.ChunkSize * 3 + 91]; new Random(917).NextBytes(plain);
        byte[] packed = Pack(plain, key);
        var issued = new List<byte[]>();
        Func<byte[]> provider = () => { var owned = (byte[])key.Clone(); issued.Add(owned); return owned; };
        using (var stream = new ProtectedReadStream(new MemoryStream(packed), provider, "test/file"))
        {
            Check(issued.Count == 1 && issued.All(Wiped), "provider header key cleared");
            Check(OwnedField(stream, "enc") == null && OwnedField(stream, "mac") == null, "provider no resident derived keys after constructor");
            var output = new byte[plain.Length];
            Check(stream.Read(output, 0, output.Length) == output.Length && output.SequenceEqual(plain), "provider full read across chunks");
            Check(issued.Count == 2, "one provider acquisition per multi-chunk Read");
            Check(issued.All(Wiped) && OwnedField(stream, "cached") == null && OwnedField(stream, "enc") == null && OwnedField(stream, "mac") == null,
                "provider keys and plaintext cache absent after Read");
            int before = issued.Count;
            Check(stream.Read(output, 0, output.Length) == 0 && issued.Count == before, "EOF does not acquire key");
            var random = new Random(847);
            for (int i = 0; i < 60; i++)
            {
                int offset = random.Next(plain.Length); int count = random.Next(1, 3000);
                stream.Position = offset;
                var buffer = new byte[count];
                int read = stream.Read(buffer, 0, count);
                Check(buffer.Take(read).SequenceEqual(plain.Skip(offset).Take(read)), "provider random seek bytes");
                Check(issued.All(Wiped) && OwnedField(stream, "cached") == null, "provider random read relinquishes secrets");
            }
        }

        var malformedOwned = Enumerable.Repeat((byte)0x7d, 31).ToArray();
        var invalidSource = new MemoryStream(packed);
        Throws<ArgumentException>(() => new ProtectedReadStream(invalidSource, () => malformedOwned, "test/file"), "provider malformed key");
        Check(Wiped(malformedOwned) && !invalidSource.CanRead, "failed constructor wipes provider array and closes source");
        var wrongOwned = GuardCrypto.RandomBytes(32);
        Throws<CryptographicException>(() => new ProtectedReadStream(new MemoryStream(packed), () => wrongOwned, "test/file"), "provider wrong header key");
        Check(Wiped(wrongOwned), "header authentication failure wipes provider key");

        byte[] corrupt = (byte[])packed.Clone(); corrupt[84 + GuardCrypto.ChunkSize + 32] ^= 1;
        var source = new MemoryStream(corrupt);
        using (var stream = new ProtectedReadStream(source, provider, "test/file", leaveOpen: true))
        {
            var caller = Enumerable.Repeat((byte)0x9d, GuardCrypto.ChunkSize * 2 + 14).ToArray();
            Throws<CryptographicException>(() => stream.Read(caller, 7, GuardCrypto.ChunkSize * 2), "bad second chunk");
            Check(caller.Skip(7).Take(GuardCrypto.ChunkSize).All(value => value == 0), "partial plaintext cleared after failed multi-chunk read");
            Check(caller.Take(7).All(value => value == 0x9d) && caller.Skip(7 + GuardCrypto.ChunkSize).All(value => value == 0x9d), "failure wipes only bytes written by this Read");
            Check(!stream.CanRead && source.CanRead, "fault closes wrapper but honors borrowed source ownership");
            Check(issued.All(Wiped) && OwnedField(stream, "cached") == null && OwnedField(stream, "enc") == null && OwnedField(stream, "mac") == null,
                "fault cleans owned keys and cache");
            Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1), "faulted stream cannot resume");
        }
        source.Dispose();

        int calls = 0;
        using (var stream = new ProtectedReadStream(new MemoryStream(packed), () => { if (++calls == 2) throw new IOException("provider unavailable"); return (byte[])key.Clone(); }, "test/file"))
        {
            Throws<IOException>(() => stream.Read(new byte[1], 0, 1), "provider failure during read");
            Check(!stream.CanRead && OwnedField(stream, "cached") == null, "provider failure poisons stream and clears cache");
        }

        // Verify actual owned array contents, not only references disappearing after disposal.
        using (var legacy = new ProtectedReadStream(new MemoryStream(packed), key, "test/file"))
        {
            byte[] encryption = OwnedField(legacy, "enc"), authentication = OwnedField(legacy, "mac");
            legacy.Read(new byte[1], 0, 1); byte[] cache = OwnedField(legacy, "cached");
            Check(!Wiped(encryption) && !Wiped(authentication) && !Wiped(cache), "legacy ownership oracle observes live arrays");
            legacy.Dispose();
            Check(Wiped(encryption) && Wiped(authentication) && Wiped(cache), "legacy Dispose erases previously owned arrays");
        }
    }

    static void KeyShareTests()
    {
        var left = GuardCrypto.RandomBytes(32); var right = GuardCrypto.RandomBytes(32);
        var expected = left.Select((value, index) => (byte)(value ^ right[index])).ToArray();
        var key = BuildKey.CombineBytes(left, right);
        Check(ReferenceEquals(key, left) && key.SequenceEqual(expected), "binary share reconstruction");
        Check(Wiped(right), "binary second share erased before return");
        Array.Clear(key, 0, key.Length);
        left = Enumerable.Repeat((byte)0x74, 32).ToArray(); right = Enumerable.Repeat((byte)0x69, 31).ToArray();
        Throws<InvalidOperationException>(() => BuildKey.CombineBytes(left, right), "invalid binary share length");
        Check(Wiped(left) && Wiped(right), "invalid binary share ownership cleared");
        left = GuardCrypto.RandomBytes(32);
        Throws<InvalidOperationException>(() => BuildKey.CombineBytes(left, left), "aliased share ownership");
        Check(Wiped(left), "aliased share cleaned on failure");
        key = BuildKey.Combine(Convert.ToBase64String(expected), Convert.ToBase64String(new byte[32]));
        Check(key.SequenceEqual(expected), "legacy string API compatibility"); Array.Clear(key, 0, key.Length);
        Throws<FormatException>(() => BuildKey.Combine(Convert.ToBase64String(expected), "not base64!"), "malformed legacy share");
    }
    static void Main()
    {
        var random = new Random(7419); byte[] key = GuardCrypto.RandomBytes(32);
        foreach (int size in new[] { 0, 1, 15, 16, 4095, 65535, 65536, 65537, 196731 })
        {
            var plain = new byte[size]; random.NextBytes(plain); var packed = Pack(plain, key);
            Check(!packed.SequenceEqual(Pack(plain, key)), "fresh salt per container");
            using var stream = new ProtectedReadStream(new MemoryStream(packed), key, "test/file");
            using var output = new MemoryStream(); stream.CopyTo(output);
            Check(output.ToArray().SequenceEqual(plain), "round trip " + size);
            var scratch = new byte[90000];
            for (int attempt = 0; attempt < 80; attempt++)
            {
                int start = random.Next(size + 100), requested = random.Next(90000);
                stream.Seek(start, SeekOrigin.Begin);
                int count = stream.Read(scratch, 0, requested);
                int expected = Math.Max(0, Math.Min(requested, size - start));
                Check(count == expected, "seek count");
                Check(scratch.Take(count).SequenceEqual(plain.Skip(start).Take(count)), "seek bytes");
            }
            stream.Seek(10, SeekOrigin.End); Check(stream.Read(scratch, 0, 1) == 0, "past eof");
            Reject(() => ReadAll(packed, GuardCrypto.RandomBytes(32)), "wrong key");
            Reject(() => ReadAll(packed, key, "other/file"), "wrong resource context");
            foreach (int offset in new[] { 0, 8, 12, 20, 52, packed.Length - 1 })
            {
                byte[] corrupt = (byte[])packed.Clone(); corrupt[offset] ^= 0x80;
                Reject(() => ReadAll(corrupt, key), "bit flip " + offset);
            }
            Reject(() => ReadAll(packed.Take(packed.Length - 1).ToArray(), key), "truncated");
            Reject(() => ReadAll(packed.Concat(new byte[] { 1 }).ToArray(), key), "appended");
        }
        var multi = Pack(new byte[GuardCrypto.ChunkSize * 2], key);
        int stride = GuardCrypto.ChunkSize + 32;
        byte[] swapped = (byte[])multi.Clone();
        Buffer.BlockCopy(multi, 84, swapped, 84 + stride, stride);
        Buffer.BlockCopy(multi, 84 + stride, swapped, 84, stride);
        Reject(() => ReadAll(swapped, key), "reordered authenticated chunks");
        var another = Pack(new byte[GuardCrypto.ChunkSize * 2], key);
        Buffer.BlockCopy(another, 84, multi, 84, stride);
        Reject(() => ReadAll(multi, key), "cross-container splice");
        foreach (string text in new[] { "", "hello", "한글🔐\0data", "\ud800x\udfff", new string('z', 300000) })
        {
            string encoded = GuardCrypto.SealLiteral(text, key);
            Check(GuardCrypto.OpenLiteral(encoded, key) == text, "literal roundtrip");
            Reject(() => GuardCrypto.OpenLiteral(encoded, GuardCrypto.RandomBytes(32)), "literal wrong key");
            byte[] bytes = Convert.FromBase64String(encoded); bytes[48] ^= 1;
            Reject(() => GuardCrypto.OpenLiteral(Convert.ToBase64String(bytes), key), "literal tampering");
        }
        using (var disposed = new ProtectedReadStream(new MemoryStream(Pack(new byte[1], key)), key, "test/file"))
        {
            disposed.Dispose();
            try { disposed.Read(new byte[1], 0, 1); throw new Exception("Disposed stream accepted read"); }
            catch (ObjectDisposedException) { assertions++; }
        }
        ProviderLifetimeTests(key); KeyShareTests();
        Console.WriteLine("PASS: " + assertions + " core assertions (roundtrip, seeks, tamper, context binding, literals, disposal, provider key/cache lifetime, partial-read cleanup, key shares)");
    }
}
