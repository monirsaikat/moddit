using System.Runtime.InteropServices;
using NetVips;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Modit.Engine.Host.Engines;

/// <summary>
/// Decodes the formats the libvips bundle cannot (BMP, ICO, and HEIC without an HEVC decoder) with the
/// Windows Imaging Component. Output is upright 8-bit sRGB, with alpha only when the image uses it.
/// </summary>
internal static class WicDecoder
{
    /// <summary>WINCODEC_ERR_COMPONENTNOTFOUND: no decoder installed for this format.</summary>
    private const int ComponentNotFound = unchecked((int)0x88982F50);

    private const string HevcStoreLink = "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7";

    public static ImageInfoResult GetInfo(string path, string format)
    {
        return Run(path, format, decoder =>
        {
            BitmapFrame frame = LargestFrame(decoder);
            bool alpha = frame.BitmapAlphaMode != BitmapAlphaMode.Ignore;
            return new ImageInfoResult(
                format, (int)frame.OrientedPixelWidth, (int)frame.OrientedPixelHeight, alpha ? 4 : 3, alpha,
                (int)decoder.FrameCount, frame.DpiX > 1 ? frame.DpiX : 96);
        });
    }

    public static Image Decode(string path, string format)
    {
        return Run(path, format, decoder =>
        {
            BitmapFrame frame = LargestFrame(decoder);
            PixelDataProvider data = frame.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Straight,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb)
                .AsTask().GetAwaiter().GetResult();
            byte[] pixels = data.DetachPixelData();
            int width = (int)frame.OrientedPixelWidth;
            int height = (int)frame.OrientedPixelHeight;
            bool usesAlpha = UsesAlpha(pixels);
            double pixelsPerMm = (frame.DpiX > 1 ? frame.DpiX : 96) / 25.4;

            using Image rgba = Image.NewFromMemory(pixels, width, height, 4, Enums.BandFormat.Uchar);
            using Image tagged = rgba.Copy(interpretation: Enums.Interpretation.Srgb, xres: pixelsPerMm, yres: pixelsPerMm);

            // Copy the pixels into libvips memory so nothing depends on the pinned managed array.
            using Image shaped = usesAlpha ? tagged.Copy() : tagged.ExtractBand(0, n: 3);
            return shaped.CopyMemory();
        });
    }

    private static T Run<T>(string path, string format, Func<BitmapDecoder, T> body)
    {
        try
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using IRandomAccessStream stream = file.AsRandomAccessStream();
            BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            return body(decoder);
        }
        catch (COMException ex) when (ex.HResult == ComponentNotFound && format == "heic")
        {
            throw new ModitException(
                ErrorCode.ConvertCodecMissing,
                $"Reading HEIC photos needs the HEVC Video Extensions from the Microsoft Store ({HevcStoreLink}).",
                ex);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new ModitException(
                ErrorCode.ConvertInputCorrupt, $"{Path.GetFileName(path)} could not be read: {ex.Message}", ex);
        }
    }

    private static BitmapFrame LargestFrame(BitmapDecoder decoder)
    {
        // An icon holds the same picture at several sizes; the biggest one is the best source.
        BitmapFrame? best = null;
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            BitmapFrame frame = decoder.GetFrameAsync(i).AsTask().GetAwaiter().GetResult();
            if (best is null || (long)frame.PixelWidth * frame.PixelHeight > (long)best.PixelWidth * best.PixelHeight)
            {
                best = frame;
            }
        }

        return best ?? throw new ModitException(ErrorCode.ConvertInputCorrupt, "The image contains no pictures.");
    }

    private static bool UsesAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 255)
            {
                return true;
            }
        }

        return false;
    }
}
