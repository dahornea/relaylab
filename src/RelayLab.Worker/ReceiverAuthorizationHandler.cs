using System.Net.Http.Headers;
using Azure.Core;

namespace RelayLab.Worker;

// Azure.Identity caches/refreshes this identity's token; no static bearer secret is stored.
public sealed class ReceiverAuthorizationHandler(TokenCredential credential, string audience, Uri destination) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri != destination || request.RequestUri.Scheme != "https")
            throw new InvalidOperationException("Refusing to send the receiver token to another destination.");
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([$"api://{audience}/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        catch (Azure.Identity.AuthenticationFailedException error)
        {
            throw new HttpRequestException("Receiver authentication is temporarily unavailable.", error);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
