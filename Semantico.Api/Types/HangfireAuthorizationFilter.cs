using HangfireBasicAuthenticationFilter;

namespace Semantico.Api.Types;

public class HangfireAuthorizationFilter : HangfireCustomBasicAuthenticationFilter
{
    public HangfireAuthorizationFilter()
    {
        User = "neki-user";
        Pass = "neki-password";
    }
}
