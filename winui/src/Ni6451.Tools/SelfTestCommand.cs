using System.Globalization;
using System.IO.Compression;
using System.Text;
using Ni6451.Core;

namespace Ni6451.Tools;

/// <summary>
/// Platform-independent checks over the parts of the rewrite that do not touch hardware:
/// the rolling buffer semantics, the sensor conversions, and -- most importantly -- the
/// exact on-disk layout of the <c>.npz</c> recordings, which the Python tooling on the
/// <c>main</c> branch has to keep being able to read.
/// </summary>
internal static class SelfTestCommand
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        TestNpyHeaderLayout();
        TestNpzRoundTrip();
        TestFinalizeJobProducesReadableArchive();
        TestRollingBuffer();
        TestUnitConversion();

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    // ---------- .npy / .npz container ----------

    private static void TestNpyHeaderLayout()
    {
        using var ms = new MemoryStream();
        NpyFormat.WriteHeader(ms, NpyFormat.Float64Descr, 3);
        byte[] bytes = ms.ToArray();

        Check("npy: magic", bytes[0] == 0x93 && Encoding.ASCII.GetString(bytes, 1, 5) == "NUMPY");
        Check("npy: version 1.0", bytes[6] == 1 && bytes[7] == 0);
        Check("npy: data starts on a 64-byte boundary", bytes.Length % 64 == 0);

        string header = Encoding.ASCII.GetString(bytes, 10, bytes.Length - 10);
        Check("npy: header dict matches numpy's repr",
            header.StartsWith("{'descr': '<f8', 'fortran_order': False, 'shape': (3,), }", StringComparison.Ordinal));
        Check("npy: header is newline-terminated", header.EndsWith('\n'));

        using var scalar = new MemoryStream();
        NpyFormat.WriteHeader(scalar, NpyFormat.Int64Descr);
        string scalarHeader = Encoding.ASCII.GetString(scalar.ToArray(), 10, (int)scalar.Length - 10);
        Check("npy: 0-D shape is ()",
            scalarHeader.StartsWith("{'descr': '<i8', 'fortran_order': False, 'shape': (), }", StringComparison.Ordinal));
        Check("npy: 0-D data starts on a 64-byte boundary", scalar.Length % 64 == 0);
    }

    private static void TestNpzRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ni6451_selftest_{Guid.NewGuid():N}.npz");
        try
        {
            double[] ai0 = [0.0, 1.5, -2.25, 3.125];
            using (var w = new NpzWriter(path))
            {
                w.AddFloat64Array("ai0", ai0);
                w.AddInt64Array("channels", [0L]);
                w.AddInt64Scalar("sample_rate", AppConfig.Rate);
                w.AddInt64Scalar("trigger_sample_index", -1);
            }

            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                Check("npz: members are stored uncompressed",
                    zip.Entries.All(e => e.CompressedLength == e.Length));
                Check("npz: member names carry the .npy suffix",
                    zip.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
                       .SequenceEqual(["ai0.npy", "channels.npy", "sample_rate.npy", "trigger_sample_index.npy"]));
            }

            using var r = new NpzReader(path);
            Check("npz: float64 array round-trips", r.ReadFloat64Array("ai0").SequenceEqual(ai0));
            Check("npz: int64 array round-trips", r.ReadInt64Array("channels").SequenceEqual([0L]));
            Check("npz: scalar round-trips", r.ReadInt64Scalar("sample_rate") == AppConfig.Rate);
            Check("npz: missing trigger is stored as -1", r.ReadInt64Scalar("trigger_sample_index") == -1);
            Check("npz: scalar has 0-D shape", r.GetHeader("sample_rate").Shape.Length == 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void TestFinalizeJobProducesReadableArchive()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"ni6451_selftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            int[] channels = [0, 3, 7];
            const int samples = 1000;

            var spool = new ChannelSpool(workDir, channels);
            var chunk = new double[channels.Length * samples];
            for (int c = 0; c < channels.Length; c++)
                for (int i = 0; i < samples; i++)
                    chunk[c * samples + i] = channels[c] + i * 0.001;

            spool.Write(chunk, samples);
            spool.FlushToDisk();
            spool.CloseFiles();

            string outPath = FinalizeJob.Run(new FinalizeRequest(
                spool.TempDir, spool.TotalSamplesWritten, workDir, channels, 12_345L, "0207", 5));

            Check("finalize: file name follows the T{SH}-raw-run{RN}-{timestamp}.npz pattern",
                Path.GetFileName(outPath).StartsWith("T0207-raw-run5-", StringComparison.Ordinal)
                && outPath.EndsWith(".npz", StringComparison.Ordinal));
            Check("finalize: spool directory is removed on success", !Directory.Exists(spool.TempDir));

            using var r = new NpzReader(outPath);
            Check("finalize: every selected channel is present",
                channels.All(c => r.Names.Contains($"ai{c}")));
            Check("finalize: channels array matches the selection",
                r.ReadInt64Array("channels").SequenceEqual(channels.Select(c => (long)c)));
            Check("finalize: trigger index is preserved", r.ReadInt64Scalar("trigger_sample_index") == 12_345L);
            Check("finalize: sample_rate is AppConfig.Rate", r.ReadInt64Scalar("sample_rate") == AppConfig.Rate);

            double[] ai3 = r.ReadFloat64Array("ai3");
            Check("finalize: channel data survives the spool round-trip",
                ai3.Length == samples && Math.Abs(ai3[999] - (3 + 0.999)) < 1e-12);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    // ---------- rolling buffer ----------

    private static void TestRollingBuffer()
    {
        var buf = new RollingBuffer(2, 5);
        var dest = new double[2 * 5];

        Check("buffer: starts empty", buf.GetLast(5, dest) == 0);

        // channel-major: [ch0 samples..., ch1 samples...]
        buf.Push([1, 2, 3, 11, 12, 13], 3);
        int n = buf.GetLast(5, dest);
        Check("buffer: returns only what it holds", n == 3);
        Check("buffer: keeps channels separate",
            dest[0] == 1 && dest[1] == 2 && dest[2] == 3 && dest[3] == 11 && dest[4] == 12 && dest[5] == 13);

        // Wrap around: capacity 5, 3 already written, 4 more.
        buf.Push([4, 5, 6, 7, 14, 15, 16, 17], 4);
        n = buf.GetLast(5, dest);
        Check("buffer: saturates at capacity", n == 5);
        Check("buffer: oldest samples are overwritten across the wrap",
            dest[0] == 3 && dest[1] == 4 && dest[2] == 5 && dest[3] == 6 && dest[4] == 7);
        Check("buffer: second channel wraps identically",
            dest[5] == 13 && dest[6] == 14 && dest[7] == 15 && dest[8] == 16 && dest[9] == 17);

        // A push larger than the whole buffer keeps only the tail.
        buf.Push([1, 2, 3, 4, 5, 6, 7, 21, 22, 23, 24, 25, 26, 27], 7);
        n = buf.GetLast(5, dest);
        Check("buffer: oversized push keeps the tail",
            n == 5 && dest[0] == 3 && dest[4] == 7 && dest[5] == 23 && dest[9] == 27);

        buf.Reset();
        Check("buffer: reset empties it", buf.GetLast(5, dest) == 0 && buf.Filled == 0);
    }

    // ---------- unit conversion ----------

    private static void TestUnitConversion()
    {
        // Reference values computed from the Python unit_conversion.py on the main branch.
        Check("units: oil pressure", Close(UnitConversion.GetOilPressure(1.0), 5_498_500.0));
        Check("units: 1D normal stress",
            Close(UnitConversion.GetNormalStress(1.0, FaultType.OneD, 0.05), 4_097_482.2));
        Check("units: 2D shear stress ignores the thickness argument",
            Close(UnitConversion.GetShearStress(1.0, FaultType.TwoD, 0.05), 835_991.94)
            && Close(UnitConversion.GetShearStress(1.0, FaultType.TwoD, 0.10),
                     UnitConversion.GetShearStress(1.0, FaultType.TwoD, 0.05)));
        Check("units: 1D normal stress scales inversely with thickness",
            Close(UnitConversion.GetNormalStress(1.0, FaultType.OneD, 0.10) * 2,
                  UnitConversion.GetNormalStress(1.0, FaultType.OneD, 0.05)));
        Check("units: LVDT displacement", Close(UnitConversion.GetLvdtDisplacement(2.0), 0.09954));
    }

    private static bool Close(double a, double b, double relativeTolerance = 1e-9)
        => Math.Abs(a - b) <= relativeTolerance * Math.Max(1.0, Math.Abs(b));

    // ---------- harness ----------

    private static void Check(string name, bool ok)
    {
        if (ok) _passed++;
        else _failed++;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{(ok ? "PASS" : "FAIL")}] {name}"));
    }
}
