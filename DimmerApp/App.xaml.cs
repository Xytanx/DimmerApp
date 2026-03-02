using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace DimmerApp
{
    // Explicitly use WPF Application (fully-qualified)
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private const string MutexName = "DimmerAppSingleInstance";
        private const string MessageName = "DimmerApp_ShowWindow";

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private static uint _showMessage;

        public App()
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            _showMessage = RegisterWindowMessage(MessageName);
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                // Another instance exists — bring it to front
                IntPtr existingWindow = FindWindow(null, "Screen Dimmer");
                if (existingWindow != IntPtr.Zero)
                {
                    PostMessage(existingWindow, _showMessage, IntPtr.Zero, IntPtr.Zero);
                }
                Environment.Exit(0);
                return;
            }

            base.OnStartup(e);

            try
            {
                var mainWindow = new MainWindow();
                MainWindow = mainWindow;

                mainWindow.SourceInitialized += (_, _) =>
                {
                    var source = HwndSource.FromHwnd(new WindowInteropHelper(mainWindow).Handle);
                    source.AddHook(WndProc);
                };

                // Check if started with `--minimized` argument (for Windows startup)
                bool startMinimized = false;
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                    {
                        startMinimized = true;
                        break;
                    }
                }

                if (startMinimized)
                {
                    // Don’t show window, just initialize tray
                    mainWindow.Hide();
                }
                else
                {
                    mainWindow.Show();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Error starting app:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == _showMessage)
            {
                if (MainWindow != null)
                {
                    if (MainWindow.WindowState == WindowState.Minimized)
                        MainWindow.WindowState = WindowState.Normal;

                    MainWindow.Show();
                    MainWindow.Activate();
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            System.Windows.MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"A fatal error occurred:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "An unknown fatal error occurred.", "Fatal Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
