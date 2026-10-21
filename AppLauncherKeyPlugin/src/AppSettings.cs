namespace Loupedeck.AppLauncherKeyPlugin
{
    using System;
    using System.Linq;

    // Settings of one key, parsed from its Action Editor values. A record: AppKeyCommand redraws the keys when the settings
    // they were drawn with differ from the current ones.
    // TrackExe is the full path of the exe whose process means "running" for a launcher app, null for the host's own.
    public record AppSettings(String Colour, Int32 Brightness, Boolean CloseWhenRunning, String TrackExe)
    {
        // Lamp colours and brightness levels pre-rendered by tools/render_key_backgrounds.py (LAMPS, LEVELS).
        public static readonly String[] Colours = { "Gold", "Green", "Red", "Blue", "White", "Purple", "Cyan", "Pink" };

        public const Int32 BrightnessStep = 20;

        public static readonly AppSettings Default = new AppSettings("Gold", 100, false, null);

        // Snaps values from the editor to the rendered lamps, so a key never asks for a missing resource.
        public AppSettings Normalized() => this with
        {
            Colour = Colours.Contains(this.Colour) ? this.Colour : Default.Colour,
            Brightness = Math.Clamp((this.Brightness + BrightnessStep / 2) / BrightnessStep * BrightnessStep, BrightnessStep, 100),
            TrackExe = String.IsNullOrWhiteSpace(this.TrackExe) ? null : this.TrackExe,
        };
    }
}
