namespace FlashlightApp.Core;

public enum PowerMode { External, Probe }

public sealed record FlashOptions(
    string ElfPath,
    string Port,
    PowerMode Power,
    int BmpFrequencyHz,
    bool ConnectUnderReset,
    string Product,
    string Operator,
    string Batch,
    string? GdbPath)
{
    public static FlashOptions? Parse(string[] args)
    {
        string? elf = null, port = null, product = null, op = null, batch = null, gdbPath = null;
        PowerMode power = PowerMode.External;
        int freq = 1_000_000;
        bool connectReset = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--elf":           elf = Next(args, ref i); break;
                case "--port":          port = Next(args, ref i); break;
                case "--power":
                    var p = Next(args, ref i)?.ToLowerInvariant();
                    if (p == "probe") power = PowerMode.Probe;
                    else if (p == "external") power = PowerMode.External;
                    else return null;
                    break;
                case "--freq":
                    if (!int.TryParse(Next(args, ref i), out freq)) return null;
                    break;
                case "--connect-reset": connectReset = true; break;
                case "--product":       product = Next(args, ref i); break;
                case "--operator":      op = Next(args, ref i); break;
                case "--batch":         batch = Next(args, ref i); break;
                case "--gdb-path":      gdbPath = Next(args, ref i); break;
                default:                return null;
            }
        }

        if (elf is null || port is null || product is null || op is null || batch is null)
            return null;

        return new FlashOptions(elf, port, power, freq, connectReset, product, op, batch, gdbPath);
    }

    private static string? Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) return null;
        return args[++i];
    }
}
