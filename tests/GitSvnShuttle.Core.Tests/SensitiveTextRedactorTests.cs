using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class SensitiveTextRedactorTests
{
    [Theory]
    [InlineData("https://user:secret@svn.example.test/trunk", "https://***@svn.example.test/trunk")]
    [InlineData("request?token=abc123&mode=1", "request?token=***&mode=1")]
    [InlineData("--password hunter2", "--password ***")]
    [InlineData("Authorization: Bearer-value", "Authorization: ***")]
    [InlineData("Authorization: Basic YWJjZA==", "Authorization: ***")]
    public void Redact_RemovesCredentialShapedValues(string input, string expected)
    {
        Assert.Equal(expected, SensitiveTextRedactor.Redact(input));
    }
}
