using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace SteamSmart.Desktop
{
    internal sealed class ThemeManager : IDisposable
    {
        private readonly Window window;
        private readonly FrameworkElement view;
        private readonly string settingsPath;
        private readonly Dictionary<string, Color> light = new Dictionary<string, Color>();
        private bool disposed;
        private bool listening;
        public string Mode { get; private set; }
        public bool IsDark { get; private set; }
        public event EventHandler Changed;

        private static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
        {
            { "Ink", "#E8EDF5" }, { "Muted", "#ABB7CA" }, { "Line", "#303A4B" },
            { "PageBackground", "#11161F" }, { "Surface", "#1B2230" }, { "SurfaceAlt", "#202938" },
            { "Border", "#3B485C" }, { "TextPrimary", "#E8EDF5" }, { "TextSecondary", "#ABB7CA" },
            { "TextTertiary", "#98A6BC" }, { "Accent", "#356CE5" }, { "AccentText", "#FFFFFF" },
            { "AccentForeground", "#94B8FF" }, { "AccentSurface", "#253A5D" }, { "InfoText", "#BCD2FA" },
            { "HoverSurface", "#273449" }, { "SelectionSurface", "#2B4163" },
            { "Success", "#7FDFB0" }, { "SuccessSurface", "#203E35" },
            { "Danger", "#FFAAA5" }, { "DangerSurface", "#492B32" },
            { "Warning", "#F1CB7D" }, { "WarningSurface", "#463B27" }, { "NeutralSurface", "#293446" },
            { "Focus", "#8BB3FF" }, { "Track", "#171E29" },
            { "ScrollThumb", "#52627A" }, { "ScrollThumbHover", "#7488A6" }
        };

        public ThemeManager(Window owner, FrameworkElement root, string dataDirectory)
        {
            window = owner;
            view = root;
            settingsPath = Path.Combine(dataDirectory, "ui-settings.json");
            foreach (object key in view.Resources.Keys)
            {
                var brush = view.Resources[key] as SolidColorBrush;
                if (key is string && brush != null) light[(string)key] = brush.Color;
            }
            Mode = ReadMode();
            window.SourceInitialized += OnSourceInitialized;
            try { SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged; listening = true; }
            catch (InvalidOperationException) { }
        }

        private static bool ValidMode(string value) { return value == "dark" || value == "light" || value == "system"; }

        private string ReadMode()
        {
            try
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsPath));
                object value;
                if (data != null && data.TryGetValue("theme", out value) && ValidMode(value as string)) return (string)value;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return "dark";
        }

        public bool SetMode(string value)
        {
            if (!ValidMode(value)) throw new ArgumentException("无效的外观模式。", "value");
            Mode = value;
            Apply();
            string temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new { theme = value }), new UTF8Encoding(false));
                if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null);
                else File.Move(temporary, settingsPath);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static bool SystemIsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
                }
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (FormatException) { return false; }
            catch (InvalidCastException) { return false; }
        }

        public void Apply()
        {
            if (disposed) return;
            bool contrast = SystemParameters.HighContrast;
            IsDark = !contrast && (Mode == "dark" || (Mode == "system" && SystemIsDark()));
            foreach (var entry in light)
            {
                string darkColor;
                Color color = IsDark && Dark.TryGetValue(entry.Key, out darkColor) ? (Color)ColorConverter.ConvertFromString(darkColor) : entry.Value;
                if (contrast) color = ContrastColor(entry.Key);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                view.Resources[entry.Key] = brush;
            }
            window.Background = Brush("PageBackground");
            window.Foreground = Brush("TextPrimary");
            ApplyTitleBar();
            EventHandler changed = Changed;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private static Color ContrastColor(string key)
        {
            if (key == "Accent" || key == "SelectionSurface" || key == "AccentSurface") return SystemColors.HighlightColor;
            if (key == "AccentText") return SystemColors.HighlightTextColor;
            if (key == "Border" || key == "Line" || key == "Focus" || key == "ScrollThumb" || key == "ScrollThumbHover") return SystemColors.WindowTextColor;
            if (key == "Ink" || key == "Muted" || key.StartsWith("Text", StringComparison.Ordinal) || key == "AccentForeground" || key == "InfoText" || key == "Success" || key == "Danger" || key == "Warning") return SystemColors.WindowTextColor;
            return SystemColors.WindowColor;
        }

        public Brush Brush(string key) { return (Brush)view.Resources[key]; }

        public void ApplyTray(Forms.ContextMenuStrip menu)
        {
            menu.Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors(this));
            menu.BackColor = DrawingColor("Surface");
            menu.ForeColor = DrawingColor("TextPrimary");
            foreach (Forms.ToolStripItem item in menu.Items)
            {
                item.BackColor = menu.BackColor;
                item.ForeColor = menu.ForeColor;
            }
        }

        private System.Drawing.Color DrawingColor(string key)
        {
            Color color = ((SolidColorBrush)Brush(key)).Color;
            return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        private sealed class TrayColors : Forms.ProfessionalColorTable
        {
            private readonly ThemeManager theme;
            public TrayColors(ThemeManager manager) { theme = manager; UseSystemColors = SystemParameters.HighContrast; }
            public override System.Drawing.Color ToolStripDropDownBackground { get { return theme.DrawingColor("Surface"); } }
            public override System.Drawing.Color MenuBorder { get { return theme.DrawingColor("Border"); } }
            public override System.Drawing.Color ImageMarginGradientBegin { get { return theme.DrawingColor("Surface"); } }
            public override System.Drawing.Color ImageMarginGradientMiddle { get { return theme.DrawingColor("Surface"); } }
            public override System.Drawing.Color ImageMarginGradientEnd { get { return theme.DrawingColor("Surface"); } }
            public override System.Drawing.Color MenuItemSelected { get { return theme.DrawingColor("SelectionSurface"); } }
            public override System.Drawing.Color MenuItemBorder { get { return theme.DrawingColor("Border"); } }
            public override System.Drawing.Color SeparatorDark { get { return theme.DrawingColor("Line"); } }
            public override System.Drawing.Color SeparatorLight { get { return theme.DrawingColor("Surface"); } }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

        private void ApplyTitleBar()
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            try
            {
                int dark = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, 20, ref dark, 4) < 0) DwmSetWindowAttribute(handle, 19, ref dark, 4);
                int background = SystemParameters.HighContrast ? -1 : ColorRef("Surface");
                int foreground = SystemParameters.HighContrast ? -1 : ColorRef("TextPrimary");
                DwmSetWindowAttribute(handle, 35, ref background, 4);
                DwmSetWindowAttribute(handle, 36, ref foreground, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private int ColorRef(string key)
        {
            Color color = ((SolidColorBrush)Brush(key)).Color;
            return color.R | (color.G << 8) | (color.B << 16);
        }

        private void OnSourceInitialized(object sender, EventArgs args) { ApplyTitleBar(); }
        private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
        {
            if (disposed || window.Dispatcher.HasShutdownStarted) return;
            window.Dispatcher.BeginInvoke(new Action(delegate { if (!disposed) Apply(); }));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            window.SourceInitialized -= OnSourceInitialized;
            if (listening) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        }
    }
}
