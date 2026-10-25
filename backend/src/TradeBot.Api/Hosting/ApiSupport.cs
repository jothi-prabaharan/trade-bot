using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TradeBot.Application.Serialization;
using TradeBot.Application.Validation;
using TradeBot.Domain.Common;

namespace TradeBot.Api.Hosting;

public sealed class AuthOptions
{
    public string AdminUser { get; set; } = "admin";
    /// <summary>bcrypt hash; generate with: dotnet TradeBot.Api.dll hash-password</summary>
    public string? AdminPasswordHash { get; set; }
    /// <summary>base64 HMAC-SHA256 key, at least 32 bytes.</summary>
    public string? JwtKey { get; set; }
    public int TokenHours { get; set; } = 8;
    public string Issuer { get; set; } = "tradebot";

    public SymmetricSecurityKey SigningKey() =>
        new(Convert.FromBase64String(JwtKey ?? throw new InvalidOperationException("Auth:JwtKey is not configured.")));
}

public sealed class JwtIssuer(AuthOptions options, TimeProvider clock)
{
    public (string Token, DateTime ExpiresUtc) Issue(string user)
    {
        var expires = clock.GetUtcNow().UtcDateTime.AddHours(options.TokenHours);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Issuer,
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.Name, user), new Claim(ClaimTypes.Role, "admin")]),
            Expires = expires,
            SigningCredentials = new SigningCredentials(options.SigningKey(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}

public static class ApiJson
{
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        o.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        o.Converters.Add(new DbValueEnumJsonConverterFactory());
        // Secrets never leave the API, whatever entity graph a response happens to contain
        o.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipEncrypted } };
    }

    private static void SkipEncrypted(JsonTypeInfo info)
    {
        foreach (var p in info.Properties)
            if (p.AttributeProvider?.IsDefined(typeof(EncryptedAttribute), inherit: true) == true)
                p.ShouldSerialize = static (_, _) => false;
    }

    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(o);
        return o;
    }
}

public sealed record ApiError(string Code, string Message, string? Field = null);

public static class ApiResults
{
    public static ObjectResult Invalid(IEnumerable<RuleViolation> violations) =>
        new BadRequestObjectResult(new { errors = violations.Select(v => new ApiError(v.Code, v.Message, v.Field)) });

    public static ObjectResult Invalid(string code, string message, string? field = null) =>
        new BadRequestObjectResult(new { errors = new[] { new ApiError(code, message, field) } });
}

/// <summary>Maps DB constraint and concurrency failures to 400/409 so clients see the rule that fired.</summary>
public static class ErrorMapping
{
    public static void UseTradeBotErrors(this WebApplication app) => app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
    {
        var e = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, error) = e switch
        {
            DbUpdateConcurrencyException => (409, new ApiError("CONCURRENCY_CONFLICT", "The record changed since it was loaded; reload and retry.")),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg } =>
                (409, new ApiError("DB_UNIQUE", $"Duplicate value violates {pg.ConstraintName}.", pg.ConstraintName)),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation } pg } =>
                (400, new ApiError("DB_CHECK", $"Database rule {pg.ConstraintName} rejected the change.", pg.ConstraintName)),
            JsonException or BadHttpRequestException => (400, new ApiError("BAD_REQUEST", e!.Message)),
            KeyNotFoundException => (404, new ApiError("NOT_FOUND", e!.Message)),
            _ => (500, new ApiError("SERVER_ERROR", "Unexpected error; see server logs.")),
        };
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { errors = new[] { error } }, ApiJson.Options);
    }));
}
