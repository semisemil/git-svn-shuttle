using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        try
        {
            RenderPreview(renderPath!, width, height, ReadOption(e.Args, "--scenario"), e.Args.Contains("--verify-management"));
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.GetFullPath(renderPath!) + ".failed.txt", exception.ToString());
            Shutdown(1);
            return;
        }
        Shutdown();
    }

    private static void RenderPreview(string outputPath, double width, double height, string? scenario, bool verify)
    {
        using var bindingTrace = new StringWriter();
        var listener = new TextWriterTraceListener(bindingTrace);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var data = PreviewData.Create(scenario);
        var control = new GitSvnShuttleControl
        {
            DataContext = data,
            PreserveDataContextOnLoad = true,
            Width = width,
            Height = height,
        };
        if (scenario == "advanced") ((Expander)control.FindName("BranchAdvancedSettings")).IsExpanded = true;
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
        if (verify)
        {
            VerifyManagement(control, data, scenario);
            File.WriteAllText(fullPath + ".verified.txt", "WPF management bindings and interaction checks passed: " + scenario);
        }
        listener.Flush();
        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        File.WriteAllText(fullPath + ".bindings.txt", bindingTrace.ToString());
    }

    private static void VerifyManagement(GitSvnShuttleControl control, PreviewViewModel data, string? scenario)
    {
        void Check(bool condition, string problem)
        {
            if (!condition) throw new InvalidOperationException(problem);
        }
        var name = (TextBox)control.FindName("BranchNameInput");
        var source = (ComboBox)control.FindName("BranchSourceInput");
        var destination = (ComboBox)control.FindName("BranchDestinationInput");
        var advanced = (Expander)control.FindName("BranchAdvancedSettings");
        Check(((Button)control.FindName("CloseManagementButton")).IsEnabled, "Close must remain available.");
        if (scenario == "busy")
        {
            Check(!name.IsEnabled && !source.IsEnabled && !destination.IsEnabled, "Editable controls must be locked during the query.");
            Check(((Button)control.FindName("CancelManagementButton")).IsEnabled, "Cancellation must remain available.");
            return;
        }
        if (scenario == "connection") return;
        Check(source.Text == data.BranchForm.SourcePath, "The source input must show the actual form source.");
        Check(destination.Text == data.BranchForm.DestinationPath, "The destination input must show the actual form destination.");
        if (scenario == "branch")
        {
            var paths = new GitSvnShuttle.Core.GitSvnTrackingPaths("https://svn.example.test/repos/team",
                new[] { "trunk/ProjectA" }, new[] { "branches/ProjectA/*" }, Array.Empty<string>());
            data.BranchForm.ApplyTracking(paths, "https://svn.example.test/repos/team/branches/ProjectA/current-work");
            control.UpdateLayout();
            Check(data.BranchForm.SourceUrl.EndsWith("/branches/ProjectA/current-work", StringComparison.Ordinal), "A completed connection query must update the automatic source.");
            source.Text = "trunk/ProjectA";
            source.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            data.BranchForm.ApplyTracking(paths, "https://svn.example.test/repos/team/branches/ProjectA/current-work");
            Check(data.BranchForm.SourceUrl.EndsWith("/trunk/ProjectA", StringComparison.Ordinal), "A refresh must preserve the source entered through the real control.");
        }
        if (scenario != "advanced") Check(!advanced.IsExpanded, "Advanced settings must start collapsed.");
        if (scenario == "multiple")
        {
            Check(!data.BranchForm.IsReady && source.Text.Length == 0 && destination.Text.Length == 0, "An ambiguous layout must require a choice.");
            source.SelectedIndex = 1;
            source.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            destination.SelectedIndex = 0;
            destination.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
        }
        name.Text = "release-next";
        name.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Check(data.BranchForm.CreateRequest().BranchName == "release-next", "Typing a name must change the creation request.");
        Check(data.BranchForm.DestinationUrl.EndsWith("/release-next", StringComparison.Ordinal), "The preview must follow the edited name.");
        advanced.IsExpanded = true;
        control.UpdateLayout();
        var revision = (TextBox)control.FindName("BranchRevisionInput");
        revision.Text = "42";
        revision.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        var local = (TextBox)control.FindName("LocalBranchInput");
        local.Text = "work/release-next";
        local.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        var request = data.BranchForm.CreateRequest();
        Check(request.Revision == "42" && request.LocalBranchName == "work/release-next", "Advanced inputs must reach the creation request.");
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
