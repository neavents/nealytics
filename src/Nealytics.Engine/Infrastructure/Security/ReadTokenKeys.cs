namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed class ReadTokenKeys
{
    public const int MinimumSymmetricKeyBytes = 32;

    private ReadTokenKeys(IReadOnlyList<SecurityKey> staticKeys, Uri? jwksAddress)
    {
        StaticKeys = staticKeys;
        JwksAddress = jwksAddress;
    }

    public IReadOnlyList<SecurityKey> StaticKeys { get; }

    public Uri? JwksAddress { get; }

    public JwksKeyCache? Jwks { get; private set; }

    public static ReadTokenKeys From(TelemetryEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<SecurityKey> keys = [];

        if (!string.IsNullOrWhiteSpace(options.JwtSymmetricKey))
        {
            if (Encoding.UTF8.GetByteCount(options.JwtSymmetricKey) < MinimumSymmetricKeyBytes)
            {
                throw new InvalidOperationException("TelemetryEngine:JwtSymmetricKey must be at least 32 bytes.");
            }

            keys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSymmetricKey)));
        }

        if (!string.IsNullOrWhiteSpace(options.JwtPublicKeyPem))
        {
            keys.Add(ParsePem(options.JwtPublicKeyPem));
        }

        Uri? jwks = null;

        if (!string.IsNullOrWhiteSpace(options.JwtJwksUrl))
        {
            if (!Uri.TryCreate(options.JwtJwksUrl.Trim(), UriKind.Absolute, out jwks)
                || !(jwks.Scheme == Uri.UriSchemeHttps || (jwks.Scheme == Uri.UriSchemeHttp && jwks.IsLoopback)))
            {
                throw new InvalidOperationException(
                    "TelemetryEngine:JwtJwksUrl must be an absolute https URL (plain http is accepted only for "
                    + "a loopback address).");
            }

            if (options.JwtJwksRefreshMinutes <= 0)
            {
                throw new InvalidOperationException("TelemetryEngine:JwtJwksRefreshMinutes must be positive.");
            }
        }

        if (keys.Count == 0 && jwks is null)
        {
            throw new InvalidOperationException(
                "TelemetryEngine:JwtSymmetricKey must be at least 32 bytes, unless read tokens are verified "
                + "with TelemetryEngine:JwtPublicKeyPem or TelemetryEngine:JwtJwksUrl instead.");
        }

        return new ReadTokenKeys(keys, jwks);
    }

    public static SecurityKey ParsePem(string pem)
    {
        try
        {
            RSA rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return new RsaSecurityKey(rsa);
        }
        catch (ArgumentException)
        {
        }
        catch (CryptographicException)
        {
        }

        try
        {
            ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return new ECDsaSecurityKey(ecdsa);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException(
                "TelemetryEngine:JwtPublicKeyPem is not an RSA or EC public key in PEM form.", exception);
        }
    }

    public JwksKeyCache? CreateJwksCache(TelemetryEngineOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (JwksAddress is null)
        {
            return null;
        }

        return UseJwks(new JwksKeyCache(
            JwksAddress,
            TimeSpan.FromMinutes(options.JwtJwksRefreshMinutes),
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            logger));
    }

    internal JwksKeyCache UseJwks(JwksKeyCache cache)
    {
        Jwks = cache;
        return cache;
    }

    public TokenValidationParameters ValidationParameters(TelemetryEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool validateIssuer = !string.IsNullOrWhiteSpace(options.JwtIssuer);
        bool validateAudience = !string.IsNullOrWhiteSpace(options.JwtAudience);

        TokenValidationParameters parameters = new()
        {
            ValidateIssuer = validateIssuer,
            ValidIssuer = validateIssuer ? options.JwtIssuer.Trim() : null,
            ValidateAudience = validateAudience,
            ValidAudience = validateAudience ? options.JwtAudience.Trim() : null,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = StaticKeys,
            ClockSkew = TimeSpan.FromSeconds(options.JwtClockSkewSeconds),
        };

        if (StaticKeys.Count == 1 && JwksAddress is null)
        {
            parameters.IssuerSigningKey = StaticKeys[0];
        }

        JwksKeyCache? jwks = Jwks;

        if (jwks is not null)
        {
            parameters.IssuerSigningKeyResolver = (token, securityToken, keyId, validationParameters) =>
            {
                IReadOnlyList<SecurityKey> fetched = jwks.Keys;

                if (!string.IsNullOrEmpty(keyId) && !fetched.Any(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal)))
                {
                    jwks.RequestRefresh();
                }

                return [.. StaticKeys, .. fetched];
            };
        }

        return parameters;
    }

    public void Configure(JwtBearerOptions bearer, TelemetryEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(bearer);

        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = ValidationParameters(options);
    }
}
