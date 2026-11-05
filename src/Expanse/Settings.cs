using System.Text.Json;

namespace Expanse;

/// The choices on the tray menu, kept in %LOCALAPPDATA%\Expanse\settings.json.
sealed class Settings
{
    /// A window that gets focus while it's partly off screen (from a click, the taskbar, Alt-Tab,
    /// or an app) glides into view, as on the Mac.
    public bool AutoCentre { get; set; } = true;

    /// Auto-Centre When Hidden: how much of a window, as a percentage of its area from 1 to 100,
    /// must be off screen for focus on it to glide there (see Layout.HiddenEnough).
    public int AutoCentreHidden { get; set; } = 25;

    /// The desktop picture behind the canvas; off, a plain dark background.
    public bool Wallpaper { get; set; } = true;

    /// Hot Corner: which corner of the main screen zooms out when the pointer rests there (see
    /// HotCorner for the choices; every corner to begin with, as on the Mac).
    public string HotCorner { get; set; } = Expanse.HotCorner.Default;

    /// The groups panel on the right of the canvas (G shows or hides it).
    public bool GroupsPanel { get; set; } = true;

    /// Show Window Names: a label over each picture zoomed out (the app's name; the window's title
    /// too on hover). Off, as on the Mac, where titles can be private.
    public bool ShowNames { get; set; }

    /// Include Admin Windows: Expanse runs as administrator (Windows asks each time it starts) so
    /// it can move admin windows too. See Elevation.
    public bool RunAsAdmin { get; set; }

    /// Troubleshooting > Detailed Logging: the log is written to a file too (see Log).
    public bool DetailedLogging { get; set; }

    /// Welcome & Shortcuts has been shown (it opens by itself on first launch only).
    public bool WelcomeShown { get; set; }

    /// The note about admin windows staying put has been shown (once ever).
    public bool AdminNoteShown { get; set; }

    /// Check for Updates Automatically (release builds): once a day, as on the Mac.
    public bool CheckForUpdates { get; set; } = true;

    /// The update the tray icon last said was ready (said once per version).
    public string? UpdateNoted { get; set; }

    /// Group Selection Zoom: at this zoom or lower a click on an app in a group goes to the group,
    /// and the boxes turn dark (see Canvas.GroupsTakeClicks). 0 turns it off.
    public int GroupClickZoom { get; set; } = 50;

    static string File => Path.Combine(Log.Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new Settings();
        }
        catch (Exception e) { Log.Info($"settings: couldn't read ({e.Message}); using defaults"); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this));
        }
        catch (IOException e) { Log.Info($"settings: couldn't save: {e.Message}"); }
    }
}
