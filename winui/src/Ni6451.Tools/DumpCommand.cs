using System.Globalization;
using System.Text;
using Ni6451.Core;

namespace Ni6451.Tools;

/// <summary>
/// Reads back a recording and reports what is in it, optionally exporting CSV. This is the
/// reading half of the Python <c>examples/read_example.py</c>; the plotting half is left to
/// the existing Python script, which still works unchanged against these files.
/// </summary>
internal static class DumpCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ni6451 dump <file.npz> [--csv <out.csv>] [--max-rows N]");
            return 2;
        }

        string path = args[0];
        string? csvPath = null;
        long maxRows = 10_000;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--csv" when i + 1 < args.Length:
                    csvPath = args[++i];
                    break;
                case "--max-rows" when i + 1 < args.Length:
                    maxRows = long.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                default:
                    Console.Error.WriteLine($"Unrecognised option '{args[i]}'.");
                    return 2;
            }
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No such file: {path}");
            return 1;
        }

        using var npz = new NpzReader(path);

        long rate = npz.ReadInt64Scalar("sample_rate");
        long[] channels = npz.ReadInt64Array("channels");
        long triggerIndex = npz.ReadInt64Scalar("trigger_sample_index");
        if (channels.Length == 0)
        {
            Console.Error.WriteLine("The archive records no channels.");
            return 1;
        }

        long nSamples = npz.GetHeader($"ai{channels[0]}").Count;

        Console.WriteLine($"File            : {Path.GetFullPath(path)}");
        Console.WriteLine($"Sample rate     : {rate:N0} S/s/channel");
        Console.WriteLine($"Channels        : {string.Join(", ", channels.Select(c => $"ai{c}"))}");
        Console.WriteLine($"Samples/channel : {nSamples:N0}");
        Console.WriteLine($"Duration        : {(double)nSamples / rate:F3} s");
        Console.WriteLine(triggerIndex >= 0
            ? $"Trigger         : sample {triggerIndex:N0} (t = {(double)triggerIndex / rate:F6} s)"
            : "Trigger         : not captured");

        if (csvPath is not null)
            WriteCsv(npz, csvPath, channels, rate, triggerIndex, nSamples, maxRows);

        return 0;
    }

    private static void WriteCsv(
        NpzReader npz, string csvPath, long[] channels, long rate, long triggerIndex, long nSamples, long maxRows)
    {
        long rows = Math.Min(nSamples, maxRows);
        var columns = new double[channels.Length][];
        for (int i = 0; i < channels.Length; i++)
        {
            var column = new double[rows];
            long filled = 0;
            npz.StreamFloat64($"ai{channels[i]}", 1 << 16, block =>
            {
                if (filled >= rows) return;
                int take = (int)Math.Min(block.Length, rows - filled);
                block[..take].CopyTo(column.AsSpan((int)filled));
                filled += take;
            });
            columns[i] = column;
        }

        using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("t_s," + string.Join(",", channels.Select(c => $"ai{c}_V")));

        var line = new StringBuilder();
        for (long r = 0; r < rows; r++)
        {
            // t = 0 at the trigger when one was captured, otherwise at the start of the recording.
            double t = (triggerIndex >= 0 ? r - triggerIndex : r) / (double)rate;
            line.Clear();
            line.Append(t.ToString("G17", CultureInfo.InvariantCulture));
            foreach (double[] column in columns)
            {
                line.Append(',');
                line.Append(column[r].ToString("G17", CultureInfo.InvariantCulture));
            }

            writer.WriteLine(line);
        }

        Console.WriteLine($"CSV written     : {Path.GetFullPath(csvPath)} ({rows:N0} rows"
                          + (rows < nSamples ? ", truncated by --max-rows)" : ")"));
    }
}
