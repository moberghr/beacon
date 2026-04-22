using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Beacon.UI.Components.Shared;

public class BasePageComponent: ComponentBase
{
    [Inject]
    protected NavigationManager NavManager { get; set; }

    [Inject]
    protected PageHistoryState PageState { get; set; }

    [Inject]
    protected IBrowserViewportService BrowserViewport { get; set; }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        PageState.AddPageToHistory(NavManager.Uri);
    }

    /// <summary>
    /// Creates responsive DialogOptions that go fullscreen on mobile (Xs breakpoint).
    /// </summary>
    protected async Task<DialogOptions> CreateResponsiveDialogOptions(MaxWidth maxWidth = MaxWidth.Small)
    {
        var breakpoint = await BrowserViewport.GetCurrentBreakpointAsync();
        return new DialogOptions
        {
            MaxWidth = maxWidth,
            FullWidth = true,
            FullScreen = breakpoint == Breakpoint.Xs,
        };
    }
}

public class PageHistoryState
{
    private readonly Stack<string> _previousPages = new();

    public void AddPageToHistory(string pageName)
    {
        _previousPages.Push(pageName);
    }

    public bool CanGoBack() => _previousPages.Count > 1;
}
