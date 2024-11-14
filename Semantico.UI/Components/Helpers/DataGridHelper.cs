using MudBlazor;
using Semantico.Core.Helpers;
using SortDirection = Semantico.Core.Helpers.SortDirection;

namespace Semantico.UI.Components;

public static class DataGridHelper
{
    public static SortedListRequest BuildSortedRequest<TData>(GridState<TData> state)
    {
        var sortDefinition = state.SortDefinitions.FirstOrDefault();
        var sortCriteria = sortDefinition is not null
            ? new SortCriterion(sortDefinition.SortBy, sortDefinition.Descending ? SortDirection.Descending : SortDirection.Ascending)
            : null;

        return new SortedListRequest
        {
            Page = state.Page,
            PageSize = state.PageSize,
            SortCriteria = sortCriteria is null 
                ? new List<SortCriterion>()
                : new List<SortCriterion> { sortCriteria }
        };
    }
}
