using Microsoft.AspNetCore.Components;
using MudBlazor;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Anomaly;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services;
using Beacon.UI.Components.Shared;

namespace Beacon.UI.Components.Pages.Subscriptions;

public partial class AddSubscription
{
    [Parameter]
    public int QueryId { get; set; }

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    private ISubscriptionService Service { get; set; } = null!;

    [Inject]
    private IRecipientService RecipientService { get; set; } = null!;

    [Inject]
    private IQueryService QueryService { get; set; } = null!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    private SubscriptionData Subscription { get; set; } = new();
    private List<RecipientData> _recipients { get; set; } = [];
    private IReadOnlyCollection<string> _selectedRecipientIds { get; set; } = [];
    private string FileAttachmentType { get; set; } = string.Empty;
    private List<QueryStepParameterData> _queryParameters { get; set; } = [];
    private Dictionary<string, string> _parameterValues { get; set; } = new();

    // Anomaly detection fields
    private bool _anomalyDetectionEnabled = false;
    private AnomalyDetectionMethod _anomalyMethod = AnomalyDetectionMethod.StandardDeviation;
    private AnomalySensitivity _anomalySensitivity = AnomalySensitivity.Medium;
    private int _anomalyLookbackDays = 30;
    private int _anomalyMinDataPoints = 7;
    private bool _anomalyAlertOnIncrease = true;
    private bool _anomalyAlertOnDecrease = true;

    private MudForm _form = null!;
    private bool _loading;
    private bool _isFormValid = false;
    private string[] errors = { };

    protected override async Task OnInitializedAsync()
    {
        _loading = true;

        try
        {
            // Set QueryId from route parameter
            Subscription.QueryId = QueryId;

            // Load recipients
            _recipients = await RecipientService.GetRecipients(null, null, default);

            // Fetch query parameters
            if (Subscription.QueryId > 0)
            {
                var queryDetails = await QueryService.GetQueryDetails(Subscription.QueryId, default);

                // Set query name for display
                Subscription.QueryName = queryDetails.Name;

                // Extract all parameters from all steps
                _queryParameters = queryDetails.Steps
                    .SelectMany(step => step.Parameters)
                    .ToList();

                // Initialize parameter values dictionary with empty strings
                foreach (var param in _queryParameters)
                {
                    if (!string.IsNullOrEmpty(param.Placeholder))
                    {
                        _parameterValues[param.Placeholder] = string.Empty;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error loading subscription form: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task Submit()
    {
        if (_loading)
        {
            return;
        }

        await _form.Validate();

        if (!_form.IsValid)
        {
            Snackbar.Add("Please fix validation errors before submitting", Severity.Warning);
            return;
        }

        _loading = true;

        try
        {
            Subscription.Recipients = _recipients
                .Where(x => _selectedRecipientIds.Contains(x.RecipientId.ToString()))
                .ToList();

            if (!string.IsNullOrWhiteSpace(FileAttachmentType))
            {
                Subscription.ResultAttachmentType = Enum.Parse<FileType>(FileAttachmentType, ignoreCase: true);
            }

            // Populate subscription parameters (only include parameters with values)
            Subscription.Parameters = _parameterValues
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                .Select(kvp => new SubscriptionParameterData
                {
                    QueryPlaceholder = kvp.Key,
                    Value = kvp.Value
                })
                .ToList();

            // Populate anomaly configuration if enabled
            if (_anomalyDetectionEnabled)
            {
                Subscription.AnomalyConfig = new AnomalyConfigData
                {
                    SubscriptionId = Subscription.SubscriptionId ?? 0,
                    Enabled = true,
                    DetectionMethod = _anomalyMethod,
                    Sensitivity = _anomalySensitivity,
                    LookbackDays = _anomalyLookbackDays,
                    AlertOnIncrease = _anomalyAlertOnIncrease,
                    AlertOnDecrease = _anomalyAlertOnDecrease,
                    MinimumDataPoints = _anomalyMinDataPoints
                };
            }

            var response = await Service.CreateSubscription(Subscription, CancellationToken.None);

            if (response.Success)
            {
                Snackbar.Add("Subscription created successfully", Severity.Success);
                NavigationManager.NavigateTo("subscriptions");
            }
            else
            {
                Snackbar.Add(response.Message, Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error creating subscription: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Cancel()
    {
        NavigationManager.NavigateTo("subscriptions");
    }

    private string GetRecipientIcon(NotificationType notificationType)
    {
        return notificationType switch
        {
            NotificationType.Email => Icons.Material.Filled.Email,
            NotificationType.Teams => Icons.Material.Filled.Groups,
            NotificationType.Jira => Icons.Material.Filled.BugReport,
            NotificationType.Slack => Icons.Material.Filled.Chat,
            _ => Icons.Material.Filled.Notifications
        };
    }

    private string GetDetectionMethodDescription()
    {
        return _anomalyMethod switch
        {
            AnomalyDetectionMethod.StandardDeviation =>
                "Statistical approach using mean and standard deviation. Identifies values that fall outside the expected range " +
                "based on historical patterns. Best for normally distributed data with consistent variance.",

            AnomalyDetectionMethod.IQR =>
                "Uses the middle 50% of data (between Q1 and Q3) to define normal range. More resistant to extreme outliers. " +
                "Best for data with occasional spikes that should be excluded from the baseline calculation.",

            AnomalyDetectionMethod.PercentageChange =>
                "Compares current value to historical average as a percentage change. Simple and intuitive approach. " +
                "Best when relative change matters more than absolute deviation (e.g., sales metrics, user counts).",

            _ => "Statistical method for detecting unusual patterns in your query results."
        };
    }

    private string GetSensitivityDescription()
    {
        return _anomalySensitivity switch
        {
            AnomalySensitivity.High when _anomalyMethod == AnomalyDetectionMethod.StandardDeviation =>
                "1.5 standard deviations - Detects smaller deviations early. ~13% of normal variations may trigger alerts. " +
                "Recommended for critical queries where immediate action is needed.",

            AnomalySensitivity.Medium when _anomalyMethod == AnomalyDetectionMethod.StandardDeviation =>
                "2.0 standard deviations - Balanced approach. ~5% of normal variations may trigger alerts. " +
                "Recommended for most use cases - good balance between detection and false positives.",

            AnomalySensitivity.Low when _anomalyMethod == AnomalyDetectionMethod.StandardDeviation =>
                "3.0 standard deviations - Only significant deviations trigger alerts. ~0.3% false positive rate. " +
                "Use when you want high confidence and minimal notification noise.",

            AnomalySensitivity.High when _anomalyMethod == AnomalyDetectionMethod.PercentageChange =>
                "15% change threshold - Sensitive to smaller fluctuations. Catches changes early but may increase noise.",

            AnomalySensitivity.Medium when _anomalyMethod == AnomalyDetectionMethod.PercentageChange =>
                "25% change threshold - Balanced sensitivity. Catches significant changes while filtering normal variations.",

            AnomalySensitivity.Low when _anomalyMethod == AnomalyDetectionMethod.PercentageChange =>
                "40% change threshold - Only major changes trigger alerts. Minimal false positives, but may miss gradual increases.",

            _ => "Controls how much deviation is needed to trigger an alert. Higher sensitivity = more alerts, lower sensitivity = fewer false positives."
        };
    }

    private string GetExampleScenario()
    {
        var direction = (_anomalyAlertOnIncrease, _anomalyAlertOnDecrease) switch
        {
            (true, true) => "increases or decreases significantly",
            (true, false) => "spikes above normal",
            (false, true) => "drops below normal",
            _ => "changes unexpectedly"
        };

        return _anomalyMethod switch
        {
            AnomalyDetectionMethod.StandardDeviation when _anomalySensitivity == AnomalySensitivity.High =>
                $"If your query typically returns 100 rows (±20), you'll be alerted when it {direction} - e.g., 130+ or 70- rows (1.5σ threshold).",

            AnomalyDetectionMethod.StandardDeviation when _anomalySensitivity == AnomalySensitivity.Medium =>
                $"If your query typically returns 100 rows (±20), you'll be alerted when it {direction} - e.g., 140+ or 60- rows (2.0σ threshold).",

            AnomalyDetectionMethod.StandardDeviation when _anomalySensitivity == AnomalySensitivity.Low =>
                $"If your query typically returns 100 rows (±20), you'll be alerted when it {direction} - e.g., 160+ or 40- rows (3.0σ threshold).",

            AnomalyDetectionMethod.PercentageChange when _anomalySensitivity == AnomalySensitivity.High =>
                $"If your query typically returns 100 rows, you'll be alerted when it {direction} by ≥15% - e.g., 115+ or 85- rows.",

            AnomalyDetectionMethod.PercentageChange when _anomalySensitivity == AnomalySensitivity.Medium =>
                $"If your query typically returns 100 rows, you'll be alerted when it {direction} by ≥25% - e.g., 125+ or 75- rows.",

            AnomalyDetectionMethod.PercentageChange when _anomalySensitivity == AnomalySensitivity.Low =>
                $"If your query typically returns 100 rows, you'll be alerted when it {direction} by ≥40% - e.g., 140+ or 60- rows.",

            AnomalyDetectionMethod.IQR =>
                $"You'll be alerted when results fall outside the interquartile range - the system will identify what's 'normal' based on your data distribution.",

            _ => $"The system will learn your baseline and alert you when results {direction}."
        };
    }

    private string? ValidateNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Value is required";
        }

        if (!double.TryParse(value, out _))
        {
            return "Value must be a valid number";
        }

        return null;
    }

    private string? ValidateDateTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Value is required";
        }

        if (!DateTime.TryParse(value, out _))
        {
            return "Value must be a valid date/time (e.g., 2024-01-01 12:00:00)";
        }

        return null;
    }
}
