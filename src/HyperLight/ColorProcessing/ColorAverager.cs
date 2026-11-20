namespace HyperLight.ColorProcessing;

/// <summary>
/// 算一张截图的平均色。
/// 不用每个像素都读：每隔几个像素取一个点，4K 图只读十几万个点，结果几乎一样，速度快得多。
/// </summary>
internal static class ColorAverager
{
    /// <summary>每隔多少个像素取一个点（横竖都是）。4K 图取 8，约读 13 万个点。</summary>
    private const int sampleStep = 8;

    /// <summary>每个像素占 4 字节，顺序是 B、G、R、未使用（Windows 截图的标准格式）。</summary>
    private const int bytesPerPixel = 4;

    /// <summary>按取色方式算麦克风两个灯区的颜色。</summary>
    /// <exception cref="ArgumentException">尺寸不对，或者像素数据比尺寸要求的少（这是程序写错了，不是设备问题）。</exception>
    public static ZoneColors AverageZones(ReadOnlySpan<byte> pixels, int width, int height, ZoneLayout layout)
    {
        switch (layout)
        {
            case ZoneLayout.TopBottom when height >= 2:
                int middleRow = height / 2;
                return new ZoneColors(
                    Upper: AverageRegion(pixels, width, height, 0, width, 0, middleRow),
                    Lower: AverageRegion(pixels, width, height, 0, width, middleRow, height));

            case ZoneLayout.LeftRight when width >= 2:
                int middleColumn = width / 2;
                return new ZoneColors(
                    Upper: AverageRegion(pixels, width, height, 0, middleColumn, 0, height),
                    Lower: AverageRegion(pixels, width, height, middleColumn, width, 0, height));

            default:
                // 整张壁纸；或者图片太小没法分两半（几乎不会发生）
                return ZoneColors.Same(Average(pixels, width, height));
        }
    }

    /// <summary>算整张图的平均色。pixels 是从上到下逐行排列的像素，每行 width × 4 字节。</summary>
    /// <exception cref="ArgumentException">尺寸不对，或者像素数据比尺寸要求的少。</exception>
    public static Rgb Average(ReadOnlySpan<byte> pixels, int width, int height) =>
        AverageRegion(pixels, width, height, 0, width, 0, height);

    /// <summary>
    /// 算一个矩形区域的平均色：第 firstColumn 列（含）到 endColumn 列（不含），第 firstRow 行（含）到 endRow 行（不含）。
    /// </summary>
    /// <exception cref="ArgumentException">尺寸或区域范围不对，或者像素数据比尺寸要求的少。</exception>
    public static Rgb AverageRegion(
        ReadOnlySpan<byte> pixels, int width, int height, int firstColumn, int endColumn, int firstRow, int endRow)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"图片尺寸不对：{width}x{height}");
        }

        if (firstColumn < 0 || endColumn > width || firstColumn >= endColumn
            || firstRow < 0 || endRow > height || firstRow >= endRow)
        {
            throw new ArgumentException($"区域范围不对：列 {firstColumn}～{endColumn}，行 {firstRow}～{endRow}（图片 {width}x{height}）");
        }

        long required = (long)width * height * bytesPerPixel;
        if (pixels.Length < required)
        {
            throw new ArgumentException($"像素数据不够：需要 {required} 字节，只有 {pixels.Length} 字节");
        }

        long sumR = 0, sumG = 0, sumB = 0, count = 0;
        int rowBytes = width * bytesPerPixel;
        for (int y = firstRow + (sampleStep / 2); y < endRow; y += sampleStep)
        {
            int rowStart = y * rowBytes;
            for (int x = firstColumn + (sampleStep / 2); x < endColumn; x += sampleStep)
            {
                int i = rowStart + (x * bytesPerPixel);
                sumB += pixels[i];
                sumG += pixels[i + 1];
                sumR += pixels[i + 2];
                count++;
            }
        }

        // 区域比取样间隔还小时（几乎不会发生），直接取区域正中间那个像素
        if (count == 0)
        {
            int center = (((firstRow + endRow) / 2) * rowBytes) + (((firstColumn + endColumn) / 2) * bytesPerPixel);
            return new Rgb(pixels[center + 2], pixels[center + 1], pixels[center]);
        }

        return new Rgb(RoundedAverage(sumR, count), RoundedAverage(sumG, count), RoundedAverage(sumB, count));
    }

    private static byte RoundedAverage(long sum, long count) => (byte)((sum + (count / 2)) / count);
}
