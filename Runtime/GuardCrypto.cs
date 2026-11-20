using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AegisGuard
{
    // AES-256-CBC (fixed-size zero-padded chunks), encrypt-then-HMAC-SHA256.
    // CBC uses no padding oracle: each chunk is authenticated BEFORE decryption.
    public static class GuardCrypto
    {
        public const int ChunkSize = 65536;
        internal const int HeaderSize = 84;
        internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("AEGIS001");

        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        internal static byte[] Mac(byte[] key, params byte[][] pieces)
        {
            using (var h = new HMACSHA256(key))
            {
                foreach (var piece in pieces) h.TransformBlock(piece, 0, piece.Length, null, 0);
                h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return h.Hash;
            }
        }

        internal static byte[] Derive(byte[] key, byte[] salt, string purpose)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("A 32-byte key is required.");
            return Mac(key, Encoding.UTF8.GetBytes("AegisGuard/v1/" + purpose + "\0"), salt);
        }

        internal static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int different = 0;
            for (int i = 0; i < a.Length; i++) different |= a[i] ^ b[i];
            return different == 0;
        }

        internal static byte[] Index(long value)
        {
            var b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)((ulong)value >> (i * 8));
            return b;
        }

        internal static byte[] Iv(byte[] macKey, byte[] tag, byte[] index)
        {
            var hash = Mac(macKey, new byte[] { 73, 86 }, tag, index);
            var iv = new byte[16];
            Buffer.BlockCopy(hash, 0, iv, 0, 16);
            Array.Clear(hash, 0, hash.Length);
            return iv;
        }

        internal static void ReadExactly(Stream input, byte[] buffer)
        {
            int position = 0;
            while (position < buffer.Length)
            {
                int read = input.Read(buffer, position, buffer.Length - position);
                if (read == 0) throw new EndOfStreamException("Truncated protected container.");
                position += read;
            }
        }

        public static void Pack(Stream input, Stream output, byte[] masterKey, string context)
        {
            if (!input.CanSeek || !input.CanRead || input.Position != 0)
                throw new ArgumentException("Input must be readable, seekable and positioned at zero.");
            if (string.IsNullOrEmpty(context)) throw new ArgumentException("Context is required.");
            byte[] salt = RandomBytes(32);
            byte[] enc = null, mac = null, plain = null;
            try
            {
                enc = Derive(masterKey, salt, "enc/" + context);
                mac = Derive(masterKey, salt, "mac/" + context);
                plain = new byte[ChunkSize];
                byte[] header;
                using (var mem = new MemoryStream())
                using (var writer = new BinaryWriter(mem))
                {
                    writer.Write(Magic); writer.Write(ChunkSize); writer.Write(input.Length); writer.Write(salt);
                    header = mem.ToArray();
                }
                var headerTag = Mac(mac, header);
                output.Write(header, 0, header.Length); output.Write(headerTag, 0, headerTag.Length);
                using (var aes = Aes.Create())
                {
                    aes.Key = enc; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
                    long remaining = input.Length;
                    for (long chunk = 0; remaining > 0; chunk++)
                    {
                        Array.Clear(plain, 0, plain.Length);
                        int take = (int)Math.Min(remaining, ChunkSize), done = 0;
                        while (done < take)
                        {
                            int read = input.Read(plain, done, take - done);
                            if (read == 0) throw new EndOfStreamException();
                            done += read;
                        }
                        var index = Index(chunk);
                        aes.IV = Iv(mac, headerTag, index);
                        using (var encryptor = aes.CreateEncryptor())
                        {
                            byte[] ciphertext = encryptor.TransformFinalBlock(plain, 0, plain.Length);
                            var tag = Mac(mac, headerTag, index, ciphertext);
                            output.Write(ciphertext, 0, ciphertext.Length); output.Write(tag, 0, tag.Length);
                        }
                        remaining -= take;
                    }
                }
            }
            finally
            {
                if (enc != null) Array.Clear(enc, 0, enc.Length);
                if (mac != null) Array.Clear(mac, 0, mac.Length);
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }

        // Compact envelope for literals; avoids wasting a 64 KiB chunk per literal.
        public static string SealLiteral(string value, byte[] key)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            byte[] salt = RandomBytes(32), iv = RandomBytes(16);
            byte[] enc = null, mac = null, plain = null;
            try
            {
                enc = Derive(key, salt, "literal/enc"); mac = Derive(key, salt, "literal/mac");
                // Preserve every UTF-16 code unit, including unpaired surrogates in legal C# strings.
                plain = new byte[checked(value.Length * 2)];
                for (int i = 0; i < value.Length; i++) { plain[i * 2] = (byte)value[i]; plain[i * 2 + 1] = (byte)(value[i] >> 8); }
                using (var aes = Aes.Create())
                {
                    aes.Key = enc; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    using (var transform = aes.CreateEncryptor())
                    {
                        var cipher = transform.TransformFinalBlock(plain, 0, plain.Length);
                        using (var mem = new MemoryStream())
                        {
                            mem.Write(salt, 0, 32); mem.Write(iv, 0, 16); mem.Write(cipher, 0, cipher.Length);
                            byte[] body = mem.ToArray(), tag = Mac(mac, body);
                            mem.Write(tag, 0, tag.Length);
                            return Convert.ToBase64String(mem.ToArray());
                        }
                    }
                }
            }
            finally
            {
                if (plain != null) Array.Clear(plain, 0, plain.Length);
                if (enc != null) Array.Clear(enc, 0, enc.Length);
                if (mac != null) Array.Clear(mac, 0, mac.Length);
            }
        }

        public static string OpenLiteral(string payload, byte[] key)
        {
            byte[] all = Convert.FromBase64String(payload);
            if (all.Length < 96 || (all.Length - 80) % 16 != 0) throw new InvalidDataException("Invalid literal envelope.");
            byte[] salt = new byte[32], iv = new byte[16], body = new byte[all.Length - 32], tag = new byte[32];
            Buffer.BlockCopy(all, 0, salt, 0, 32); Buffer.BlockCopy(all, 32, iv, 0, 16);
            Buffer.BlockCopy(all, 0, body, 0, body.Length); Buffer.BlockCopy(all, body.Length, tag, 0, 32);
            byte[] enc = null, mac = null, plain = null;
            try
            {
                enc = Derive(key, salt, "literal/enc"); mac = Derive(key, salt, "literal/mac");
                if (!Equal(tag, Mac(mac, body))) throw new CryptographicException("Literal authentication failed.");
                using (var aes = Aes.Create())
                {
                    aes.Key = enc; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    using (var transform = aes.CreateDecryptor()) plain = transform.TransformFinalBlock(all, 48, all.Length - 80);
                    if (plain.Length % 2 != 0) throw new InvalidDataException("Invalid UTF-16 literal length.");
                    var chars = new char[plain.Length / 2];
                    try
                    {
                        for (int i = 0; i < chars.Length; i++) chars[i] = (char)(plain[i * 2] | (plain[i * 2 + 1] << 8));
                        return new string(chars);
                    }
                    finally { Array.Clear(chars, 0, chars.Length); }
                }
            }
            finally
            {
                if (enc != null) Array.Clear(enc, 0, enc.Length);
                if (mac != null) Array.Clear(mac, 0, mac.Length);
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }
    }
}
