namespace Semantico.Core.Authorization;

/// <summary>
/// Standard claim types used by Semantico.
/// External systems should map their claims to these types.
/// </summary>
public static class SemanticoClaims
{
    public const string UserId = "semantico:user_id";
    public const string UserName = "semantico:user_name";
    public const string Role = "semantico:role";
    public const string Permission = "semantico:permission";
}
