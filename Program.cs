/* SPDX-License-Identifier: MPL-2.0
 * Copyright (c) 2026 1R1an1 */
using Avalonia;
using MediaPlayer.TUI;
using System;
using System.Linq;
using TermFlow.Core;

namespace MediaPlayer;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args?.FirstOrDefault() == "--nogui")
        {
            BasicTUI.Start(args[1..]);
            return 0;
        }
        else if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Error.WriteLine($"{ThemeColors.Error}No hay DISPLAY disponible, usando BasicTUI{ThemeColors.Reset}");
            BasicTUI.Start(args);
            return 0;
        }
        else
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
