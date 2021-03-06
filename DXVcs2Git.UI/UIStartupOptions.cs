﻿using CommandLine;

namespace DXVcs2Git.UI {
    public class UIStartupOptions {
        [Option('s', "state", Default = System.Windows.WindowState.Normal)]
        public System.Windows.WindowState State { get; set; }
        [Option('h', "hidden", Default = false)]
        public bool Hidden { get; set; }
        public static UIStartupOptions GenerateDefault() {
            return new UIStartupOptions() {
                Hidden = false,
                State = System.Windows.WindowState.Normal
            };
        }
    }
}
