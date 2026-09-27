using System.Windows;
using Nightrunner.Core.Logging;

namespace Nightrunner.UI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>The window in front and the open game, kept by the main window for crash reports.</summary>
        internal static volatile string ActivePanel = "", Game = "";

        private static Exception? _reported;

        protected override void OnStartup(StartupEventArgs e)
        {
            // the packs are mapped into memory: a 32-bit process runs out of address space and falls apart
            if (!Environment.Is64BitProcess)
            {
                MessageBox.Show("Nightrunner needs 64-bit Windows and the x64 build.", "Nightrunner",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            DispatcherUnhandledException += (_, a) =>
            {
                string? path = Crashed("ui", a.Exception);
                MessageBox.Show(path is null ? a.Exception.Message : $"{a.Exception.Message}\n\n{path}",
                                "Nightrunner crashed", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
                Crashed("unhandled", a.ExceptionObject as Exception ?? new Exception($"{a.ExceptionObject}"));
            TaskScheduler.UnobservedTaskException += (_, a) => Crashed("task", a.Exception);
            Log.Info("app", CrashReport.Process);

            base.OnStartup(e);
            new MainWindow().Show();
        }

        /// <summary>Report once: a UI exception left unhandled reaches the AppDomain hook as well.</summary>
        private static string? Crashed(string kind, Exception e)
        {
            if (ReferenceEquals(Interlocked.Exchange(ref _reported, e), e)) return null;
            string context = $"panel {Show(ActivePanel)} · job {Show(Jobs.Running?.Title)} · game {Show(Game)}";
            return CrashReport.Write(kind, e, context);

            static string Show(string? s) => string.IsNullOrEmpty(s) ? "none" : s;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Viewport.Gpu.Shutdown();
            base.OnExit(e);
        }
    }

}
