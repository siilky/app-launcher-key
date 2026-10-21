namespace Loupedeck.AppLauncherKeyPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.Win32.SafeHandles;

    // The plugin's launch key: launches an installed app and paints the key as a lit button with the app icon, the lamp
    // being on while the app is running. An Action Editor action, so each key keeps its own app, lamp colour, brightness,
    // closing and tracked exe in its Action Editor values.
    //
    // Not a profile action (MakeProfileAction) and not a command with the apps as parameters: in Options+ 2.9.994631 /
    // LPS 6.4.2 the host draws a profile action key with an instance ID instead of the chosen value,
    // and a parameter would leave a key nowhere to keep its own settings.
    //
    // When a new Action Editor key is saved for the first time, a snapshot ActionIcons\<profile action name>.ict appears
    // next to the profile's ProfileInfo.json. While that file exists the key shows it and GetCommandImage is not called;
    // once it is deleted the key is live again without a restart (seen on the device). So after a Save this action
    // deletes the fresh snapshots of its own keys and asks for a redraw; from then on the key is drawn here and its lamp
    // follows the app. Re-saving a key writes no new snapshot.
    // Known gap: a copied or pasted key gets its snapshot without a Finished event and stays frozen.
    //
    // undocumented API: GetCommandImage(ActionEditorActionParameters, Int32, Int32), the parameterless
    // ActionImageChanged(), ActionEditorAction.OnLoad/OnUnload and ActionEditorFinishedEventArgs.IsCanceled are public in
    // PluginApi.dll 6.4.2 but absent from the SDK docs (OnLoad/OnUnload are documented only for PluginDynamicAction);
    // without them an Action Editor key could not draw itself, redraw on a state change, poll, or tell Save from Cancel.
    // The members used by LoadApps and DrawKey are marked at those methods.
    public class AppKeyCommand : ActionEditorCommand
    {
        private const String AppControl = "App";
        private const String ColourControl = "Colour";
        private const String BrightnessControl = "Brightness";
        private const String CloseControl = "Close";
        private const String TrackExeControl = "TrackExe";

        // Programs a shortcut uses to open a document, folder or settings page: such an entry is not an app, and the
        // program's other windows would light its lamp.
        private static readonly HashSet<String> GenericHosts = new HashSet<String>(StringComparer.OrdinalIgnoreCase)
        {
            "notepad", "explorer", "rundll32", "cscript", "wscript", "control",
        };

        // Key configurations LPS has asked to draw, with the running state last drawn. Keys with equal values share an
        // entry. GetCommandImage runs on LPS threads and Poll on a timer thread, so the map must be concurrent. Entries of
        // keys that were unassigned or re-saved are never removed; the set is bounded by what the user ever configured.
        private readonly ConcurrentDictionary<KeyConfig, Boolean> _drawn = new ConcurrentDictionary<KeyConfig, Boolean>();

        // Installed apps by AppId. Replaced as a whole, never mutated: it is read from LPS threads and the timer thread.
        private IReadOnlyDictionary<String, InstalledApp> _apps = new Dictionary<String, InstalledApp>();

        // Processes of drawn keys whose exit is awaited on their handle, by process ID. The open handle keeps the ID from
        // being reused, so an entry always means the same process; Exited removes and disposes it.
        private readonly ConcurrentDictionary<Int32, Process> _watched = new ConcurrentDictionary<Int32, Process>();

        // Process IDs whose handle could not be opened for waiting; their exit is left to the poll and never retried. Only
        // WatchExit touches it, under _pollLock.
        // Limitation: never pruned, so a reused ID of such a process is polled too; prune by the snapshot if it matters.
        private readonly HashSet<Int32> _unwatchable = new HashSet<Int32>();

        // Serializes Poll, whose timer callbacks can overlap: both write _drawn, and WatchExit's check-then-add must not
        // watch one process twice (the overwritten Process would leak with a live wait).
        private readonly Object _pollLock = new Object();

        private Timer _timer;

        public AppKeyCommand()
        {
            // Stored in users' profiles as templateActionName: renaming it orphans their keys.
            this.Name = "AppKey";
            this.DisplayName = "Launch App";
            this.GroupName = "Commands";
            this.Description = "Launches an app and shows whether it is running";

            this.ActionEditor.AddControlEx(new ActionEditorListbox(AppControl, "Application:"));
            this.ActionEditor.AddControlEx(new ActionEditorListbox(ColourControl, "Lamp colour:"));
            this.ActionEditor.AddControlEx(new ActionEditorSlider(BrightnessControl, "Lamp brightness:")
                .SetValues(AppSettings.BrightnessStep, 100, AppSettings.Default.Brightness, AppSettings.BrightnessStep)
                .SetFormatString("{0}%"));
            this.ActionEditor.AddControlEx(new ActionEditorCheckbox(CloseControl, "Close the app when pressed while it runs"));
            this.ActionEditor.AddControlEx(new ActionEditorFileSelector(TrackExeControl, "Program to track (launchers only):",
                "For an app whose shortcut starts a launcher that exits: the exe of the program that keeps running."));

            this.ActionEditor.ListboxItemsRequested += this.OnListboxItemsRequested;
            this.ActionEditor.ControlValueChanged += this.OnControlValueChanged;
            this.ActionEditor.Finished += this.OnFinished;
        }

        protected override Boolean OnLoad()
        {
            // In the background: the host drops the action if OnLoad takes about 1 s (loading the list inside OnLoad ended
            // with "0 dynamic actions loaded" after 1025 ms).
            Task.Run(this.LoadApps);
            this._timer = new Timer(_ => this.Poll(), null, 1000, 1000);
            return true;
        }

        protected override Boolean OnUnload()
        {
            this._timer?.Dispose();
            foreach (var processId in this._watched.Keys)
            {
                if (this._watched.TryRemove(processId, out var process))
                {
                    process.Dispose();
                }
            }

            return true;
        }

        // undocumented API: INativeMethods.GetInstalledApplications, the list the built-in "Run" action shows: Start Menu
        // shortcuts plus Store apps, each with a PNG icon, a process name and a launch string. It is the only source of
        // Store apps and spares the plugin resolving shortcuts and process names itself.
        // Reading the list takes about 2 s (for about 200 apps); until it arrives the App listbox is empty and keys
        // show the display name.
        // Limitation: apps installed later appear only after the plugin reloads; reload the list periodically if that
        // turns out to matter.
        private void LoadApps()
        {
            try
            {
                var nativeApps = this.Plugin.NativeApi.GetNativeMethods().GetInstalledApplications().ToList();
                var apps = SelectApps(nativeApps);
                this._apps = apps;

                // Keys drawn before the list arrived showed text.
                this.ActionImageChanged();

                var dropped = nativeApps.Where(app => app.ExecuteApplicationActionParameter == null || !apps.ContainsKey(AppId(app)));
                PluginLog.Info($"{apps.Count} applications added");
                PluginLog.Verbose($"Skipped: {String.Join("; ", dropped.Select(app => $"{app.DisplayName} [{app.ExecuteApplicationActionParameter}]"))}");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Cannot read installed applications");
            }
        }

        // The host lists Start Menu shortcuts unfiltered (over 200 entries on a test machine).
        // Kept is one entry per exe that can show a lamp; entries of different exes with the same name get the exe's
        // folder appended, so the App listbox tells them apart.
        private static Dictionary<String, InstalledApp> SelectApps(IEnumerable<NativeApplication> nativeApps)
        {
            var installerFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer") + "\\";
            var apps = nativeApps
                .Where(app => !String.IsNullOrEmpty(app.ExecuteApplicationActionParameter)
                    && !String.IsNullOrEmpty(app.ProcessName)
                    && !String.IsNullOrEmpty(app.ExecutablePath)
                    && !(GenericHosts.Contains(app.ProcessName) && Arguments(app) != "")
                    && !app.ExecutablePath.StartsWith(installerFolder, StringComparison.OrdinalIgnoreCase))

                // Shortcuts of one exe that differ in arguments (AnyDesk --control, Viber SafeMode) or only in the working
                // folder all light together; the plain one stays. An exe with only argument variants is a tool's commands
                // (TortoiseGit /command:..., OpenVPN tapctl create ...), not an app.
                .GroupBy(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.Count() == 1 ? group : group.Where(app => Arguments(app) == "").Take(1))
                .DistinctBy(AppId)
                .ToList();

            var sharedNames = apps.GroupBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            return apps.ToDictionary(AppId, app =>
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(app.ExecutablePath));
                var displayName = sharedNames.Contains(app.DisplayName) ? $"{app.DisplayName} ({Path.GetFileName(folder)})" : app.DisplayName;
                return new InstalledApp(app, displayName, folder);
            });
        }

        // The host's launch string is "path||arguments||working directory" for desktop apps and a shell:AppsFolder URI
        // for Store apps.
        private static String Arguments(NativeApplication app)
        {
            var parts = app.ExecuteApplicationActionParameter.Split("||");
            return parts.Length > 1 ? parts[1] : "";
        }

        private void OnListboxItemsRequested(Object sender, ActionEditorListboxItemsRequestedEventArgs e)
        {
            if (e.ControlName.EqualsNoCase(AppControl))
            {
                foreach (var (id, app) in this._apps.OrderBy(pair => pair.Value.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    e.AddItem(id, app.DisplayName, app.Native.ExecutablePath);
                }
            }
            else if (e.ControlName.EqualsNoCase(ColourControl))
            {
                foreach (var colour in AppSettings.Colours)
                {
                    e.AddItem(colour, colour, null);
                }

                var selected = e.ActionEditorState.GetControlValue(ColourControl);
                e.SetSelectedItemName(AppSettings.Colours.Contains(selected) ? selected : AppSettings.Default.Colour);
            }
        }

        // The key is labelled with its app.
        private void OnControlValueChanged(Object sender, ActionEditorControlValueChangedEventArgs e)
        {
            if (e.ControlName.EqualsNoCase(AppControl) && this.FindApp(e.ActionEditorState.GetControlValue(AppControl)) is InstalledApp app)
            {
                e.ActionEditorState.SetDisplayName(app.DisplayName);
            }
        }

        protected override Boolean RunCommand(ActionEditorActionParameters actionParameters)
        {
            var key = GetKeyConfig(actionParameters);
            var app = this.FindApp(key.AppId);
            if (app == null)
            {
                PluginLog.Warning($"Unknown application '{key.AppId}'");
                return false;
            }

            if (!CloseApp(app, key.Settings))
            {
                Launch(app);
            }

            return true;
        }

        // Asks the app's windows to close, like their close button: the app may ask to save, and a tray app hides instead
        // of exiting (its lamp stays on). Never killed, so no work is lost. An app hidden in the tray has no window to
        // close, so the press launches it instead, which brings a single-instance app's window back.
        private static Boolean CloseApp(InstalledApp app, AppSettings settings) =>
            settings.CloseWhenRunning && GetAppProcessIds(app, settings, GetRunningProcesses()).Count(CloseMainWindow) > 0;

        private static void Launch(InstalledApp app)
        {
            try
            {
                // Shell execute handles both launch string forms.
                var parts = app.Native.ExecuteApplicationActionParameter.Split("||");
                var startInfo = new ProcessStartInfo(parts[0]) { UseShellExecute = true };
                if (parts.Length > 1)
                {
                    startInfo.Arguments = parts[1];
                }

                if (parts.Length > 2)
                {
                    startInfo.WorkingDirectory = parts[2];
                }

                Process.Start(startInfo)?.Dispose();
            }
            catch (Exception ex)
            {
                // An uninstalled app is a user error, not a reason to throw inside the service process.
                PluginLog.Warning(ex, $"Cannot start '{app.Native.ExecuteApplicationActionParameter}'");
            }
        }

        // False for a process without a main window or one that exited since the snapshot.
        private static Boolean CloseMainWindow(Int32 processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.CloseMainWindow();
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                return false;
            }
        }

        // No app before one is chosen, before the list arrives or after the app was uninstalled. The host falls back to the
        // display name.
        protected override BitmapImage GetCommandImage(ActionEditorActionParameters actionParameters, Int32 imageWidth, Int32 imageHeight)
        {
            var key = GetKeyConfig(actionParameters);
            var app = this.FindApp(key.AppId);
            if (app == null)
            {
                return null;
            }

            var running = this._drawn.GetOrAdd(key, _ => this.IsRunning(key, GetRunningProcesses()));
            using (var bitmapBuilder = new BitmapBuilder(imageWidth, imageHeight))
            {
                return DrawKey(bitmapBuilder, app, running, key.Settings);
            }
        }

        // undocumented API: the BitmapBuilder.Width/Height properties and the DrawImage(Byte[], ...) overload are public
        // in PluginApi.dll 6.4.2 but absent from the SDK docs; the host's icons come as PNG bytes and the requested size
        // can be a value outside the PluginImageSize members (143 was seen).
        private static BitmapImage DrawKey(BitmapBuilder bitmapBuilder, InstalledApp app, Boolean running, AppSettings settings)
        {
            var lamp = $"Lamp{settings.Colour}{(running ? $"On{settings.Brightness}" : "Off")}";

            // package/metadata/DefaultIconTemplate.ict gives this image the whole key and lays the display name over its
            // bottom (y 71-93 %), so the icon stays above that band. The lamp images are rendered around this icon
            // placement by tools/render_key_backgrounds.py: BitmapBuilder has no gradients, blur or image opacity, and
            // light faked with stacked translucent rectangles showed on the device as separate frames.
            // Limitation: the host's icons are small (50 px for Notepad) and get upscaled; extract a larger
            // icon from the exe if that looks too blurry.
            bitmapBuilder.DrawImage(PluginResources.ReadBinaryFile($"{lamp}.png"), 0, 0, bitmapBuilder.Width, bitmapBuilder.Height);

            var iconSize = bitmapBuilder.Height * 70 / 100;
            var iconX = (bitmapBuilder.Width - iconSize) / 2;
            var iconY = bitmapBuilder.Height / 100;
            if (app.Native.Icon != null)
            {
                bitmapBuilder.DrawImage(app.Native.Icon, iconX, iconY, iconSize, iconSize);
            }
            else
            {
                bitmapBuilder.DrawText(app.DisplayName, iconX, iconY, iconSize, iconSize);
            }

            // The lamp's tint and the cap's gloss go over the icon, as if it were printed under the lit plastic.
            bitmapBuilder.DrawImage(PluginResources.ReadBinaryFile($"{lamp}Overlay.png"), 0, 0, bitmapBuilder.Width, bitmapBuilder.Height);
            return bitmapBuilder.ToImage();
        }

        // Redraws the keys when an app started or exited. One redraw for all of them: the parameterless
        // ActionImageChanged() is the only one an Action Editor action has. Runs every second, and a watched process's
        // exit brings the tick forward, so a lamp goes off without waiting for it; a start is only seen by the tick.
        private void Poll()
        {
            // Runs inside the LPS process on a timer thread: an escaping exception could take the service down.
            try
            {
                lock (this._pollLock)
                {
                    if (this._drawn.IsEmpty)
                    {
                        return;
                    }

                    var processes = GetRunningProcesses();
                    var changed = false;
                    foreach (var (key, drawn) in this._drawn)
                    {
                        var processIds = this.FindApp(key.AppId) is InstalledApp app
                            ? GetAppProcessIds(app, key.Settings, processes).ToList()
                            : new List<Int32>();
                        var running = processIds.Count > 0;
                        if (running != drawn)
                        {
                            this._drawn[key] = running;
                            changed = true;
                        }

                        processIds.ForEach(this.WatchExit);
                    }

                    if (changed)
                    {
                        this.ActionImageChanged();
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Process poll failed");
            }
        }

        // Windows reports process starts and exits only to administrators (WMI Win32_ProcessStartTrace/StopTrace and the
        // ETW Kernel-Process provider are denied, and LPS runs unelevated), but a process's handle
        // signals its exit. Process.Exited waits on it on a thread-pool thread. EnableRaisingEvents opens the process with
        // full access (.NET 10 runtime source), so an elevated app, which an unelevated LPS cannot open that way, is left
        // to the poll, like any other process it fails for.
        private void WatchExit(Int32 processId)
        {
            if (this._watched.ContainsKey(processId) || this._unwatchable.Contains(processId))
            {
                return;
            }

            Process process = null;
            try
            {
                process = Process.GetProcessById(processId);
                process.Exited += (_, _) =>
                {
                    // Disposed first, so the next snapshot is not kept from dropping the process by our own handle.
                    if (this._watched.TryRemove(processId, out var exited))
                    {
                        exited.Dispose();
                    }

                    // Brings the tick forward instead of polling here: closing a browser ends dozens of watched processes
                    // at once, and a poll per exit would hold as many pool threads waiting on _pollLock. After OnUnload
                    // disposed the timer, Change does nothing.
                    this._timer.Change(0, 1000);
                };

                // Added before the wait starts, so an Exited raised right away finds the entry to remove.
                this._watched[processId] = process;
                process.EnableRaisingEvents = true;
                PluginLog.Verbose($"Watching the exit of process {processId}");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            {
                // ArgumentException and InvalidOperationException: the process exited meanwhile, which the next poll
                // sees. Win32Exception: no access to wait on its handle.
                this._watched.TryRemove(processId, out _);
                process?.Dispose();
                if (ex is Win32Exception)
                {
                    this._unwatchable.Add(processId);
                    PluginLog.Verbose($"Cannot watch the exit of process {processId}, polling it: {ex.Message}");
                }
            }
        }

        private Boolean IsRunning(KeyConfig key, ILookup<String, Int32> processes) =>
            this.FindApp(key.AppId) is InstalledApp app && GetAppProcessIds(app, key.Settings, processes).Any();

        private InstalledApp FindApp(String id) =>
            id != null && this._apps.TryGetValue(id, out var app) ? app : null;

        // The app ID is stored in the profile as the listbox item name, so it must survive reloads and stay free of the
        // separators a launch string contains ("||", "\", ":", "!"); a hash of the launch string gives both.
        private static String AppId(NativeApplication app) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(app.ExecuteApplicationActionParameter.ToUpperInvariant())), 0, 8);

        // Processes of the app: the name the host gives (or the tracked exe's, for a launcher whose running program is
        // another exe), running from the app's folder or a subfolder (or the tracked exe's folder). The folder keeps
        // other programs with the same file name out (another python.exe, a CLI shipped next to a desktop app,
        // unrelated apps with the same exe name) and still finds Squirrel apps (Discord, Toggl Track) that run from app-<version>\.
        // Only the name of a tracked exe is matched in the app's folder, which the host reports anew on every load, so
        // Store and Squirrel updates that change the folder do not break it.
        private static IEnumerable<Int32> GetAppProcessIds(InstalledApp app, AppSettings settings, ILookup<String, Int32> processes)
        {
            var name = settings.TrackExe != null ? Path.GetFileNameWithoutExtension(settings.TrackExe) : app.Native.ProcessName;
            var folders = settings.TrackExe != null ? new[] { app.Folder, Path.GetDirectoryName(settings.TrackExe) } : new[] { app.Folder };
            return processes[name].Where(processId =>
                GetProcessPath(processId) is String path
                && folders.Any(folder => path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)));
        }

        // One snapshot per tick instead of GetProcessesByName per key: each of those enumerates all processes anyway.
        private static ILookup<String, Int32> GetRunningProcesses()
        {
            var processes = Process.GetProcesses();
            var lookup = processes.ToLookup(process => process.ProcessName, process => process.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return lookup;
        }

        // Limited query access is granted for elevated processes too, unlike the full access Process.MainModule needs.
        // Null for a process that exited or is protected.
        private static String GetProcessPath(Int32 processId)
        {
            using (var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId))
            {
                var path = new StringBuilder(1024);
                var size = path.Capacity;
                return !handle.IsInvalid && QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
            }
        }

        private const UInt32 ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(UInt32 desiredAccess, Boolean inheritHandle, Int32 processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Boolean QueryFullProcessImageName(SafeProcessHandle process, UInt32 flags, StringBuilder exeName, ref Int32 size);

        // The host keeps the values of the key's last Save: listbox item names, the slider's number, "true"/"false" for
        // the checkbox and the full path from the file selector.
        private static KeyConfig GetKeyConfig(ActionEditorActionParameters actionParameters)
        {
            actionParameters.TryGetString(AppControl, out var id);
            actionParameters.TryGetString(ColourControl, out var colour);
            actionParameters.TryGetString(TrackExeControl, out var trackExe);
            var settings = new AppSettings(
                colour,
                actionParameters.TryGetString(BrightnessControl, out var brightnessValue) && Int32.TryParse(brightnessValue, out var brightness)
                    ? brightness
                    : AppSettings.Default.Brightness,
                actionParameters.TryGetString(CloseControl, out var closeValue) && Boolean.TryParse(closeValue, out var close)
                    ? close
                    : AppSettings.Default.CloseWhenRunning,
                trackExe).Normalized();
            return new KeyConfig(id, settings);
        }

        private void OnFinished(Object sender, ActionEditorFinishedEventArgs e)
        {
            if (e.IsCanceled)
            {
                return;
            }

            // Captured per Save, so the windows of two quick Saves do not overwrite each other.
            var finishedAt = DateTime.UtcNow;
            Task.Run(() => this.RemoveSnapshots(finishedAt));
        }

        // Deletes the icon snapshot Options+ had LPS store for a key of this action saved for the first time, so the key is
        // drawn by GetCommandImage. Only the .ict of this action's own profile actions is deleted, and only if written
        // within 10 s before Finished or later: older files are icons the user drew in the Icon Editor, which must stay
        // (an Icon Editor icon written 21 s before a Save survived on the device); ProfileInfo.json and everything else is
        // only read: the plugin writes no other Options+ file. On the device the first sweep, about 0.5 s after Finished,
        // already found and deleted the snapshot; the order of LPS's request handling is not documented, so a second
        // sweep about 2 s after Finished stays as a margin, and it finding nothing is normal.
        private async Task RemoveSnapshots(DateTime finishedAt)
        {
            try
            {
                var since = finishedAt.AddSeconds(-10);
                var templateActionName = "$AppLauncherKey___" + this.Name;
                var applicationsFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Logi", "LogiPluginService", "Applications");
                foreach (var delay in new[] { 500, 1500 })
                {
                    await Task.Delay(delay);
                    if (DeleteSnapshots(applicationsFolder, templateActionName, since))
                    {
                        this.ActionImageChanged();
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Cannot remove icon snapshots");
            }
        }

        private static Boolean DeleteSnapshots(String applicationsFolder, String templateActionName, DateTime since)
        {
            var deleted = false;
            foreach (var profilePath in Directory.EnumerateFiles(applicationsFolder, "ProfileInfo.json", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(profilePath) < since)
                    {
                        continue;
                    }

                    // LPS may be writing the file: share everything, and the next sweep retries a file that fails.
                    using (var stream = new FileStream(profilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var profile = JsonDocument.Parse(stream))
                    {
                        if (!profile.RootElement.TryGetProperty("profileActions", out var actions) || actions.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var action in actions.EnumerateArray())
                        {
                            if (action.ValueKind != JsonValueKind.Object
                                || !action.TryGetProperty("templateActionName", out var template)
                                || template.ValueKind != JsonValueKind.String
                                || template.GetString() != templateActionName
                                || !action.TryGetProperty("name", out var name)
                                || name.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var iconPath = Path.Combine(Path.GetDirectoryName(profilePath), "ActionIcons", name.GetString() + ".ict");
                            if (File.Exists(iconPath) && File.GetLastWriteTimeUtc(iconPath) >= since)
                            {
                                File.Delete(iconPath);
                                PluginLog.Info($"Deleted icon snapshot {iconPath}");
                                deleted = true;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    PluginLog.Warning(ex, $"Cannot check {profilePath}");
                }
            }

            return deleted;
        }

        private record struct KeyConfig(String AppId, AppSettings Settings);

        // An app of the host's list after clean-up. Folder is the folder of its exe: a process counts as the app only if it
        // runs from there (GetAppProcessIds).
        private record InstalledApp(NativeApplication Native, String DisplayName, String Folder);
    }
}
