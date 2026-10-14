using System.Text.RegularExpressions;
using Beacon.Core.HostData;

namespace Beacon.Core.Services.Security;

/// <summary>
/// The columns whose values Beacon never reads to describe them — sample values (when collected and when shown), value
/// grounding and documentation enum discovery — and the values it drops when it does read them.
/// <list type="bullet">
/// <item>A column is left out by its name: a PII name (<see cref="IQueryGuardrailService.IsPiiColumn"/>, built-in or custom
/// pattern), a secret-like name (<see cref="SecretLikeColumnNames.IsSecretLikeForValues"/>), the name of a person's name
/// (first, last, middle, given, family, full or surname, customer_name; Croatian ime, prezime) or of Croatian personal data
/// (adresa, telefon, mobitel, placa, stanje, broj_racuna), or the name of a personal financial amount (salary, wage,
/// balance, income — not a balance or income type, category, code or id). Financial amounts are left out here only: query
/// results still return them, as they are what a query aggregates.</item>
/// <item>A set of values is dropped when one looks like PII (<see cref="PiiValueScreen"/>) or like a credential
/// (<see cref="CredentialValueScreen"/>).</item>
/// </list>
/// </summary>
public static partial class ColumnValueExclusions
{
    // Matched on the name in snake_case: a balance or income followed by type, category, code or id names a kind of amount,
    // not one.
    [GeneratedRegex(@"salar(y|ies)|wage|(balance|income)(?!_?(type|category|code|id)(_|$))", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex FinancialAmountName();

    // Matched on the name in snake_case (camelCase split, lower case), segment by segment.
    [GeneratedRegex(
        @"(^|_)((first|last|middle|given|family|full|sur)_?name|customer_?name|ime|prezime|adresa|telefon|mobitel|placa|plaća|stanje|broj_?racuna|broj_?računa)(_|$)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex PersonalDataName();

    [GeneratedRegex(@"(?<=\p{Ll}|\p{Nd})(?=\p{Lu})|[\s\-]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex WordBoundary();

    /// <summary>True when the values of <paramref name="columnName"/> are never read to describe it.</summary>
    public static bool IsExcludedColumn(IQueryGuardrailService guardrail, string columnName, IReadOnlyList<string>? customPatterns)
    {
        return guardrail.IsPiiColumn(columnName, customPatterns)
            || SecretLikeColumnNames.IsSecretLikeForValues(columnName)
            || IsPersonalData(columnName)
            || IsFinancialAmount(columnName);
    }

    /// <summary>True when any of <paramref name="values"/> looks like PII or like a credential.</summary>
    public static bool ContainsExcludedValue(IReadOnlyList<string> values, IReadOnlyList<string>? customPatterns)
    {
        return PiiValueScreen.ContainsPiiValue(values, customPatterns) || CredentialValueScreen.ContainsCredential(values);
    }

    // A check that times out counts as a match (fail closed): the column only loses its examples.
    private static bool IsPersonalData(string columnName)
    {
        try
        {
            return PersonalDataName().IsMatch(SnakeCase(columnName));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static bool IsFinancialAmount(string columnName)
    {
        try
        {
            return FinancialAmountName().IsMatch(SnakeCase(columnName));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    // CustomerFirstName, customer-first-name and customer first name all read as customer_first_name.
    private static string SnakeCase(string columnName)
    {
        return WordBoundary().Replace(columnName, "_").ToLowerInvariant();
    }
}
