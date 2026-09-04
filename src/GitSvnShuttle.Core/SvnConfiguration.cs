using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GitSvnShuttle.Core;

internal static class SvnConfiguration
{
    internal static IReadOnlyList<string> ExtractSvnTargets(string configOutput)
    {
        var entries = configOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var separator = line.IndexOfAny(new[] { ' ', '\t' });
                return separator > 0
                    ? new { Key = line.Substring(0, separator), Value = line.Substring(separator + 1).Trim() }
                    : null;
            })
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Value))
            .ToArray();

        var commitTargets = entries
            .Where(entry => entry!.Key.Equals("svn.commiturl", StringComparison.OrdinalIgnoreCase) ||
                            entry.Key.EndsWith(".commiturl", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry!.Value)
            .ToArray();
        var targets = commitTargets.Length > 0
            ? commitTargets
            : entries
                .Where(entry => entry!.Key.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry!.Value)
                .ToArray();

        return targets
            .Select(SensitiveTextRedactor.Redact)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string ComputeFingerprint(string value)
    {
        using (var sha256 = SHA256.Create())
        {
            return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
    }
}
