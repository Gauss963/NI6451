using Ni6451.Tools;

return args.Length == 0 ? Usage() : args[0].ToLowerInvariant() switch
{
    "devices" => DevicesCommand.Run(),
    "dump" => DumpCommand.Run(args[1..]),
    "trigger-test" => TriggerTestCommand.Run(args[1..]),
    "recover" => RecoverCommand.Run(args[1..]),
    "selftest" => SelfTestCommand.Run(),
    "-h" or "--help" or "help" => Usage(),
    _ => UnknownCommand(args[0]),
};

static int Usage()
{
    Console.WriteLine("""
        ni6451 -- command line companion to the USB-6451 WinUI application

        Usage:
          ni6451 devices
              List the NI-DAQmx devices the driver currently sees. (Windows + NI-DAQmx)

          ni6451 dump <file.npz> [--csv <out.csv>] [--max-rows N]
              Summarise a recording, and optionally export it as CSV.
              Port of the reading half of examples/read_example.py.

          ni6451 trigger-test [--device Dev1] [--line port0/line0] [--ai ai0]
              Hardware-clock-synced TTL trigger test; prints every rising edge.
              Port of tests/trigger_tester.py. (Windows + NI-DAQmx)

          ni6451 recover <output-folder> [--apply] [--out <folder>]
              Find acquisitions interrupted before their .npz was written and
              merge the spooled data into normal recordings. Lists them first;
              pass --apply to actually write the files.

          ni6451 selftest
              Verify the rolling buffer, the unit conversions and the .npy/.npz
              container round-trip. Runs on any platform, no hardware needed.
        """);
    return 0;
}

static int UnknownCommand(string name)
{
    Console.Error.WriteLine($"Unknown command '{name}'. Run 'ni6451 --help'.");
    return 2;
}
