// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors

using System.Linq;
using Foundation;
using UIKit;

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        var text = new UITextView { Editable = false, Font = UIFont.GetMonospacedSystemFont(14, UIFontWeight.Regular) };
        var controller = new UIViewController();
        controller.View = text;
        Window.RootViewController = controller;
        Window.MakeKeyAndVisible();
        void Log(string line)
        {
            System.Console.WriteLine("ISOSHIM " + line);
            InvokeOnMainThread(() => text.Text += line + "\n");
        }
        // Launch arguments, after the executable path (they do not reach Main here).
        string[] args = NSProcessInfo.ProcessInfo.Arguments.Skip(1).ToArray();
        new System.Threading.Thread(() =>
        {
            if (args.Length >= 3 && args[0] == "bench") IsoShimBench.Run(args[1], int.Parse(args[2]), Log);
            else IsoShimSpike.RunAll(Log);
        }) { IsBackground = true }.Start();
        return true;
    }
}

public static class Program
{
    public static string[] Args = System.Array.Empty<string>();
    static void Main(string[] args) { Args = args; UIApplication.Main(args, null, typeof(AppDelegate)); }
}
