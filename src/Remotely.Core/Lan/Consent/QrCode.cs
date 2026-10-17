namespace Remotely.Core.Lan.Consent;

public enum QrErrorCorrection { Low = 0, Medium = 1, Quartile = 2, High = 3 }

/// <summary>
/// A small QR Code (Model 2) encoder written for this project, so no package is needed: byte mode, versions 1 to 10, error correction level chosen by the caller
/// (the token screen uses Medium). It follows ISO/IEC 18004 (Reed-Solomon over GF(256) with polynomial 0x11D, the eight masks with the four penalty rules, BCH-coded
/// format and version information). The tests decode its output with an independent reader on the build machine; see docs/share-host.md for what was and was not checked.
/// </summary>
public sealed class QrCode
{
    public const int MaxVersion = 10;

    // Index: error correction level (L, M, Q, H), then version 1..10 (index 0 unused).
    private static readonly int[][] EccPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28],
    ];
    private static readonly int[][] BlockCount =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8],
    ];
    private static readonly int[] FormatBits = [1, 0, 3, 2];   // L, M, Q, H

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    public int Version { get; }
    public int Size { get; }
    public int Mask { get; private set; }

    /// <summary>True for a dark module. x is the column, y the row.</summary>
    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];

    public static QrCode Encode(string text, QrErrorCorrection ecc = QrErrorCorrection.Medium)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        var level = (int)ecc;
        for (var version = 1; version <= MaxVersion; version++)
        {
            var capacityBits = DataCodewords(version, level) * 8;
            var countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + data.Length * 8 <= capacityBits) return new QrCode(version, level, data);
        }
        throw new ArgumentException("The text is too long for a QR code of this program (at most " + (DataCodewords(MaxVersion, level) - 3) + " bytes at this error correction level).");
    }

    private QrCode(int version, int level, byte[] data)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
        DrawFunctionPatterns(level);
        var codewords = AddEccAndInterleave(MakeDataCodewords(version, level, data), version, level);
        DrawCodewords(codewords);

        var best = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(level, mask);
            var penalty = Penalty();
            if (penalty < bestPenalty) { best = mask; bestPenalty = penalty; }
            ApplyMask(mask);   // undo
        }
        Mask = best;
        ApplyMask(best);
        DrawFormatBits(level, best);
    }

    // ---- sizes ----

    private static int RawModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version, int level) =>
        RawModules(version) / 8 - EccPerBlock[level][version] * BlockCount[level][version];

    // ---- data ----

    private static byte[] MakeDataCodewords(int version, int level, byte[] data)
    {
        var bits = new List<bool>();
        void Append(int value, int count) { for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(0b0100, 4);
        Append(data.Length, version <= 9 ? 8 : 16);
        foreach (var b in data) Append(b, 8);
        var capacity = DataCodewords(version, level) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        var result = new byte[capacity / 8];
        for (var i = 0; i < bits.Count; i++) if (bits[i]) result[i >> 3] |= (byte)(0x80 >> (i & 7));
        var pad = true;
        for (var i = bits.Count / 8; i < result.Length; i++, pad = !pad) result[i] = pad ? (byte)0xEC : (byte)0x11;
        return result;
    }

    private static byte[] AddEccAndInterleave(byte[] data, int version, int level)
    {
        var blocks = BlockCount[level][version];
        var eccLen = EccPerBlock[level][version];
        var raw = RawModules(version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLen = raw / blocks;
        var generator = ReedSolomonGenerator(eccLen);
        var parts = new List<(byte[] Data, byte[] Ecc)>();
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var length = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
            var block = data[k..(k + length)];
            k += length;
            parts.Add((block, ReedSolomonRemainder(block, generator)));
        }
        var result = new List<byte>();
        for (var i = 0; i < parts.Max(p => p.Data.Length); i++)
            foreach (var p in parts)
                if (i < p.Data.Length) result.Add(p.Data[i]);
        for (var i = 0; i < eccLen; i++)
            foreach (var p in parts) result.Add(p.Ecc[i]);
        return [.. result];
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private static byte[] ReedSolomonGenerator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] generator)
    {
        var result = new byte[generator.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(generator[i], factor);
        }
        return result;
    }

    // ---- drawing ----

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private void DrawFunctionPatterns(int level)
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
        for (var i = 0; i < positions.Count; i++)
            for (var j = 0; j < positions.Count; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == positions.Count - 1) || (i == positions.Count - 1 && j == 0)) continue;
                DrawAlignment(positions[i], positions[j]);
            }

        DrawFormatBits(level, 0);   // reserves the area; redrawn with the real mask later
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(x, y, distance != 2 && distance != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private List<int> AlignmentPositions()
    {
        if (Version == 1) return [];
        var count = Version / 7 + 2;
        var step = (Version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        var result = new List<int> { 6 };
        for (var position = Size - 7; result.Count < count; position -= step) result.Insert(1, position);
        return result;
    }

    private void DrawFormatBits(int level, int mask)
    {
        var data = FormatBits[level] << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(i));
        SetFunction(8, 7, Bit(6));
        SetFunction(8, 8, Bit(7));
        SetFunction(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(i));

        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(i));
        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        var rem = Version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = Version << 12 | rem;
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
            for (var vertical = 0; vertical < Size; vertical++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vertical : vertical;
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
                if (invert && !_isFunction[y, x]) _modules[y, x] = !_modules[y, x];
            }
    }

    // ---- penalty (the four rules of the standard) ----

    private int Penalty()
    {
        var result = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            bool Get(int a, int b) => pass == 0 ? _modules[a, b] : _modules[b, a];
            for (var a = 0; a < Size; a++)
            {
                var run = 1;
                for (var b = 1; b < Size; b++)
                {
                    if (Get(a, b) == Get(a, b - 1)) { run++; continue; }
                    if (run >= 5) result += 3 + (run - 5);
                    run = 1;
                }
                if (run >= 5) result += 3 + (run - 5);

                for (var b = 0; b + 11 <= Size; b++)
                {
                    if (Matches(a, b, [true, false, true, true, true, false, true, false, false, false, false], Get)
                        || Matches(a, b, [false, false, false, false, true, false, true, true, true, false, true], Get)) result += 40;
                }
            }
        }
        for (var y = 0; y + 1 < Size; y++)
            for (var x = 0; x + 1 < Size; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) result += 3;
            }
        var dark = 0;
        foreach (var m in _modules) if (m) dark++;
        var total = Size * Size;
        result += (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1 > 0 ? ((Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1) * 10 : 0;
        return result;
    }

    private static bool Matches(int a, int start, bool[] pattern, Func<int, int, bool> get)
    {
        for (var i = 0; i < pattern.Length; i++) if (get(a, start + i) != pattern[i]) return false;
        return true;
    }
}
