using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace ReactionTimer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        using Stream stream = Resource("ReactionTimer.xaml");
        Window window = (Window)XamlReader.Load(stream);

        new ReactionController(window);
        app.Run(window);
    }

    internal static Stream Resource(string name)
    {
        return typeof(Program).Assembly.GetManifestResourceStream(name);
    }

    internal static BitmapImage LoadImage(string name)
    {
        using Stream stream = Resource(name);
        BitmapImage image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
