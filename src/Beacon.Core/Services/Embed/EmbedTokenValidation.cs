namespace Beacon.Core.Services.Embed;

[Obsolete("Not used by Beacon's embed routes; removed in 6.0")]
public enum EmbedTokenValidationResult
{
    Valid,
    Expired,
    InvalidSignature,
    WrongAudience,
    Malformed,
    MissingClaims,
    NoAllowedOrigins
}

[Obsolete("Not used by Beacon's embed routes; removed in 6.0")]
public record EmbedTokenValidation(EmbedTokenValidationResult Result, EmbedTokenClaims? Claims);
