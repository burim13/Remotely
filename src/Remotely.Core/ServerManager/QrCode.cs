using System.Text;

namespace Remotely.Core.ServerManager;

public enum QrEcc { Low = 0, Medium = 1, Quartile = 2, High = 3 }

/// <summary>
/// A small QR Code encoder (ISO/IEC 18004, byte mode only, versions 1 to 40, all four error-correction levels). Written for the Server Manager's invite page
/// and independent of any other QR code in this repository. Rendering is left to the caller: <see cref="GetModule"/> says whether a square is dark.
/// </summary>
public sealed class QrCode
{
    private readonly bool[,] _modules;     // [y, x], true = dark
    private readonly bool[,] _isFunction;

    public int Version { get; }
    public int Size { get; }
    public QrEcc Ecc { get; }
    public int Mask { get; private set; }

    public bool GetModule(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size && _modules[y, x];

    public static QrCode EncodeText(string text, QrEcc ecc = QrEcc.Medium) => EncodeBytes(Encoding.UTF8.GetBytes(text), ecc);

    /// <summary>Encodes bytes at the requested level, or a higher one when that still fits in the same version.</summary>
    public static QrCode EncodeBytes(byte[] data, QrEcc ecc = QrEcc.Medium)
    {
        ArgumentNullException.ThrowIfNull(data);
        int version;
        for (version = 1; ; version++)
        {
            if (version > 40) throw new ArgumentException("The text is too long for a QR code.", nameof(data));
            if (4 + CountBits(version) + data.Length * 8 <= DataCodewords(version, ecc) * 8) break;
        }
        foreach (var higher in new[] { QrEcc.Medium, QrEcc.Quartile, QrEcc.High })
            if (higher > ecc && 4 + CountBits(version) + data.Length * 8 <= DataCodewords(version, higher) * 8) ecc = higher;

        var bits = new List<bool>();
        Append(bits, 0b0100, 4);
        Append(bits, data.Length, CountBits(version));
        foreach (var b in data) Append(bits, b, 8);
        var capacity = DataCodewords(version, ecc) * 8;
        Append(bits, 0, Math.Min(4, capacity - bits.Count));
        Append(bits, 0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);

        var codewords = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return new QrCode(version, ecc, AddEccAndInterleave(codewords, version, ecc));
    }

    private QrCode(int version, QrEcc ecc, byte[] allCodewords)
    {
        Version = version;
        Ecc = ecc;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(allCodewords);

        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            var penalty = Penalty();
            if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
            ApplyMask(mask);   // XOR again: undo
        }
        Mask = bestMask;
        ApplyMask(bestMask);
        DrawFormatBits(bestMask);
    }

    private static void Append(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    // Index 0 of each row is unused; the index is the version.
    private static readonly int[][] EccPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    private static readonly int[][] BlockCount =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    public static int RawCodewords(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result / 8;
    }

    public static int DataCodewords(int version, QrEcc ecc) =>
        RawCodewords(version) - EccPerBlock[(int)ecc][version] * BlockCount[(int)ecc][version];

    public static (int EccPerBlock, int Blocks) BlockInfo(int version, QrEcc ecc) => (EccPerBlock[(int)ecc][version], BlockCount[(int)ecc][version]);

    private static byte[] AddEccAndInterleave(byte[] data, int version, QrEcc ecc)
    {
        var blocks = BlockCount[(int)ecc][version];
        var eccLen = EccPerBlock[(int)ecc][version];
        var raw = RawCodewords(version);
        var shortBlocks = blocks - raw % blocks;
        var shortLen = raw / blocks;
        var divisor = ReedSolomonDivisor(eccLen);
        var list = new List<byte[]>();
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var datLen = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
            var dat = data.AsSpan(k, datLen).ToArray();
            k += datLen;
            var e = ReedSolomonRemainder(dat, divisor);
            var block = new byte[shortLen + 1];
            dat.CopyTo(block, 0);
            e.CopyTo(block, shortLen + 1 - eccLen);   // short blocks keep a gap after the data, which is skipped below
            list.Add(block);
        }
        var result = new List<byte>(raw);
        for (var i = 0; i <= shortLen; i++)
            for (var j = 0; j < blocks; j++)
                if (i != shortLen - eccLen || j >= shortBlocks) result.Add(list[j][i]);
        return result.ToArray();
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    private static int Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);
        var positions = AlignmentPositions();
        for (var i = 0; i < positions.Length; i++)
            for (var j = 0; j < positions.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0)) continue;
                DrawAlignment(positions[i], positions[j]);
            }
        DrawFormatBits(0);   // reserve the area; the real bits are drawn once the mask is chosen
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size) SetFunction(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return [];
        var count = Version / 7 + 2;
        var step = Version == 32 ? 26 : (Version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void DrawFormatBits(int mask)
    {
        var eccBits = Ecc switch { QrEcc.Low => 1, QrEcc.Medium => 0, QrEcc.Quartile => 3, _ => 2 };
        var data = (eccBits << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = ((data << 10) | rem) ^ 0x5412;
        static bool Bit(int value, int i) => ((value >> i) & 1) != 0;
        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));
        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        var rem = Version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = (Version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < Size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (_isFunction[y, x] || i >= data.Length * 8) continue;
                    _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                    i++;
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (_isFunction[y, x]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) _modules[y, x] = !_modules[y, x];
            }
    }

    private static readonly bool[] FinderLikeA = [true, false, true, true, true, false, true, false, false, false, false];
    private static readonly bool[] FinderLikeB = [false, false, false, false, true, false, true, true, true, false, true];

    private int Penalty()
    {
        var penalty = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                var line = new bool[Size];
                for (var b = 0; b < Size; b++) line[b] = pass == 0 ? _modules[a, b] : _modules[b, a];
                var run = 1;
                for (var b = 1; b < Size; b++)
                {
                    if (line[b] == line[b - 1]) { run++; if (run == 5) penalty += 3; else if (run > 5) penalty++; }
                    else run = 1;
                }
                for (var b = 0; b + 11 <= Size; b++)
                    if (Matches(line, b, FinderLikeA) || Matches(line, b, FinderLikeB)) penalty += 40;
            }
        }
        for (var y = 0; y + 1 < Size; y++)
            for (var x = 0; x + 1 < Size; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) penalty += 3;
            }
        var dark = 0;
        foreach (var m in _modules) if (m) dark++;
        var total = Size * Size;
        penalty += ((Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1) * 10;
        return penalty;
    }

    private static bool Matches(bool[] line, int start, bool[] pattern)
    {
        for (var i = 0; i < pattern.Length; i++) if (line[start + i] != pattern[i]) return false;
        return true;
    }

    /// <summary>The code as rows of text, for tests and diagnostics: '#' dark, '.' light, no quiet zone.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++) sb.Append(_modules[y, x] ? '#' : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
