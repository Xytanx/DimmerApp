using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using Timer = System.Timers.Timer;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using ApplicationWPF = System.Windows.Application;
using System.Text.Json;
using System.Linq; // 🔹 Added for ToList()

namespace DimmerApp
{
    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, Window> overlays = new();
        private readonly Dictionary<string, ScheduleData> schedules = new();
        private NotifyIcon? trayIcon;
        private readonly Timer scheduleTimer = new(5000);
        private bool _isExiting = false;
        private readonly string scheduleFilePath;

        // 🔹 Replaced single boolean with a per-display manual override set
        private readonly HashSet<string> manualDimOverrides = new();

        public MainWindow()
        {
            InitializeComponent();

            // File path in install directory
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            scheduleFilePath = Path.Combine(exeDir, "schedules.json");
            LoadSchedules();

            PopulateDisplays();

            dimmingSlider.Minimum = 0;
            dimmingSlider.Maximum = 100;
            dimmingSlider.Value = 50;
            dimmingSlider.ValueChanged += (s, e) => dimmingValue.Text = $"{dimmingSlider.Value:F0}%";

            scheduleDimmingSlider.Minimum = 0;
            scheduleDimmingSlider.Maximum = 100;
            scheduleDimmingSlider.Value = 50;
            scheduleDimmingSlider.ValueChanged += (s, e) => scheduleDimmingValue.Text = $"{scheduleDimmingSlider.Value:F0}%";

            applyButton.Click += ApplyButton_Click;
            removeButton.Click += RemoveButton_Click;
            setScheduleButton.Click += SetScheduleButton_Click;
            resetScheduleButton.Click += ResetScheduleButton_Click;
            scheduleDisplaySelector.SelectionChanged += ScheduleDisplaySelector_SelectionChanged;

            enableSchedule.Checked += (s, e) => { UpdateScheduleControlsState(); SaveSchedules(); };
            enableSchedule.Unchecked += (s, e) => { UpdateScheduleControlsState(); SaveSchedules(); };

            InitializeTrayIcon();
            InitializeStartupSetting();

            scheduleTimer.Elapsed += ScheduleTimer_Elapsed;
            scheduleTimer.Start();

            startTimeBox.TextChanged += TimeBox_TextChanged;
            stopTimeBox.TextChanged += TimeBox_TextChanged;

            // Handle minimized startup
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && args[1] == "--minimized")
            {
                this.WindowState = WindowState.Minimized;
                this.Hide();
            }

            // Initialize scheduler controls state
            UpdateScheduleControlsState();
        }

        private void PopulateDisplays()
        {
            displaySelector.Items.Clear();
            scheduleDisplaySelector.Items.Clear();

            displaySelector.Items.Add("All Displays");
            scheduleDisplaySelector.Items.Add("All Displays");

            try
            {
                var screens = Screen.AllScreens;
                foreach (var screen in screens)
                {
                    string name = GetMonitorFriendlyName(screen.DeviceName);
                    if (screen.Primary)
                        name += " (Primary)";
                    displaySelector.Items.Add(name);
                    scheduleDisplaySelector.Items.Add(name);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error detecting displays: {ex.Message}");
            }

            displaySelector.SelectedIndex = 0;
            scheduleDisplaySelector.SelectedIndex = 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplayDevices(string lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

        private static string GetMonitorFriendlyName(string deviceName)
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(dd);
            return EnumDisplayDevices(deviceName, 0, ref dd, 0) ? dd.DeviceString : deviceName;
        }

        #region Dimming
        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (displaySelector.SelectedItem is not string selected)
                return;

            double strength = dimmingSlider.Value / 100.0;

            // 🔹 If user manually applies dimming, clear per-display manual override for that selection
            if (selected == "All Displays")
                manualDimOverrides.Clear();
            else
                manualDimOverrides.Remove(selected);

            if (selected == "All Displays")
            {
                foreach (var screen in Screen.AllScreens)
                {
                    string name = GetMonitorFriendlyName(screen.DeviceName);
                    ApplyOverlay(name, screen, strength);
                }
            }
            else
            {
                Screen screen = Screen.AllScreens[Math.Max(0, displaySelector.SelectedIndex - 1)];
                ApplyOverlay(selected, screen, strength);
            }
        }

        private void ApplyOverlay(string name, Screen screen, double strength)
        {
            if (!overlays.ContainsKey(name))
            {
                var overlay = CreateOverlay(screen, strength);
                overlays[name] = overlay;
                overlay.Show();
            }
            else
            {
                overlays[name].Opacity = strength * 0.85;
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            // 🔹 Mark currently-dimmed displays as manually removed (only those keys)
            foreach (var key in overlays.Keys.ToList())
                manualDimOverrides.Add(key);

            foreach (var o in overlays.Values)
                o.Close();
            overlays.Clear();

            // Note: do not modify saved schedules here — we only prevent immediate reapply for these displays
        }

        private static Window CreateOverlay(Screen screen, double strength)
        {
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)),
                Opacity = strength * 0.85,
                Left = screen.Bounds.Left,
                Top = screen.Bounds.Top,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height
            };
            overlay.Loaded += (s, e) => MakeWindowClickThrough(overlay);
            return overlay;
        }

        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_LAYERED = 0x80000;

        static void MakeWindowClickThrough(Window w)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }
        #endregion

        #region Startup
        private void InitializeStartupSetting()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                if (key?.GetValue("DimmerApp") != null)
                    startWithWindows.IsChecked = true;
            }
            catch { }

            startWithWindows.Checked += (s, e) => SetStartup(true);
            startWithWindows.Unchecked += (s, e) => SetStartup(false);
        }

        private void SetStartup(bool enable)
        {
            string appName = "DimmerApp";
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (enable)
                    key?.SetValue(appName, $"\"{exePath}\" --minimized");
                else
                    key?.DeleteValue(appName, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update startup setting: {ex.Message}");
            }
        }
        #endregion

        #region Tray
        private void InitializeTrayIcon()
{
    try
    {
        // Dispose previous if any
        try { trayIcon?.Dispose(); } catch { }

        System.Drawing.Icon icon = null;
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();

        // 1) Try embedded assembly resource (any *.ico)
        try
        {
            var names = assembly.GetManifestResourceNames();
            var resName = names.FirstOrDefault(n => n.EndsWith("DimmerApp.ico", StringComparison.OrdinalIgnoreCase))
                          ?? names.FirstOrDefault(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));

            if (resName != null)
            {
                using var rs = assembly.GetManifestResourceStream(resName);
                if (rs != null)
                {
                    icon = new System.Drawing.Icon(rs);
                }
            }
        }
        catch
        {
            // ignore and try next
        }

        // 2) Try WPF pack URI (application resource)
        if (icon == null)
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/DimmerApp.ico"));
                if (info?.Stream != null)
                {
                    icon = new System.Drawing.Icon(info.Stream);
                }
            }
            catch
            {
                // ignore and try next
            }
        }

        // 3) Try file next to executable
        if (icon == null)
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DimmerApp.ico");
                if (File.Exists(iconPath))
                    icon = new System.Drawing.Icon(iconPath);
            }
            catch
            {
                // ignore and try fallback
            }
        }

        // 4) Final fallback: system application icon
        if (icon == null)
            icon = System.Drawing.SystemIcons.Application;

        // Create tray icon
        trayIcon = new NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "DimmerApp"
        };

        trayIcon.DoubleClick += (s, e) =>
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        };

        var menu = new ContextMenuStrip();
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) =>
        {
            _isExiting = true;
            try { trayIcon.Visible = false; } catch { }
            ApplicationWPF.Current.Shutdown();
        };
        menu.Items.Add(exitItem);
        trayIcon.ContextMenuStrip = menu;
    }
    catch
    {
        // Last-resort fallback: attempt to create a minimal tray icon silently.
        try
        {
            try { trayIcon?.Dispose(); } catch { }
            trayIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "DimmerApp"
            };

            trayIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };

            var menu = new ContextMenuStrip();
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) =>
            {
                _isExiting = true;
                try { trayIcon.Visible = false; } catch { }
                ApplicationWPF.Current.Shutdown();
            };
            menu.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = menu;
        }
        catch
        {
            // If even fallback fails, swallow: don't block startup with an error dialog.
        }
    }
}


        #endregion

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
                this.Hide();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
            }
            catch { }

            try
            {
                scheduleTimer.Stop();
                scheduleTimer.Dispose();
            }
            catch { }
        }

        #region Schedule
        private record ScheduleData
        {
            public bool Enabled { get; set; }
            public string Start { get; set; } = "";
            public string Stop { get; set; } = "";
            public double Strength { get; set; } = 0.5;
        }

        private void LoadSchedules()
        {
            try
            {
                if (File.Exists(scheduleFilePath))
                {
                    var json = File.ReadAllText(scheduleFilePath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, ScheduleData>>(json);
                    if (data != null)
                        foreach (var kvp in data)
                            schedules[kvp.Key] = kvp.Value;
                }
            }
            catch { }
        }

        private void SaveSchedules()
        {
            try
            {
                var json = JsonSerializer.Serialize(schedules, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(scheduleFilePath, json);
            }
            catch { }
        }

        private void ScheduleDisplaySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (scheduleDisplaySelector.SelectedItem is not string selected) return;

            if (schedules.TryGetValue(selected, out var s))
            {
                enableSchedule.IsChecked = s.Enabled;
                startTimeBox.Text = s.Start;
                stopTimeBox.Text = s.Stop;
                scheduleDimmingSlider.Value = s.Strength * 100;
            }
            else
            {
                enableSchedule.IsChecked = false;
                startTimeBox.Text = "";
                stopTimeBox.Text = "";
                scheduleDimmingSlider.Value = 50;
            }

            UpdateScheduleControlsState();
        }

        private void ScheduleTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
{
    Dispatcher.Invoke(() =>
    {
        foreach (var kvp in schedules)
        {
            var name = kvp.Key;
            var s = kvp.Value;
            if (!s.Enabled) continue;

            if (TryParseTime(s.Start, out var start) && TryParseTime(s.Stop, out var stop))
            {
                var now = DateTime.Now.TimeOfDay;
                bool inRange = start <= stop ? now >= start && now <= stop : now >= start || now <= stop;

                if (inRange)
                {
                    if (name == "All Displays")
                    {
                        foreach (var screen in Screen.AllScreens)
                        {
                            string displayName = GetMonitorFriendlyName(screen.DeviceName);
                            if (manualDimOverrides.Contains(displayName)) continue; // skip manually removed displays
                            ApplyOverlay(displayName, screen, s.Strength);
                        }
                    }
                    else
                    {
                        if (manualDimOverrides.Contains(name)) continue;

                        var idx = scheduleDisplaySelector.Items.IndexOf(name);
                        if (idx > 0 && idx - 1 < Screen.AllScreens.Length)
                        {
                            var screen = Screen.AllScreens[idx - 1];
                            ApplyOverlay(name, screen, s.Strength);
                        }
                    }
                }
                else
                {
                    // ✅ FIX: no longer call RemoveButton_Click(null, null);
                    // Instead, close overlays directly for that schedule entry
                    if (name == "All Displays")
                    {
                        foreach (var screen in Screen.AllScreens)
                        {
                            string displayName = GetMonitorFriendlyName(screen.DeviceName);
                            if (overlays.ContainsKey(displayName))
                            {
                                overlays[displayName].Close();
                                overlays.Remove(displayName);
                            }
                            manualDimOverrides.Remove(displayName);
                        }
                    }
                    else if (overlays.ContainsKey(name))
                    {
                        overlays[name].Close();
                        overlays.Remove(name);
                        manualDimOverrides.Remove(name);
                    }
                }
            }
        }
    });
}

        private void SetScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            if (scheduleDisplaySelector.SelectedItem is not string selected)
            {
                MessageBox.Show("Please select a display for schedule.");
                return;
            }

            if (TryParseTime(startTimeBox.Text, out TimeSpan start) &&
                TryParseTime(stopTimeBox.Text, out TimeSpan stop))
            {
                var data = new ScheduleData
                {
                    Enabled = enableSchedule.IsChecked == true,
                    Start = start.ToString(@"hh\:mm"),
                    Stop = stop.ToString(@"hh\:mm"),
                    Strength = scheduleDimmingSlider.Value / 100.0
                };

                schedules[selected] = data;
                SaveSchedules();
                MessageBox.Show($"Schedule saved for {selected}");
            }
            else
            {
                MessageBox.Show("Invalid time format. Use HH:MM 24h format.");
            }
        }

        private void ResetScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            if (scheduleDisplaySelector.SelectedItem is not string selected) return;

            if (schedules.ContainsKey(selected))
            {
                schedules.Remove(selected);
                SaveSchedules();
            }

            enableSchedule.IsChecked = false;
            startTimeBox.Text = "";
            stopTimeBox.Text = "";
            scheduleDimmingSlider.Value = 50;

            UpdateScheduleControlsState();

            // 🔹 Remove dim only for that display (or all if "All Displays"), and clear manual override for it.
            if (selected == "All Displays")
            {
                foreach (var key in overlays.Keys.ToList())
                {
                    overlays[key].Close();
                }
                overlays.Clear();
                manualDimOverrides.Clear();
            }
            else
            {
                if (overlays.ContainsKey(selected))
                {
                    overlays[selected].Close();
                    overlays.Remove(selected);
                }
                manualDimOverrides.Remove(selected);
            }

            MessageBox.Show($"Schedule reset for {selected}");
        }

        private static bool TryParseTime(string input, out TimeSpan time)
        {
            input = input.Replace(":", "");
            if (input.Length != 4)
            {
                time = TimeSpan.Zero;
                return false;
            }

            if (int.TryParse(input[..2], out int h) && int.TryParse(input[2..], out int m) && h < 24 && m < 60)
            {
                time = new TimeSpan(h, m, 0);
                return true;
            }

            time = TimeSpan.Zero;
            return false;
        }

        private void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox box) return;
            string t = box.Text.Replace(":", "");
            if (t.Length == 4)
            {
                box.TextChanged -= TimeBox_TextChanged;
                box.Text = t.Insert(2, ":");
                box.SelectionStart = box.Text.Length;
                box.TextChanged += TimeBox_TextChanged;
            }
        }

        private void UpdateScheduleControlsState()
        {
            bool isEnabled = enableSchedule.IsChecked == true;

            scheduleDisplaySelector.IsEnabled = true;
            startTimeBox.IsEnabled = isEnabled;
            stopTimeBox.IsEnabled = isEnabled;
            scheduleDimmingSlider.IsEnabled = isEnabled;
            setScheduleButton.IsEnabled = isEnabled;
            resetScheduleButton.IsEnabled = true; // always allow reset
        }
        #endregion
    }
}
