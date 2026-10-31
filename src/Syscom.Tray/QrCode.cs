using System.Text;

namespace Syscom.Tray;

/// <summary>
/// Minimal QR code encoder (byte mode, error correction level M, versions 1–10, up to ~210 bytes) so the tray app
/// can show the phone-setup link as a QR code without a library. Follows ISO/IEC 18004; structure after Project
/// Nayuki's reference implementation.
/// </summary>
internal sealed class QrCode
{
    private static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };   // level M
    private static readonly int[] NumBlocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };              // level M

    public int Size { get; }
    private readonly bool[,] _modules;     // [y, x]
    private readonly bool[,] _isFunction;
    private readonly int _version;

    public bool this[int x, int y] => _modules[y, x];

    public static QrCode Encode(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        for (var ver = 1; ver <= 10; ver++)
        {
            var ccBits = ver <= 9 ? 8 : 16;
            var capacityBits = DataCodewords(ver) * 8;
            if (4 + ccBits + data.Length * 8 <= capacityBits) return new QrCode(ver, data, ccBits);
        }
        throw new ArgumentException("Text too long for a QR code here.");
    }

    private QrCode(int version, byte[] data, int ccBits)
    {
        _version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        // ---- bit stream: mode 0100 (byte), count, data, terminator, padding
        var bits = new List<bool>();
        void Append(int value, int len) { for (var i = len - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(0b0100, 4);
        Append(data.Length, ccBits);
        foreach (var b in data) Append(b, 8);
        var capacity = DataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);
        var codewords = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(codewords));

        // Pick the mask with the lowest penalty.
        var best = 0;
        var bestScore = int.MaxValue;
        for (var m = 0; m < 8; m++)
        {
            ApplyMask(m);
            DrawFormatBits(m);
            var score = Penalty();
            if (score < bestScore) { best = m; bestScore = score; }
            ApplyMask(m); // undo (XOR)
        }
        ApplyMask(best);
        DrawFormatBits(best);
    }

    // ---- function patterns

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

        var pos = AlignmentPositions();
        var n = pos.Length;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                if (!((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)))
                    DrawAlignment(pos[i], pos[j]);

        DrawFormatBits(0); // reserve; real bits drawn after masking
        DrawVersion();
    }

    private void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void DrawFormatBits(int mask)
    {
        var data = (0b00 << 3) | mask; // level M = 00
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = ((data << 10) | rem) ^ 0x5412;

        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true); // dark module
    }

    private void DrawVersion()
    {
        if (_version < 7) return;
        var rem = _version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = (_version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private int[] AlignmentPositions()
    {
        if (_version == 1) return Array.Empty<int>();
        var n = _version / 7 + 2;
        var step = (int)Math.Ceiling((_version * 4 + 4) / (double)(n * 2 - 2)) * 2;
        var result = new int[n];
        result[0] = 6;
        for (int i = n - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    // ---- data

    private static int RawDataModules(int ver)
    {
        var result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            var numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int ver) => RawDataModules(ver) / 8 - EccPerBlock[ver] * NumBlocks[ver];

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int numBlocks = NumBlocks[_version], eccLen = EccPerBlock[_version];
        var raw = RawDataModules(_version) / 8;
        var numShort = numBlocks - raw % numBlocks;
        var shortLen = raw / numBlocks;
        var divisor = RsDivisor(eccLen);

        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
            var dat = data.AsSpan(k, datLen).ToArray();
            k += datLen;
            var ecc = RsRemainder(dat, divisor);
            var block = new byte[shortLen + 1];
            dat.CopyTo(block, 0);
            ecc.CopyTo(block, shortLen + 1 - eccLen);
            blocks.Add(block);
        }

        var result = new List<byte>(raw);
        for (var i = 0; i < blocks[0].Length; i++)
            for (var j = 0; j < blocks.Count; j++)
                if (i != shortLen - eccLen || j >= numShort) result.Add(blocks[j][i]);
        return result.ToArray();
    }

    private static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = (byte)GfMul(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMul(root, 0x02);
        }
        return result;
    }

    private static byte[] RsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= (byte)GfMul(divisor[i], factor);
        }
        return result;
    }

    private static int GfMul(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < Size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
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

    /// <summary>Standard penalty rules (runs, 2×2 blocks, finder look-alikes, dark balance).</summary>
    private int Penalty()
    {
        var score = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                var run = 0;
                var prev = false;
                for (var b = 0; b < Size; b++)
                {
                    var m = pass == 0 ? _modules[a, b] : _modules[b, a];
                    if (b > 0 && m == prev) { run++; if (run == 5) score += 3; else if (run > 5) score++; }
                    else run = 1;
                    prev = m;
                }
                // finder-like 1:1:3:1:1 with 4 light modules on one side
                for (var b = -4; b < Size; b++)
                {
                    bool At(int k) { var p = b + k; return p >= 0 && p < Size && (pass == 0 ? _modules[a, p] : _modules[p, a]); }
                    bool core = At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
                    if (!core) continue;
                    if (!At(0) && !At(1) && !At(2) && !At(3)) score += 40;
                    if (!At(11) && !At(12) && !At(13) && !At(14)) score += 40;
                }
            }
        }
        for (var y = 0; y < Size - 1; y++)
            for (var x = 0; x < Size - 1; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) score += 3;
            }
        var dark = 0;
        foreach (var m in _modules) if (m) dark++;
        var total = Size * Size;
        var k2 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return score + Math.Max(0, k2) * 10;
    }

    private static bool Bit(int x, int i) => ((x >> i) & 1) != 0;
}
