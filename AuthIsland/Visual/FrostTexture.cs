using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AuthIsland.Visual;

/// <summary>
/// 磨砂颗粒纹理。
///
/// 做法：生成灰度噪声位图，当作白色矩形的 OpacityMask —— 亮的地方透出白、
/// 暗的地方透过去，视觉上就是玻璃表面那层细密的霜。
///
/// 为什么用两层：单张噪声平铺会在胶囊这种窄长形状上露出明显的重复规律，
/// 两层周期互质（83 / 127）叠起来，肉眼基本看不出接缝。
/// </summary>
internal static class FrostTexture
{
    public static ImageBrush Create(int tile, int seed, double opacity)
    {
        var pixels = new byte[tile * tile];
        var rnd = new Random(seed);

        for (int i = 0; i < pixels.Length; i++)
        {
            // 两个均匀随机数取平均 → 三角分布：大部分落在中灰，
            // 少数偏亮/偏暗，比纯均匀噪声更像真实的磨砂颗粒。
            double v = (rnd.NextDouble() + rnd.NextDouble()) * 0.5;

            // 再压一下对比，避免出现刺眼的单像素亮点
            v = 0.5 + (v - 0.5) * 0.85;

            pixels[i] = (byte)Math.Clamp(v * 255.0, 0, 255);
        }

        var bmp = BitmapSource.Create(
            tile, tile, 96, 96, PixelFormats.Gray8, null, pixels, tile);
        bmp.Freeze();

        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, tile, tile),
            Stretch = Stretch.None,
            Opacity = opacity
        };
        brush.Freeze();

        return brush;
    }
}
