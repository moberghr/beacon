using HangfireBasicAuthenticationFilter;

namespace Semantico.Api.Hangfire;

public class HangfireAuthorizationFilter : HangfireCustomBasicAuthenticationFilter
{
    public HangfireAuthorizationFilter()
    {
        User = "neki-user";
        Pass = "neki-password";
    }
}
