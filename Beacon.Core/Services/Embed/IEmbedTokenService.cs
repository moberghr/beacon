namespace Beacon.Core.Services.Embed;

public interface IEmbedTokenService
{
    EmbedTokenResult Mint(EmbedTokenMintRequest request);

    EmbedTokenValidation Validate(string token);
}
