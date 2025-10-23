namespace Semantico.UI.AspNet.Authentication;

public interface ISemanticoAuthorizationProvider
{
    Task<bool> HasReadPermissionAsync(string username);
    Task<bool> HasWritePermissionAsync(string username);
}
