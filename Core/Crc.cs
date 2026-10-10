namespace ZerkSense5;

static class Crc
{
    static readonly uint[] Table = BuildTable();

    const uint SeedOutput = 0xEADA2D49;
    const uint SeedFeature = 0x2060EFC3;

    static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int b = 0; b < 8; b++)
                c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1));
            t[i] = c;
        }
        return t;
    }

    static uint Seeded(ReadOnlySpan<byte> data, uint seed)
    {
        uint crc = ~seed;
        foreach (byte b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return ~crc;
    }

    static void Store(Span<byte> report, uint crc)
    {
        int n = report.Length;
        report[n - 4] = (byte)crc;
        report[n - 3] = (byte)(crc >> 8);
        report[n - 2] = (byte)(crc >> 16);
        report[n - 1] = (byte)(crc >> 24);
    }

    public static void FillOutput(Span<byte> report) =>
        Store(report, Seeded(report[..^4], SeedOutput));

    public static void FillFeature(Span<byte> report) =>
        Store(report, Seeded(report[..^4], SeedFeature));
}
