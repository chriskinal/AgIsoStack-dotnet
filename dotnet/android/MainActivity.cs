// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors

using Android.App;
using Android.OS;
using Android.Widget;

[assembly: UsesPermission(Android.Manifest.Permission.Internet)]

[Activity(Label = "IsoStack spike", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var text = new TextView(this) { TextSize = 14 };
        text.SetPadding(32, 64, 32, 32);
        var scroll = new ScrollView(this);
        scroll.AddView(text);
        SetContentView(scroll);
        string? host = Intent?.GetStringExtra("host");
        int port = Intent?.GetIntExtra("port", 2323) ?? 2323;
        void Log(string line)
        {
            Android.Util.Log.Info("ISOSHIM", line);
            RunOnUiThread(() => text.Append(line + "\n"));
        }
        new System.Threading.Thread(() =>
        {
            if (host != null) IsoShimBench.Run(host, port, Log);
            else IsoShimSpike.RunAll(Log);
        }) { IsBackground = true }.Start();
    }
}
