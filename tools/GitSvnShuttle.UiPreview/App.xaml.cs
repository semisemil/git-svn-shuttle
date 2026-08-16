using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GitSvnShuttle.Vsix;

namespace GitSvnShuttle.UiPreview;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var renderPath = ReadOption(e.Args, "--render");
        if (string.IsNullOrWhiteSpace(renderPath))
        {
            ThemePalette.Apply("dark");
            new PreviewWindow().Show();
            return;
        }

        var theme = ReadOption(e.Args, "--theme") ?? "dark";
        var width = ReadDouble(e.Args, "--width", 1280);
        var height = ReadDouble(e.Args, "--height", 760);
        ThemePalette.Apply(theme);
        RenderPreview(renderPath!, width, height);
        Shutdown();
    }

    private static void RenderPreview(string outputPath, double width, double height)
    {
        var control = new GitSvnShuttleControl
        {
            DataContext = PreviewData.Create(),
            PreserveDataContextOnLoad = true,
            Width = width,
            Height = height,
        };
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        const double dpi = 96;
        var bitmap = new RenderTargetBitmap((int)width, (int)height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using (var stream = File.Create(fullPath))
        {
            encoder.Save(stream);
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double ReadDouble(string[] args, string name, double fallback) =>
        double.TryParse(ReadOption(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
