using Microsoft.AspNetCore.Mvc;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class NotificationsController : ControllerBase
{
    [HttpGet]
    public async Task GetNotifications()
    {
    }

    [HttpPost]
    public async Task CreateNotification()
    {
    }

    [HttpPut]
    public async Task UpdateNotification()
    {
    }

    [HttpDelete]
    public async Task DeleteNotificationy()
    {
    }
}