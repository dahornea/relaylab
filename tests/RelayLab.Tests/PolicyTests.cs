using RelayLab.Core;
using Xunit;

namespace RelayLab.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("tab\tkey")]
    [InlineData("unicode-ă")]
    [InlineData("trailing ")]
    public void Keys_reject_whitespace_and_nonvisible_ASCII(string key) =>
        Assert.Contains("Idempotency-Key", EventContract.Validate(TestRuntime.Event(), key));

    [Fact]
    public void Identifier_and_key_lengths_have_explicit_boundaries()
    {
        Assert.Empty(EventContract.Validate(TestRuntime.Event(new string('d', 128)), new string('k', 128)));
        var errors = EventContract.Validate(TestRuntime.Event(new string('d', 129)), new string('k', 129));
        Assert.Contains("Idempotency-Key", errors);
        Assert.Contains("data.documentId", errors);
        Assert.Contains("data.documentId", EventContract.Validate(TestRuntime.Event("  "), "key"));
    }
}
