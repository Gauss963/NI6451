using Ni6451.Core;

namespace Ni6451.Tools;

/// <summary>
/// Finds acquisitions that were interrupted before their <c>.npz</c> was written -- a crash,
/// a power cut, a closed laptop lid -- and merges the spooled data into a normal recording.
/// The application offers the same thing in its UI; this exists for the case where the data
/// needs rescuing from a machine that is not the one running the app.
/// </summary>
internal static class RecoverCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ni6451 recover <output-folder> [--apply] [--out <folder>]");
            return 2;
        }

        string saveDir = args[0];
        string outDir = saveDir;
        bool apply = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply": apply = true; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                default:
                    Console.Error.WriteLine($"Unrecognised option '{args[i]}'.");
                    return 2;
            }
        }

        if (!Directory.Exists(saveDir))
        {
            Console.Error.WriteLine($"No such folder: {saveDir}");
            return 1;
        }

        IReadOnlyList<OrphanedSpool> orphans = SpoolRecovery.FindOrphans(saveDir);
        if (orphans.Count == 0)
        {
            Console.WriteLine("No interrupted acquisitions found.");
            return 0;
        }

        Console.WriteLine($"Found {orphans.Count} interrupted acquisition(s):");
        Console.WriteLine();
        foreach (OrphanedSpool o in orphans)
        {
            Console.WriteLine($"  {Path.GetFileName(o.SpoolDir)}");
            Console.WriteLine($"    started   : {o.StartedUtc:u}");
            Console.WriteLine($"    device    : {o.Manifest.Device}");
            Console.WriteLine($"    channels  : {string.Join(", ", o.Manifest.Channels.Select(c => $"ai{c}"))}");
            Console.WriteLine($"    recovered : {o.RecoverableSamplesPerChannel:N0} samples/channel ({o.DurationSeconds:F1} s)");
            Console.WriteLine($"    trigger   : {(o.Manifest.TriggerSampleIndex >= 0 ? $"sample {o.Manifest.TriggerSampleIndex:N0}" : "none")}");
            Console.WriteLine($"    would save: T{o.Manifest.Sh}-raw-run{o.Manifest.Rn}-<timestamp>.npz");
            Console.WriteLine();
        }

        if (!apply)
        {
            Console.WriteLine("Dry run. Re-run with --apply to write the .npz files.");
            return 0;
        }

        Directory.CreateDirectory(outDir);
        int failed = 0;
        foreach (OrphanedSpool o in orphans)
        {
            try
            {
                var progress = new Progress<FinalizeProgress>();
                string path = SpoolRecovery.Recover(o, outDir, progress);
                Console.WriteLine($"Recovered -> {path}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed++;
                Console.Error.WriteLine($"Failed to recover {Path.GetFileName(o.SpoolDir)}: {e.Message}");
                Console.Error.WriteLine("  The raw spool files were left in place.");
            }
        }

        return failed == 0 ? 0 : 1;
    }
}
