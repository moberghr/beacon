namespace Beacon.Core.Services.Embed;

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

public record EmbedTokenValidation(EmbedTokenValidationResult Result, EmbedTokenClaims? Claims);
