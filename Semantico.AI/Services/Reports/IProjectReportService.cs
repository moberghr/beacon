using Semantico.Core.Data.Enums;

namespace Semantico.AI.Services.Reports;

public interface IProjectReportService
{
    Task<int> GenerateReportAsync(int projectId, ReportFormat format = ReportFormat.Markdown, ReportType type = ReportType.Full, CancellationToken ct = default);
    Task<string?> GetReportContentAsync(int reportId, CancellationToken ct = default);
}
