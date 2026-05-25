using FlashlightApp.Core;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

var opts = FlashOptions.Parse(args);
if (opts is null)
{
    PrintUsage();
    return 2;
}

Console.WriteLine("FlashlightApp CLI (Sprint 1 scaffold)");
Console.WriteLine("Parsed options:");
Console.WriteLine($"  elf            : {opts.ElfPath}");
Console.WriteLine($"  port           : {opts.Port}");
Console.WriteLine($"  power          : {opts.Power}");
Console.WriteLine($"  freq           : {opts.BmpFrequencyHz} Hz");
Console.WriteLine($"  connect-reset  : {opts.ConnectUnderReset}");
Console.WriteLine($"  product        : {opts.Product}");
Console.WriteLine($"  operator       : {opts.Operator}");
Console.WriteLine($"  batch          : {opts.Batch}");
Console.WriteLine($"  gdb-path       : {opts.GdbPath ?? "<auto-detect>"}");
Console.WriteLine();
Console.WriteLine("Not yet wired to gdb. State machine + SQLite log arrive next commit.");
return 0;

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          FlashlightApp.Cli --elf <path> --port <COMxx>
                            --power {probe|external}
                            [--freq <hz>]
                            [--connect-reset]
                            --product <id> --operator <name> --batch <id>
                            [--gdb-path <path-to-arm-none-eabi-gdb.exe>]

        Defaults: --freq 1000000, --power external, no --connect-reset.
        """);
}
