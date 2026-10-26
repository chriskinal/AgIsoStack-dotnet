// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors

System.Console.OutputEncoding = System.Text.Encoding.UTF8;  // Windows writes its OEM code page otherwise
// Windows sleeps in 15.6 ms ticks unless asked for 1 ms: the tests step every 2 ms.
if (System.OperatingSystem.IsWindows()) TimeBeginPeriod(1);
if (args.Length >= 3 && args[0] == "bench")
    return IsoShimBench.Run(args[1], int.Parse(args[2]), System.Console.WriteLine) ? 0 : 1;
if (args.Length >= 2 && args[0] == "screens")
    IsoShimSpike.ScreenDir = args[1];
if (args.Length >= 3 && args[0] == "screens")
    return IsoShimSpike.Tour(args[2], System.Console.WriteLine) ? 0 : 1;
return IsoShimSpike.RunAll(System.Console.WriteLine) ? 0 : 1;

[System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
static extern uint TimeBeginPeriod(uint milliseconds);
