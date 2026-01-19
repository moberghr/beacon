namespace Semantico.UI.Authentication;

public interface ISemanticoAuthorizationProvider
{
    Task<bool> HasReadPermissionAsync(string username);
    Task<bool> HasWritePermissionAsync(string username);
}
