using Semantico.UI.AspNet.Authentication;

namespace Semantico.SampleProject.Services;

public class SampleAuthorizationProvider : ISemanticoAuthorizationProvider
{
    public Task<bool> HasReadPermissionAsync(string username)
    {
        // Implement your logic here
        // For example, check database, external service, etc.
        return Task.FromResult(true);
    }

    public Task<bool> HasWritePermissionAsync(string username)
    {
        // Implement your logic here
        // For example, admin user has write permission
        return Task.FromResult(username == "admin");
    }
}
