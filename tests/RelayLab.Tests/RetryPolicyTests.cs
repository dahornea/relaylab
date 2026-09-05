using RelayLab.Core;
using Xunit;

namespace RelayLab.Tests;

public sealed class RetryPolicyTests
{
    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(409, false)]
    [InlineData(302, false)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    public void Classifier_matches_the_explicit_retry_contract(int status, bool retryable) => Assert.Equal(retryable, RetryPolicy.IsRetryable(status));

    [Fact]
    public void Backoff_doubles_and_caps_at_thirty_seconds()
    {
        var delivery = new Delivery { RetryBaseSeconds = 2 };
        var delays = Enumerable.Range(1, 5).Select(n => { delivery.AttemptNumber = n; return RetryPolicy.DelaySeconds(delivery); }).ToArray();
        Assert.Equal(new[] { 2, 4, 8, 16, 30 }, delays);
    }
}
