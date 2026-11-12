// Minimal 16-bit greyscale PNG writer/reader (pure C#, no Unity): used for the recorded depth maps (millimetres)
// on a worker thread, and by the offline replay to read them back. Row 0 of the array is the FIRST row of the PNG
// (no vertical flip), so what is written is exactly what cv2.imread(..., IMREAD_UNCHANGED) returns on the PC.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace QuestDetect3D.Recording
{
    public static class Png16
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static uint[] s_crcTable;

        /// <summary>uint16 pixels (row 0 first) → PNG bytes (16-bit greyscale, no filter, fast deflate).</summary>
        public static byte[] Encode(ushort[] pixels, int width, int height)
        {
            if (pixels.Length < width * height)
            {
                throw new ArgumentException("pixel buffer too small");
            }

            // Raw scanlines: filter byte 0 (None) + big-endian samples.
            var stride = 1 + width * 2;
            var raw = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                var o = y * stride;
                raw[o] = 0;
                for (var x = 0; x < width; x++)
                {
                    var v = pixels[y * width + x];
                    raw[o + 1 + 2 * x] = (byte)(v >> 8);
                    raw[o + 2 + 2 * x] = (byte)(v & 0xFF);
                }
            }

            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, true))
                {
                    ds.Write(raw, 0, raw.Length);
                }

                deflated = ms.ToArray();
            }

            var adler = Adler32(raw);
            var zlib = new byte[2 + deflated.Length + 4];
            zlib[0] = 0x78;
            zlib[1] = 0x01;
            Buffer.BlockCopy(deflated, 0, zlib, 2, deflated.Length);
            zlib[zlib.Length - 4] = (byte)(adler >> 24);
            zlib[zlib.Length - 3] = (byte)(adler >> 16);
            zlib[zlib.Length - 2] = (byte)(adler >> 8);
            zlib[zlib.Length - 1] = (byte)adler;

            using (var png = new MemoryStream())
            {
                png.Write(Signature, 0, Signature.Length);
                var ihdr = new byte[13];
                WriteBe(ihdr, 0, (uint)width);
                WriteBe(ihdr, 4, (uint)height);
                ihdr[8] = 16; // bit depth
                ihdr[9] = 0;  // greyscale
                ihdr[10] = 0; // deflate
                ihdr[11] = 0; // adaptive filtering
                ihdr[12] = 0; // no interlace
                WriteChunk(png, "IHDR", ihdr);
                WriteChunk(png, "IDAT", zlib);
                WriteChunk(png, "IEND", Array.Empty<byte>());
                return png.ToArray();
            }
        }

        /// <summary>PNG bytes (16-bit greyscale, any filter, not interlaced) → uint16 pixels.</summary>
        public static ushort[] Decode(byte[] png, out int width, out int height)
        {
            width = height = 0;
            for (var i = 0; i < Signature.Length; i++)
            {
                if (png[i] != Signature[i])
                {
                    throw new InvalidDataException("not a PNG");
                }
            }

            var idat = new List<byte>();
            int bitDepth = 0, colorType = -1;
            var pos = 8;
            while (pos + 8 <= png.Length)
            {
                var len = (int)ReadBe(png, pos);
                var type = Encoding.ASCII.GetString(png, pos + 4, 4);
                var data = pos + 8;
                if (type == "IHDR")
                {
                    width = (int)ReadBe(png, data);
                    height = (int)ReadBe(png, data + 4);
                    bitDepth = png[data + 8];
                    colorType = png[data + 9];
                    if (png[data + 12] != 0)
                    {
                        throw new InvalidDataException("interlaced PNG not supported");
                    }
                }
                else if (type == "IDAT")
                {
                    for (var i = 0; i < len; i++)
                    {
                        idat.Add(png[data + i]);
                    }
                }
                else if (type == "IEND")
                {
                    break;
                }

                pos = data + len + 4;
            }

            if (bitDepth != 16 || colorType != 0)
            {
                throw new InvalidDataException($"expected 16-bit greyscale, got depth={bitDepth} type={colorType}");
            }

            var stride = width * 2;
            var raw = new byte[(stride + 1) * height];
            using (var ms = new MemoryStream(idat.ToArray(), 2, idat.Count - 2)) // skip the zlib header
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                var read = 0;
                while (read < raw.Length)
                {
                    var n = ds.Read(raw, read, raw.Length - read);
                    if (n <= 0)
                    {
                        throw new InvalidDataException("truncated image data");
                    }

                    read += n;
                }
            }

            const int bpp = 2;
            var prev = new byte[stride];
            var cur = new byte[stride];
            var pixels = new ushort[width * height];
            for (var y = 0; y < height; y++)
            {
                var o = y * (stride + 1);
                var filter = raw[o];
                Buffer.BlockCopy(raw, o + 1, cur, 0, stride);
                for (var i = 0; i < stride; i++)
                {
                    var a = i >= bpp ? cur[i - bpp] : 0;
                    var b = prev[i];
                    var c = i >= bpp ? prev[i - bpp] : 0;
                    int v = cur[i];
                    switch (filter)
                    {
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) >> 1; break;
                        case 4: v += Paeth(a, b, c); break;
                    }

                    cur[i] = (byte)v;
                }

                for (var x = 0; x < width; x++)
                {
                    pixels[y * width + x] = (ushort)((cur[2 * x] << 8) | cur[2 * x + 1]);
                }

                (prev, cur) = (cur, prev);
            }

            return pixels;
        }

        /// <summary>PNG Paeth predictor.</summary>
        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            var pa = Math.Abs(p - a);
            var pb = Math.Abs(p - b);
            var pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        /// <summary>Length, type, data, CRC.</summary>
        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var header = new byte[8];
            WriteBe(header, 0, (uint)data.Length);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            Buffer.BlockCopy(typeBytes, 0, header, 4, 4);
            s.Write(header, 0, 8);
            s.Write(data, 0, data.Length);
            var crc = Crc32(typeBytes, data);
            var tail = new byte[4];
            WriteBe(tail, 0, crc);
            s.Write(tail, 0, 4);
        }

        /// <summary>Big-endian uint32.</summary>
        private static void WriteBe(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        /// <summary>Big-endian uint32.</summary>
        private static uint ReadBe(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

        /// <summary>zlib checksum of the raw scanlines.</summary>
        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var d in data)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        /// <summary>PNG chunk CRC.</summary>
        private static uint Crc32(byte[] type, byte[] data)
        {
            if (s_crcTable == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    var c = n;
                    for (var k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    }

                    table[n] = c;
                }

                s_crcTable = table;
            }

            var crc = 0xFFFFFFFFu;
            foreach (var d in type)
            {
                crc = s_crcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
            }

            foreach (var d in data)
            {
                crc = s_crcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
