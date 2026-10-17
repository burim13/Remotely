using System.Text;

namespace Remotely.Tests;

/// <summary>
/// A deterministic, seeded input generator for the parser tests. Same seed and same iteration count give the same inputs on every machine, so a failure
/// can be reproduced from the seed and iteration printed in the failure message. This is a property-test harness with a fixed budget, not coverage-guided
/// fuzzing (it has no feedback from the code under test).
/// The budget is <see cref="DefaultIterations"/> per target; set the environment variable REMOTELY_FUZZ_ITERATIONS to run more.
/// </summary>
internal static class Fuzz
{
    public const int DefaultIterations = 4000;

    public static int Budget
    {
        get
        {
            var text = Environment.GetEnvironmentVariable("REMOTELY_FUZZ_ITERATIONS");
            return int.TryParse(text, out var n) && n is > 0 and <= 10_000_000 ? n : DefaultIterations;
        }
    }

    /// <summary>Mixes pure noise, mutated copies of valid samples, truncations, extensions, splices and boundary-valued length fields.</summary>
    public static IEnumerable<byte[]> Inputs(int seed, int count, IReadOnlyList<byte[]> valid, int maxRandomLength = 600)
    {
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            byte[] sample = valid.Count == 0 ? [] : valid[rng.Next(valid.Count)];
            switch (valid.Count == 0 ? 0 : rng.Next(8))
            {
                case 0: yield return Noise(rng, maxRandomLength); break;
                case 1: yield return FlipBits(rng, sample); break;
                case 2: yield return sample[..rng.Next(sample.Length + 1)]; break;
                case 3: yield return [.. sample, .. Noise(rng, 32)]; break;
                case 4:
                    var other = valid[rng.Next(valid.Count)];
                    yield return [.. sample[..rng.Next(sample.Length + 1)], .. other[rng.Next(other.Length + 1)..]];
                    break;
                case 5: yield return Boundary(rng, sample); break;
                case 6: yield return FlipBits(rng, FlipBits(rng, sample)); break;
                default: yield return ZeroRange(rng, sample); break;
            }
        }
    }

    public static byte[] Noise(Random rng, int maxLength)
    {
        var length = rng.Next(4) == 0 ? rng.Next(maxLength + 1) : rng.Next(Math.Min(maxLength, 48) + 1);
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    private static byte[] FlipBits(Random rng, byte[] sample)
    {
        var copy = (byte[])sample.Clone();
        if (copy.Length == 0) return copy;
        for (var n = rng.Next(1, 9); n > 0; n--) copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
        return copy;
    }

    private static byte[] Boundary(Random rng, byte[] sample)
    {
        var copy = (byte[])sample.Clone();
        if (copy.Length < 4) return copy;
        var at = rng.Next(copy.Length - 3);
        uint[] values = [0, 1, 0xFF, 0xFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, (uint)copy.Length, (uint)copy.Length - 1, (uint)copy.Length + 1, 4096, 4097, 1 << 20, (1 << 20) + 65];
        var v = values[rng.Next(values.Length)];
        copy[at] = (byte)(v >> 24); copy[at + 1] = (byte)(v >> 16); copy[at + 2] = (byte)(v >> 8); copy[at + 3] = (byte)v;
        return copy;
    }

    private static byte[] ZeroRange(Random rng, byte[] sample)
    {
        var copy = (byte[])sample.Clone();
        if (copy.Length == 0) return copy;
        var start = rng.Next(copy.Length);
        Array.Clear(copy, start, rng.Next(copy.Length - start + 1));
        return copy;
    }

    /// <summary>Runs <paramref name="target"/> on every input. Any exception that the target lets through fails the test with the seed, the iteration and the input.</summary>
    public static void Run(string name, int seed, IEnumerable<byte[]> inputs, Action<byte[]> target)
    {
        var i = 0;
        foreach (var input in inputs)
        {
            try { target(input); }
            catch (Exception ex)
            {
                Xunit.Assert.Fail($"{name}: seed={seed} iteration={i} input({input.Length} bytes)={Convert.ToHexString(input.AsSpan(0, Math.Min(input.Length, 256)))} threw {ex.GetType().Name}: {ex.Message}");
            }
            i++;
        }
    }

    /// <summary>Fixed-seed random text built from <paramref name="alphabet"/>, with noise characters mixed in.</summary>
    public static IEnumerable<string> Texts(int seed, int count, string alphabet, int maxLength = 40)
    {
        var rng = new Random(seed);
        const string noise = " -_=+/\\\"'\t\r\néЖ中0O1Il";
        for (var i = 0; i < count; i++)
        {
            var sb = new StringBuilder();
            for (var n = rng.Next(maxLength + 1); n > 0; n--)
                sb.Append(rng.Next(6) == 0 ? noise[rng.Next(noise.Length)] : alphabet[rng.Next(alphabet.Length)]);
            yield return sb.ToString();
        }
    }
}
