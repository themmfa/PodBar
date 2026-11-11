namespace PodBar;

/// <summary>
/// Minimal animated GIF encoder, used only by the developer switch <c>--demo-gif</c> to make the README demo.
/// One shared 256-colour palette, LZW compression, and each frame stores only the rectangle that changed.
/// </summary>
static class GifWriter
{
    /// <param name="frames">Frames as 32-bit ARGB pixels (alpha ignored) with a delay in hundredths of a second.</param>
    public static void Write(string path, IReadOnlyList<(int[] Pixels, int DelayCs)> frames, int width, int height)
    {
        var palette = BuildPalette(frames.Select(f => f.Pixels));
        var nearest = new Dictionary<int, byte>();

        byte[] Index(int[] pixels)
        {
            var result = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                int rgb = pixels[i] & 0xFFFFFF;
                if (!nearest.TryGetValue(rgb, out byte index))
                {
                    index = Nearest(palette, rgb);
                    nearest[rgb] = index;
                }
                result[i] = index;
            }
            return result;
        }

        // Turn frames into "changed rectangle" patches; a frame identical to the previous one just extends its delay.
        var patches = new List<(int X, int Y, int W, int H, byte[] Data, int DelayCs)>();
        byte[]? previous = null;
        foreach (var (pixels, delay) in frames)
        {
            byte[] indexed = Index(pixels);
            if (previous is null)
            {
                patches.Add((0, 0, width, height, indexed, delay));
            }
            else
            {
                int minX = width, minY = height, maxX = -1, maxY = -1;
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        if (indexed[row + x] == previous[row + x]) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        maxY = y;
                    }
                }

                if (maxX < 0)
                {
                    var last = patches[^1];
                    patches[^1] = (last.X, last.Y, last.W, last.H, last.Data, last.DelayCs + delay);
                }
                else
                {
                    int w = maxX - minX + 1, h = maxY - minY + 1;
                    var data = new byte[w * h];
                    for (int y = 0; y < h; y++)
                        Array.Copy(indexed, (minY + y) * width + minX, data, y * w, w);
                    patches.Add((minX, minY, w, h, data, delay));
                }
            }
            previous = indexed;
        }

        using var stream = File.Create(path);
        using var o = new BinaryWriter(stream);

        o.Write("GIF89a"u8);
        o.Write((ushort)width);
        o.Write((ushort)height);
        o.Write((byte)0xF7); // global colour table, 256 entries
        o.Write((byte)0);    // background colour index
        o.Write((byte)0);    // pixel aspect ratio
        foreach (int rgb in palette)
        {
            o.Write((byte)(rgb >> 16));
            o.Write((byte)(rgb >> 8));
            o.Write((byte)rgb);
        }

        // Loop forever.
        o.Write((byte)0x21); o.Write((byte)0xFF); o.Write((byte)11);
        o.Write("NETSCAPE2.0"u8);
        o.Write((byte)3); o.Write((byte)1); o.Write((ushort)0); o.Write((byte)0);

        foreach (var p in patches)
        {
            // Graphic control: keep the previous frame in place (disposal 1), no transparency.
            o.Write((byte)0x21); o.Write((byte)0xF9); o.Write((byte)4);
            o.Write((byte)0x04);
            o.Write((ushort)p.DelayCs);
            o.Write((byte)0); o.Write((byte)0);

            o.Write((byte)0x2C);
            o.Write((ushort)p.X); o.Write((ushort)p.Y); o.Write((ushort)p.W); o.Write((ushort)p.H);
            o.Write((byte)0); // no local colour table, not interlaced

            const int minCodeSize = 8;
            o.Write((byte)minCodeSize);
            byte[] compressed = Lzw(p.Data, minCodeSize);
            for (int offset = 0; offset < compressed.Length; offset += 255)
            {
                int n = Math.Min(255, compressed.Length - offset);
                o.Write((byte)n);
                o.Write(compressed, offset, n);
            }
            o.Write((byte)0);
        }
        o.Write((byte)0x3B);
    }

    /// <summary>The 256 most used colours, counted in buckets of 4 levels per channel and averaged within each bucket.</summary>
    static int[] BuildPalette(IEnumerable<int[]> frames)
    {
        var buckets = new Dictionary<int, (long Count, long R, long G, long B)>();
        foreach (var pixels in frames)
        {
            foreach (int argb in pixels)
            {
                int r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
                int key = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
                buckets.TryGetValue(key, out var v);
                buckets[key] = (v.Count + 1, v.R + r, v.G + g, v.B + b);
            }
        }

        var palette = buckets.Values
            .OrderByDescending(v => v.Count)
            .Take(256)
            .Select(v => (int)(((v.R / v.Count) << 16) | ((v.G / v.Count) << 8) | (v.B / v.Count)))
            .ToList();
        while (palette.Count < 256) palette.Add(0);
        return palette.ToArray();
    }

    static byte Nearest(int[] palette, int rgb)
    {
        int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            int dr = r - ((palette[i] >> 16) & 0xFF), dg = g - ((palette[i] >> 8) & 0xFF), db = b - (palette[i] & 0xFF);
            int distance = dr * dr * 2 + dg * dg * 4 + db * db; // rough perceptual weighting
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return (byte)best;
    }

    /// <summary>GIF-flavoured LZW: variable code size starting at minCodeSize + 1, codes packed least significant bit first.</summary>
    static byte[] Lzw(byte[] data, int minCodeSize)
    {
        int clear = 1 << minCodeSize, end = clear + 1;
        int next = end + 1, codeSize = minCodeSize + 1;
        var table = new Dictionary<int, int>();
        var output = new List<byte>(data.Length / 2);
        int bitBuffer = 0, bitCount = 0;

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.Add((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clear);
        int prefix = data[0];
        for (int i = 1; i < data.Length; i++)
        {
            int value = data[i];
            int key = (prefix << 8) | value;
            if (table.TryGetValue(key, out int code))
            {
                prefix = code;
                continue;
            }

            Emit(prefix);
            if (next < 4096)
            {
                table[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clear);
                table.Clear();
                next = end + 1;
                codeSize = minCodeSize + 1;
            }
            prefix = value;
        }
        Emit(prefix);
        Emit(end);
        if (bitCount > 0) output.Add((byte)bitBuffer);
        return output.ToArray();
    }
}
