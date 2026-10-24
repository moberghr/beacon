namespace Beacon.Core.Services.Embed;

[Obsolete("Not used by Beacon's embed routes; removed in 6.0")]
public interface IEmbedTokenService
{
    EmbedTokenResult Mint(EmbedTokenMintRequest request);

    EmbedTokenValidation Validate(string token);
}
