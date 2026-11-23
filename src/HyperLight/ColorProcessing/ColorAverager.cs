namespace HyperLight.ColorProcessing;

/// <summary>
/// 算一张截图各区域的平均色。
/// 不用每个像素都读：每隔几个像素取一个点，4K 图只读十几万个点，结果几乎一样，速度快得多。
/// 只扫一遍图片：把图分成左上、右上、左下、右下四块分别累加，再组合出上、下、左、右、整张五个颜色。
/// </summary>
internal static class ColorAverager
{
    /// <summary>每隔多少个像素取一个点（横竖都是）。4K 图取 8，约读 13 万个点。</summary>
    private const int sampleStep = 8;

    /// <summary>每个像素占 4 字节，顺序是 B、G、R、未使用（Windows 截图的标准格式）。</summary>
    private const int bytesPerPixel = 4;

    /// <summary>
    /// 算上、下、左、右、整张五个区域的平均色。pixels 是从上到下逐行排列的像素，每行 width × 4 字节。
    /// 图片太小、没法分成两半时（几乎不会发生），五个区域都用整张的颜色。
    /// </summary>
    /// <exception cref="ArgumentException">尺寸不对，或者像素数据比尺寸要求的少（这是程序写错了，不是设备问题）。</exception>
    public static WallpaperColors AverageRegions(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"图片尺寸不对：{width}x{height}");
        }

        long required = (long)width * height * bytesPerPixel;
        if (pixels.Length < required)
        {
            throw new ArgumentException($"像素数据不够：需要 {required} 字节，只有 {pixels.Length} 字节");
        }

        int middleColumn = width / 2;
        int middleRow = height / 2;

        // 四块的累加值：下标 0 左上、1 右上、2 左下、3 右下
        var quarters = new ColorSum[4];
        int rowBytes = width * bytesPerPixel;
        for (int y = sampleStep / 2; y < height; y += sampleStep)
        {
            int rowStart = y * rowBytes;
            int rowQuarter = y < middleRow ? 0 : 2;
            for (int x = sampleStep / 2; x < width; x += sampleStep)
            {
                int i = rowStart + (x * bytesPerPixel);
                quarters[rowQuarter + (x < middleColumn ? 0 : 1)].Add(pixels[i + 2], pixels[i + 1], pixels[i]);
            }
        }

        ColorSum whole = quarters[0] + quarters[1] + quarters[2] + quarters[3];

        // 图片比取样间隔还小（几乎不会发生）：直接取正中间那个像素
        if (whole.Count == 0)
        {
            int center = (middleRow * rowBytes) + (middleColumn * bytesPerPixel);
            var only = new Rgb(pixels[center + 2], pixels[center + 1], pixels[center]);
            return new WallpaperColors(only, only, only, only, only);
        }

        Rgb wholeColor = whole.Average();
        return new WallpaperColors(
            Upper: (quarters[0] + quarters[1]).AverageOr(wholeColor),
            Lower: (quarters[2] + quarters[3]).AverageOr(wholeColor),
            Left: (quarters[0] + quarters[2]).AverageOr(wholeColor),
            Right: (quarters[1] + quarters[3]).AverageOr(wholeColor),
            Whole: wholeColor);
    }

    /// <summary>一块区域里取样点的红、绿、蓝累加值和点数。</summary>
    private struct ColorSum
    {
        public long R;
        public long G;
        public long B;
        public long Count;

        public void Add(byte r, byte g, byte b)
        {
            R += r;
            G += g;
            B += b;
            Count++;
        }

        public static ColorSum operator +(ColorSum a, ColorSum b) =>
            new() { R = a.R + b.R, G = a.G + b.G, B = a.B + b.B, Count = a.Count + b.Count };

        /// <summary>平均色（四舍五入）。调用前要确定 Count 大于 0。</summary>
        public readonly Rgb Average() => new(Rounded(R), Rounded(G), Rounded(B));

        /// <summary>平均色；这块一个点都没取到时（图片太窄或太矮）用 fallback。</summary>
        public readonly Rgb AverageOr(Rgb fallback) => Count == 0 ? fallback : Average();

        private readonly byte Rounded(long sum) => (byte)((sum + (Count / 2)) / Count);
    }
}
