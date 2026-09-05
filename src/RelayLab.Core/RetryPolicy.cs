using Microsoft.Extensions.Configuration;

namespace RelayLab.Core;

public sealed record RetryPolicy(int MaxAttempts = 3, int BaseSeconds = 2)
{
    public static RetryPolicy From(IConfiguration configuration)
    {
        var policy = new RetryPolicy(configuration.GetValue("RelayLab:MaxAttempts", 3),
            configuration.GetValue("RelayLab:RetryBaseSeconds", 2));
        if (policy.MaxAttempts is < 1 or > 5 || policy.BaseSeconds is < 1 or > 30)
            throw new InvalidOperationException("Invalid RelayLab retry bounds.");
        return policy;
    }

    public static int DelaySeconds(Delivery delivery) => Math.Min(30, delivery.RetryBaseSeconds * (1 << (delivery.AttemptNumber - 1)));
    public static bool IsRetryable(int status) => status is 408 or 429 or (>= 500 and <= 599);
}
