using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Azure.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RelayLab.Core;
using RelayLab.Worker;
using Xunit;

namespace RelayLab.Tests;

public sealed class CloudSecurityTests
{
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Audience = "22222222-2222-4222-8222-222222222222";
    private const string Caller = "33333333-3333-4333-8333-333333333333";
    private const string Operator = "44444444-4444-4444-8444-444444444444";
    private const string Issuer = "https://login.microsoftonline.com/" + Tenant + "/v2.0";

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("forged", 401)]
    [InlineData("expired", 401)]
    [InlineData("audience", 401)]
    [InlineData("issuer", 401)]
    [InlineData("tenant", 403)]
    [InlineData("caller", 403)]
    [InlineData("delegated", 403)]
    [InlineData("user", 403)]
    [InlineData("valid", 200)]
    public async Task Production_bearer_validation_rejects_invalid_tokens_and_unauthorized_workloads(string scenario, int status)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "synthetic-test-signing-key" };
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Production";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:TenantId"] = Tenant, ["Security:Audience"] = Audience,
            ["Security:CallerObjectId"] = Caller, ["Security:OperatorObjectId"] = Operator
        });
        CloudHosting.ConfigureAuthentication(builder);
        // Test-only metadata replaces Entra discovery; the real JWT signature/issuer/audience/lifetime checks still run.
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            o.Configuration.SigningKeys.Add(signingKey);
            o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(o.Configuration);
        });
        await using var app = builder.Build();
        CloudHosting.UseAuthentication(app);
        app.MapGet("/protected", () => "authorized");
        app.MapGet("/operator", () => "operator").RequireAuthorization(CloudHosting.OperatorPolicy);
        app.MapGet("/health/live", () => "live").AllowAnonymous();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var health = await http.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        if (scenario != "missing")
        {
            var claims = new List<Claim> { new("tid", scenario == "tenant" ? Operator : Tenant),
                new("oid", scenario == "caller" ? Operator : Caller), new("idtyp", scenario == "user" ? "user" : "app") };
            if (scenario == "delegated") claims.Add(new("scp", "synthetic-delegated-scope"));
            using var otherRsa = RSA.Create(2048);
            var key = scenario == "forged" ? new RsaSecurityKey(otherRsa) { KeyId = signingKey.KeyId } : signingKey;
            var token = new JwtSecurityToken(scenario == "issuer" ? "https://untrusted.example" : Issuer,
                scenario == "audience" ? Operator : Audience, claims, DateTime.UtcNow.AddMinutes(-10),
                DateTime.UtcNow.AddMinutes(scenario == "expired" ? -5 : 5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        }
        using var result = await http.GetAsync("/protected");
        Assert.Equal(status, (int)result.StatusCode);
        if (scenario == "valid")
        {
            using var diagnostic = await http.GetAsync("/operator");
            Assert.Equal(HttpStatusCode.Forbidden, diagnostic.StatusCode);
        }
    }

    [Fact]
    public async Task Shipping_cloud_routes_require_authentication_before_accessing_SQL()
    {
        void Configure(WebApplicationBuilder builder)
        {
            builder.Environment.EnvironmentName = "Production";
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var connection = $"Server=tcp:synthetic.database.windows.net,1433;Database=synthetic;Authentication=Active Directory Managed Identity;User Id={Caller};Encrypt=True;TrustServerCertificate=False";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TenantId"] = Tenant, ["Security:Audience"] = Audience, ["Security:CallerObjectId"] = Caller,
                ["Security:OperatorObjectId"] = Operator, ["Azure:ClientId"] = Caller,
                ["ConnectionStrings:RelayLab"] = connection, ["ConnectionStrings:Receiver"] = connection
            });
        }
        await using var api = RelayLab.Api.Program.Build([], Configure);
        await using var receiver = RelayLab.Receiver.Program.Build([], Configure);
        await api.StartAsync();
        await receiver.StartAsync();
        using var apiClient = new HttpClient { BaseAddress = new Uri(api.Urls.Single()) };
        using var receiverClient = new HttpClient { BaseAddress = new Uri(receiver.Urls.Single()) };
        foreach (var (client, path, method) in new[] { (apiClient,"/events",HttpMethod.Post), (apiClient,$"/deliveries/{Caller}",HttpMethod.Get),
            (apiClient,$"/deliveries/{Caller}/replay",HttpMethod.Post), (receiverClient,"/webhooks",HttpMethod.Post), (receiverClient,$"/receipts/{Caller}",HttpMethod.Get) })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public void Production_SQL_configuration_rejects_passwords_and_disabled_TLS_validation()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Azure:ClientId"] = Caller, ["ConnectionStrings:RelayLab"] = "Server=localhost;Database=test;User ID=sa;Password=synthetic-test-only;TrustServerCertificate=True" }).Build();
        Assert.Throws<InvalidOperationException>(() => CloudHosting.SqlConnection(config, "RelayLab", true));
    }

    [Fact]
    public async Task Receiver_token_is_sent_only_to_the_configured_HTTPS_destination()
    {
        var destination = new Uri("https://receiver.example/webhooks");
        var credential = new SyntheticCredential();
        var capture = new CaptureHandler();
        using var auth = new ReceiverAuthorizationHandler(credential, Audience, destination) { InnerHandler = capture };
        using var client = new HttpClient(auth);
        using var response = await client.PostAsync(destination, null);
        Assert.Equal("synthetic-test-access-token", capture.Token);
        Assert.Equal($"api://{Audience}/.default", credential.Scope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("https://elsewhere.example/webhooks", null));
        Assert.Equal(1, capture.Calls);
    }
    private sealed class SyntheticCredential : TokenCredential
    {
        public string? Scope { get; private set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
        { Scope = Assert.Single(context.Scopes); return ValueTask.FromResult(new AccessToken("synthetic-test-access-token", DateTimeOffset.UtcNow.AddMinutes(5))); }
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Token { get; private set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; Token = request.Headers.Authorization?.Parameter; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
