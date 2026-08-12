using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitSvnShuttle.Core;

namespace GitSvnShuttle.AcpProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[4], out var expectedAcp))
        {
            Console.Error.WriteLine(
                "Usage: GitSvnShuttle.AcpProbe <git.exe> <repository> <author> <subject> <expected-acp>");
            return 2;
        }

        var actualAcp = checked((int)GetACP());
        var defaultCodePage = Encoding.Default.CodePage;
        if (actualAcp != expectedAcp || defaultCodePage != expectedAcp)
        {
            Console.Error.WriteLine(
                "ACP isolation failed. GetACP=" + actualAcp +
                ", Encoding.Default=" + defaultCodePage +
                ", expected=" + expectedAcp);
            return 3;
        }

        var service = new GitSvnWorkspaceService(new ProcessGitCommandRunner(args[0]));
        var repository = await service.InspectAsync(args[1], CancellationToken.None).ConfigureAwait(false);
        var commit = repository.PendingCommits.SingleOrDefault();
        if (commit == null ||
            !string.Equals(commit.Author, args[2], StringComparison.Ordinal) ||
            !string.Equals(commit.Subject, args[3], StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Git commit metadata did not match the expected Unicode strings.");
            return 4;
        }

        Console.WriteLine("ACP=" + actualAcp);
        Console.WriteLine("DEFAULT_ENCODING=" + defaultCodePage);
        Console.WriteLine("AUTHOR_UTF8_BASE64=" + ToUtf8Base64(commit.Author));
        Console.WriteLine("SUBJECT_UTF8_BASE64=" + ToUtf8Base64(commit.Subject));
        return 0;
    }

    private static string ToUtf8Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
