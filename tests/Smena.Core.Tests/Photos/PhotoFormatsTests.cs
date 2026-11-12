using System.Text;
using Smena.Core.Photos;

namespace Smena.Core.Tests.Photos;

public sealed class PhotoFormatsTests
{
    [Fact]
    public void Jpeg_png_webp_and_heic_are_recognised_by_their_first_bytes()
    {
        Assert.Equal(PhotoFormat.Jpeg, PhotoFormats.Detect([0xFF, 0xD8, 0xFF, 0xE1, 0x00]));
        Assert.Equal(PhotoFormat.Png, PhotoFormats.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]));
        Assert.Equal(PhotoFormat.WebP, PhotoFormats.Detect(Encoding.ASCII.GetBytes("RIFF\u0010\u0000\u0000\u0000WEBPVP8 ")));
        Assert.Equal(PhotoFormat.Heic, PhotoFormats.Detect(Encoding.ASCII.GetBytes("\u0000\u0000\u0000\u0018ftypheic\u0000\u0000")));
        Assert.Equal(PhotoFormat.Heic, PhotoFormats.Detect(Encoding.ASCII.GetBytes("\u0000\u0000\u0000\u0018ftypmif1\u0000\u0000")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("GIF89a")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("%PDF-1.7")]
    [InlineData("RIFF\u0010\u0000\u0000\u0000WAVEfmt ")]
    [InlineData("\u0000\u0000\u0000\u0018ftypisom\u0000\u0000")] // видео mp4
    [InlineData("push graphic-context")]
    public void Everything_else_is_unknown(string content)
    {
        Assert.Equal(PhotoFormat.Unknown, PhotoFormats.Detect(Encoding.ASCII.GetBytes(content)));
    }

    [Fact]
    public void Truncated_signatures_are_unknown()
    {
        Assert.Equal(PhotoFormat.Unknown, PhotoFormats.Detect([0xFF, 0xD8]));
        Assert.Equal(PhotoFormat.Unknown, PhotoFormats.Detect("RIFF"u8));
    }
}
