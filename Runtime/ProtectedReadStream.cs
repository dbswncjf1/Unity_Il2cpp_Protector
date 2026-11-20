using System;
using System.IO;
using System.Security.Cryptography;

namespace AegisGuard
{
    /// <summary>Seekable authenticated stream. Dispose after AssetBundle.Unload. Provider mode wipes keys and its plaintext cache after each synchronous Read.</summary>
    public sealed class ProtectedReadStream : Stream
    {
        private readonly object gate = new object();
        private readonly Stream source;
        private readonly bool leaveOpen;
        private readonly Func<byte[]> keyProvider;
        private readonly string context;
        private byte[] enc, mac, headerTag, cached;
        private byte[] salt;
        private int chunkSize;
        private long length, position, cachedIndex = -1;
        private bool disposed;

        // Compatibility mode: caller retains ownership of key. Derived keys/cache live until Dispose.
        public ProtectedReadStream(Stream source, byte[] key, string context, bool leaveOpen = false)
            : this(source, context, leaveOpen, null, key) { }

        // Provider MUST return a fresh exclusively owned 32-byte array on each call. It is always cleared.
        // Do not capture a long-lived master key in the provider; production loaders use BuildKey.GetCopy.
        public ProtectedReadStream(Stream source, Func<byte[]> keyProvider, string context, bool leaveOpen = false)
            : this(source, context, leaveOpen, keyProvider ?? throw new ArgumentNullException(nameof(keyProvider)), null) { }

        private ProtectedReadStream(Stream source, string context, bool leaveOpen, Func<byte[]> keyProvider, byte[] key)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.leaveOpen = leaveOpen;
            this.keyProvider = keyProvider; this.context = context;
            byte[] ownedKey = null;
            try
            {
                if (!source.CanRead || !source.CanSeek) throw new ArgumentException("A seekable source is required.");
                if (string.IsNullOrEmpty(context)) throw new ArgumentException("Context is required.");
                source.Position = 0;
                byte[] header = new byte[52]; salt = new byte[32];
                headerTag = new byte[32];
                GuardCrypto.ReadExactly(source, header); GuardCrypto.ReadExactly(source, headerTag);
                Buffer.BlockCopy(header, 20, salt, 0, 32);
                if (keyProvider != null) key = ownedKey = keyProvider();
                enc = GuardCrypto.Derive(key, salt, "enc/" + context);
                mac = GuardCrypto.Derive(key, salt, "mac/" + context);
                Wipe(ownedKey); ownedKey = null;
                if (!GuardCrypto.Equal(headerTag, GuardCrypto.Mac(mac, header)))
                    throw new CryptographicException("Container authentication failed.");
                using (var reader = new BinaryReader(new MemoryStream(header)))
                {
                    if (!GuardCrypto.Equal(reader.ReadBytes(8), GuardCrypto.Magic)) throw new InvalidDataException("Unknown container version.");
                    chunkSize = reader.ReadInt32(); length = reader.ReadInt64();
                }
                if (chunkSize < 4096 || chunkSize > 1048576 || chunkSize % 16 != 0 || length < 0)
                    throw new InvalidDataException("Invalid container dimensions.");
                long chunks = length / chunkSize + (length % chunkSize == 0 ? 0 : 1);
                long expected = checked(GuardCrypto.HeaderSize + chunks * (chunkSize + 32L));
                if (source.Length != expected) throw new InvalidDataException("Truncated or extended container.");
            }
            catch { Dispose(); throw; }
            finally
            {
                Wipe(ownedKey);
                if (keyProvider != null) { Wipe(enc); Wipe(mac); enc = null; mac = null; }
            }
        }

        private void Check() { if (disposed) throw new ObjectDisposedException(nameof(ProtectedReadStream)); }
        public override bool CanRead => !disposed;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => false;
        public override long Length { get { lock (gate) { Check(); return length; } } }
        public override long Position { get { lock (gate) { Check(); return position; } } set { Seek(value, SeekOrigin.Begin); } }
        public override void Flush() { lock (gate) Check(); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (gate)
            {
                Check();
                long result;
                switch (origin)
                {
                    case SeekOrigin.Begin: result = offset; break;
                    case SeekOrigin.Current: result = checked(position + offset); break;
                    case SeekOrigin.End: result = checked(length + offset); break;
                    default: throw new ArgumentOutOfRangeException(nameof(origin));
                }
                if (result < 0) throw new IOException("Cannot seek before the beginning.");
                return position = result; // Unity may seek/read past EOF.
            }
        }

        private static void Wipe(byte[] bytes) { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        private void ClearCache() { Wipe(cached); cached = null; cachedIndex = -1; }

        private void LoadChunk(long index, byte[] encryptionKey, byte[] authenticationKey)
        {
            if (cachedIndex == index) return;
            ClearCache();
            byte[] cipher = new byte[chunkSize], tag = new byte[32], indexBytes = GuardCrypto.Index(index);
            source.Position = checked(GuardCrypto.HeaderSize + index * (chunkSize + 32L));
            GuardCrypto.ReadExactly(source, cipher); GuardCrypto.ReadExactly(source, tag);
            if (!GuardCrypto.Equal(tag, GuardCrypto.Mac(authenticationKey, headerTag, indexBytes, cipher)))
                throw new CryptographicException("Chunk authentication failed.");
            using (var aes = Aes.Create())
            {
                aes.Key = encryptionKey; aes.IV = GuardCrypto.Iv(authenticationKey, headerTag, indexBytes);
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
                using (var transform = aes.CreateDecryptor()) cached = transform.TransformFinalBlock(cipher, 0, cipher.Length);
                cachedIndex = index;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
            lock (gate)
            {
                Check();
                if (count == 0 || position >= length) return 0;
                int total = 0, originalOffset = offset;
                byte[] ownedKey = null, readEnc = null, readMac = null;
                try
                {
                    if (keyProvider != null)
                    {
                        ownedKey = keyProvider();
                        readEnc = GuardCrypto.Derive(ownedKey, salt, "enc/" + context);
                        readMac = GuardCrypto.Derive(ownedKey, salt, "mac/" + context);
                        Wipe(ownedKey); ownedKey = null;
                    }
                    else { readEnc = enc; readMac = mac; }
                    while (count > 0 && position < length)
                    {
                        LoadChunk(position / chunkSize, readEnc, readMac);
                        int within = (int)(position % chunkSize);
                        int take = (int)Math.Min(Math.Min(count, chunkSize - within), length - position);
                        Buffer.BlockCopy(cached, within, buffer, offset, take);
                        position += take; offset += take; count -= take; total += take;
                    }
                    return total;
                }
                catch
                {
                    // A failed read must not expose a silently usable partial plaintext result.
                    Array.Clear(buffer, originalOffset, total);
                    try { Dispose(); } catch { /* Preserve the original read/authentication failure. */ }
                    throw;
                }
                finally
                {
                    if (keyProvider != null)
                    {
                        Wipe(ownedKey); Wipe(readEnc); Wipe(readMac); ClearCache();
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    disposed = true;
                    ClearCache(); Wipe(enc); Wipe(mac); enc = null; mac = null;
                    if (disposing && !leaveOpen) source.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
