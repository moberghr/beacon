using MudBlazor;
using Beacon.Core.Helpers;
using SortDirection = Beacon.Core.Helpers.SortDirection;

namespace Beacon.UI.Components;

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
                ? []
                : [sortCriteria]
        };
    }
    
    public static T BuildSortedRequest<T, TData>(GridState<TData> state) where T : SortedListRequest, new()
    {
        var sortDefinition = state.SortDefinitions.FirstOrDefault();
        var sortCriteria = sortDefinition is not null
            ? new SortCriterion(sortDefinition.SortBy, sortDefinition.Descending ? SortDirection.Descending : SortDirection.Ascending)
            : null;

        return new T
        {
            Page = state.Page,
            PageSize = state.PageSize,
            SortCriteria = sortCriteria is null 
                ? []
                : [sortCriteria]
        };
    }
}
