namespace Beacon.Core.Authorization;

/// <summary>
/// A list or aggregate request whose handler keeps only what the caller's project access allows, in the query it sends
/// to the database (never after reading). A filter id it carries (such as <c>?dataSourceId=</c>) narrows the list and
/// grants nothing, so such a property carries <see cref="ProjectScopeExemptAttribute"/> with that reason.
/// </summary>
public interface IProjectFilteredRequest;
