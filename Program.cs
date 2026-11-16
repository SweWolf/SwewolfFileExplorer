using System.Windows;

namespace SwewolfFileExplorer;

/// <summary>
/// The entry point. It only decides about the splash screen and then runs the generated <see cref="App.Main"/>: a normal start shows the
/// splash at once (before WPF and the main window are ready; it closes by itself when the first window appears), the administrator
/// helper of Paste (<c>--elevated-transfer</c>, the same EXE started again with <c>runas</c>) has no window and shows nothing.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!Helpers.ElevatedTransfer.IsHelperStart(args))
        {
            var splash = new SplashScreen("resourses/splash.png");
            splash.Show(true);
        }
        App.Main();
    }
}
