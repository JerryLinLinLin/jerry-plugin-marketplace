// Extracted and adapted from RaivenX b5971dda1fa2050dde36b8b22fda9726df92cb48. See THIRD-PARTY-NOTICES.md.
namespace HyperVControl.Extracted;

/// <summary>
/// Converts the raw RGB565 framebuffer that Hyper-V's <c>GetVirtualSystemThumbnailImage</c>
/// returns into 32-bit BGRA pixels ready for a <c>Format32bppArgb</c> bitmap.
/// </summary>
/// <remarks>
/// Lives here rather than in the broker so the bit math is unit-testable (this assembly is
/// <c>InternalsVisibleTo</c> the test project) and dependency-free — the broker owns the
/// System.Drawing PNG encode. Expansion replicates the high bits into the low ones
/// (5→8: <c>v&lt;&lt;3 | v&gt;&gt;2</c>, 6→8: <c>v&lt;&lt;2 | v&gt;&gt;4</c>) so full white
/// stays 255 rather than 248.
/// </remarks>
internal static class Rgb565Pixels
{
    /// <summary>BGRA byte order matches GDI+'s Format32bppArgb little-endian memory layout.</summary>
    internal static byte[] ToBgra32(ReadOnlySpan<byte> rgb565, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var pixels = width * height;

        // Hyper-V returns a few surplus bytes past the pixel data (a constant +4 observed
        // on Windows 11 26200 at every resolution request). The image starts at offset 0,
        // so surplus is a trailer to ignore, not a malformed frame; only missing pixel
        // data is unrenderable.
        if (rgb565.Length < pixels * 2)
        {
            throw new ArgumentException(
                $"Expected at least {pixels * 2} bytes of RGB565 data for {width}×{height}, got {rgb565.Length}.",
                nameof(rgb565));
        }

        var bgra = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            // Little-endian ushort per pixel: rrrrrggg gggbbbbb.
            var value = (ushort)(rgb565[i * 2] | (rgb565[(i * 2) + 1] << 8));
            var red = (value >> 11) & 0x1F;
            var green = (value >> 5) & 0x3F;
            var blue = value & 0x1F;

            bgra[i * 4] = (byte)((blue << 3) | (blue >> 2));
            bgra[(i * 4) + 1] = (byte)((green << 2) | (green >> 4));
            bgra[(i * 4) + 2] = (byte)((red << 3) | (red >> 2));
            bgra[(i * 4) + 3] = 0xFF;
        }

        return bgra;
    }
}
