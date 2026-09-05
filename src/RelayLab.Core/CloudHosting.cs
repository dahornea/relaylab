using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace RelayLab.Core;

public static class CloudHosting
{
    public const string CallerPolicy = "CloudCaller";
    public const string OperatorPolicy = "CloudOperator";
    public static bool IsCloud(IHostEnvironment environment) => environment.IsProduction();

    public static string RequiredGuid(IConfiguration configuration, string key) =>
        Guid.TryParse(configuration[key], out var id) && id != Guid.Empty ? id.ToString("D")
            : throw new InvalidOperationException($"Configure {key} as a nonempty UUID.");

    public static TokenCredential Credential(IConfiguration configuration) =>
        new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(RequiredGuid(configuration, "Azure:ClientId")));

    public static string SqlConnection(IConfiguration configuration, string name, bool cloud)
    {
        var value = LocalHosting.Connection(configuration, name);
        if (!cloud) return value;
        var connection = new SqlConnectionStringBuilder(value);
        if (connection.Authentication != SqlAuthenticationMethod.ActiveDirectoryManagedIdentity ||
            connection.UserID != RequiredGuid(configuration, "Azure:ClientId") ||
            connection.TrustServerCertificate || connection.Encrypt == SqlConnectionEncryptOption.Optional ||
            !string.IsNullOrEmpty(connection.Password) || string.IsNullOrWhiteSpace(connection.InitialCatalog) ||
            !connection.DataSource.EndsWith(".database.windows.net,1433", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production SQL requires the configured managed identity, a database and validated Azure SQL TLS.");
        return value;
    }

    public static void ConfigureAuthentication(WebApplicationBuilder builder)
    {
        if (!IsCloud(builder.Environment)) return;
        var tenant = RequiredGuid(builder.Configuration, "Security:TenantId");
        var audience = RequiredGuid(builder.Configuration, "Security:Audience");
        var caller = RequiredGuid(builder.Configuration, "Security:CallerObjectId");
        var operatorId = RequiredGuid(builder.Configuration, "Security:OperatorObjectId");
        var issuer = $"https://login.microsoftonline.com/{tenant}/v2.0";
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = issuer;
            options.Audience = audience;
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
                ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
                ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(30), ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
        });
        builder.Services.AddAuthorization(options =>
        {
            AuthorizationPolicy Policy(string objectId) => new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
                .RequireClaim("tid", tenant).RequireClaim("oid", objectId).RequireClaim("idtyp", "app")
                .RequireAssertion(context => !context.User.HasClaim(c => c.Type == "scp")).Build();
            options.AddPolicy(CallerPolicy, Policy(caller));
            options.AddPolicy(OperatorPolicy, Policy(operatorId));
            options.FallbackPolicy = Policy(caller);
        });
    }

    public static void UseAuthentication(WebApplication app)
    {
        if (!IsCloud(app.Environment)) return;
        app.UseAuthentication();
        app.UseAuthorization();
    }
}
