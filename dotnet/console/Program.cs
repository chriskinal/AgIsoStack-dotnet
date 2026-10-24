if (args.Length >= 3 && args[0] == "bench")
    return IsoShimBench.Run(args[1], int.Parse(args[2]), System.Console.WriteLine) ? 0 : 1;
if (args.Length >= 2 && args[0] == "screens")
    IsoShimSpike.ScreenDir = args[1];
if (args.Length >= 3 && args[0] == "screens")
    return IsoShimSpike.Tour(args[2], System.Console.WriteLine) ? 0 : 1;
return IsoShimSpike.RunAll(System.Console.WriteLine) ? 0 : 1;
