using System.Text;
using Remotely.Core.Lan.Consent;

namespace Remotely.Tests;

public class QrCodeTests
{
    [Fact]
    public void A_token_fits_the_smallest_code_and_the_fixed_patterns_are_where_the_standard_puts_them()
    {
        var qr = QrCode.Encode("K7P4-4NX2-QMH9");
        Assert.Equal(1, qr.Version);
        Assert.Equal(21, qr.Size);
        // finder pattern: a dark 7x7 ring, a light ring, a dark 3x3 centre, in three corners
        foreach (var (ox, oy) in new[] { (0, 0), (14, 0), (0, 14) })
        {
            for (var i = 0; i < 7; i++)
            {
                Assert.True(qr[ox + i, oy]); Assert.True(qr[ox + i, oy + 6]);
                Assert.True(qr[ox, oy + i]); Assert.True(qr[ox + 6, oy + i]);
            }
            Assert.False(qr[ox + 1, oy + 1]); Assert.True(qr[ox + 3, oy + 3]);
        }
        Assert.True(qr[8, qr.Size - 8]);   // the always-dark module
        for (var i = 8; i < 13; i++) Assert.Equal(i % 2 == 0, qr[i, 6]);   // timing pattern
    }

    [Fact]
    public void The_version_grows_with_the_text_and_too_long_text_is_refused()
    {
        Assert.Equal(2, QrCode.Encode(new string('A', 15)).Version);
        Assert.True(QrCode.Encode(new string('A', 150)).Version >= 7);
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('A', 400)));
    }

    [Fact]
    public void The_same_text_gives_the_same_picture()
    {
        var a = QrCode.Encode("hello");
        var b = QrCode.Encode("hello");
        for (var y = 0; y < a.Size; y++) for (var x = 0; x < a.Size; x++) Assert.Equal(a[x, y], b[x, y]);
    }

    /// <summary>
    /// Not an assertion: when QR_DUMP_DIR is set, writes one PBM image per case so an independent QR reader can decode them (done by hand on the build machine).
    /// </summary>
    [Fact]
    public void Dump_images_for_an_independent_reader_when_asked()
    {
        var dir = Environment.GetEnvironmentVariable("QR_DUMP_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var rng = new Random(7);
        var cases = new List<string> { "K7P4-4NX2-QMH9", "A", "hello world", "remotely://K7P4-4NX2-QMH9" };
        for (var length = 1; length <= 213; length += 1) cases.Add(new string(Enumerable.Range(0, length).Select(_ => (char)rng.Next('!', '~')).ToArray()));
        var n = 0;
        foreach (var level in Enum.GetValues<QrErrorCorrection>())
            foreach (var text in cases)
            {
                QrCode qr;
                try { qr = QrCode.Encode(text, level); } catch (ArgumentException) { continue; }
                var sb = new StringBuilder();
                var border = 4;
                var size = qr.Size + 2 * border;
                sb.Append("P1\n").Append(size).Append(' ').Append(size).Append('\n');
                for (var y = -border; y < qr.Size + border; y++)
                {
                    for (var x = -border; x < qr.Size + border; x++) sb.Append(qr[x, y] ? '1' : '0').Append(' ');
                    sb.Append('\n');
                }
                File.WriteAllText(Path.Combine(dir, $"{n:D4}_{(int)level}_v{qr.Version}.pbm"), sb.ToString());
                File.WriteAllText(Path.Combine(dir, $"{n:D4}_{(int)level}_v{qr.Version}.txt"), text, new UTF8Encoding(false));
                n++;
            }
    }
}
