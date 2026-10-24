if (args.Length >= 3 && args[0] == "bench")
    return IsoShimBench.Run(args[1], int.Parse(args[2]), System.Console.WriteLine) ? 0 : 1;
return IsoShimSpike.RunAll(System.Console.WriteLine) ? 0 : 1;
