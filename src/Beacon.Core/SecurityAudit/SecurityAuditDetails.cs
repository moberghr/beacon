using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// The details of a security audit event: identifiers, counts, flags, times and short codes, never free text. A string is
/// kept only when it is a code (letters, digits, <c>_ . : -</c>, at most 64 characters) and its key does not name text
/// (a name, an e-mail address, SQL, a question, a comment, a description, a destination, a token, a secret, a password,
/// a header, a body, an address, an agent, a subject or an external id); any other string is replaced by
/// <see cref="WithheldValue"/>, so a mistake at an event site cannot store personal data or a secret. Id lists keep their
/// first <see cref="MaxListLength"/> entries and record their length; the whole stays under <see cref="MaxJsonLength"/>
/// characters: when it would not, every single value is kept and the lists are shortened, the longest first, each one's
/// full length recorded as <c>&lt;key&gt;Count</c>, and the details are marked <c>"truncated":true</c>.
/// </summary>
public sealed partial class SecurityAuditDetails
{
    /// <summary>What a string that is not a code, or that sits under a key naming text, is stored as.</summary>
    public const string WithheldValue = "[withheld]";

    public const int MaxListLength = 50;

    public const int MaxJsonLength = 2000;

    private static readonly string[] TextKeyFragments =
    [
        "name", "email", "mail", "sql", "question", "comment", "note", "description", "text", "message", "destination",
        "url", "token", "secret", "password", "header", "body", "address", "agent", "subject", "externalid", "login"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly JsonObject _values = new();
    private readonly List<string> _withheld = [];

    /// <summary>The keys whose string value was withheld.</summary>
    public IReadOnlyList<string> Withheld => _withheld;

    public SecurityAuditDetails Add(string key, int? value)
    {
        _values[Checked(key)] = value;

        return this;
    }

    public SecurityAuditDetails Add(string key, long? value)
    {
        _values[Checked(key)] = value;

        return this;
    }

    public SecurityAuditDetails Add(string key, bool? value)
    {
        _values[Checked(key)] = value;

        return this;
    }

    /// <summary>A time, as UTC to the second.</summary>
    public SecurityAuditDetails Add(string key, DateTime? value)
    {
        _values[Checked(key)] = value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        return this;
    }

    /// <summary>An enum value, by name.</summary>
    public SecurityAuditDetails Add<TEnum>(string key, TEnum value) where TEnum : struct, Enum
    {
        return AddCode(key, value.ToString());
    }

    /// <summary>Ids, in order, distinct; the first <see cref="MaxListLength"/> are kept and the count is recorded.</summary>
    public SecurityAuditDetails Add(string key, IEnumerable<int>? ids)
    {
        var checkedKey = Checked(key);
        if (ids == null)
        {
            _values[checkedKey] = null;

            return this;
        }

        var distinct = ids
            .Distinct()
            .ToList();
        var array = new JsonArray();
        foreach (var id in distinct.Take(MaxListLength))
        {
            array.Add(id);
        }

        _values[checkedKey] = array;
        if (distinct.Count > MaxListLength)
        {
            _values[checkedKey + "Count"] = distinct.Count;
        }

        return this;
    }

    /// <summary>A short code (a reason, a kind, a permission name). Anything else is withheld.</summary>
    public SecurityAuditDetails AddCode(string key, string? code)
    {
        var checkedKey = Checked(key);
        if (code == null)
        {
            _values[checkedKey] = null;

            return this;
        }

        if (NamesText(checkedKey) || !CodePattern().IsMatch(code))
        {
            _values[checkedKey] = WithheldValue;
            _withheld.Add(checkedKey);

            return this;
        }

        _values[checkedKey] = code;

        return this;
    }

    /// <summary>
    /// Short codes (setting keys, kinds), in order, distinct: each one that is not a code is withheld on its own, and the
    /// first <see cref="MaxListLength"/> are kept with the count recorded, as for ids.
    /// </summary>
    public SecurityAuditDetails AddCodes(string key, IEnumerable<string>? codes)
    {
        var checkedKey = Checked(key);
        if (codes == null)
        {
            _values[checkedKey] = null;

            return this;
        }

        var distinct = codes
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var namesText = NamesText(checkedKey);
        var array = new JsonArray();
        foreach (var code in distinct.Take(MaxListLength))
        {
            if (namesText || !CodePattern().IsMatch(code))
            {
                array.Add(WithheldValue);
                if (!_withheld.Contains(checkedKey))
                {
                    _withheld.Add(checkedKey);
                }

                continue;
            }

            array.Add(code);
        }

        _values[checkedKey] = array;
        if (distinct.Count > MaxListLength)
        {
            _values[checkedKey + "Count"] = distinct.Count;
        }

        return this;
    }

    /// <summary>
    /// Keyed digests (lowercase hex, from <see cref="SecurityAuditIntegrity.Digest"/>), in the given order and not made
    /// distinct, so they stay aligned with another list of the same event (the keys of a settings save, say).
    /// </summary>
    internal SecurityAuditDetails AddDigests(string key, IEnumerable<string> digests)
    {
        var array = new JsonArray();
        foreach (var digest in digests)
        {
            array.Add(DigestPattern().IsMatch(digest) ? digest : WithheldValue);
        }

        _values[Checked(key)] = array;

        return this;
    }

    /// <summary>The details as JSON; null when there are none. Bounded by <see cref="MaxJsonLength"/> (see the class).</summary>
    public string? ToJson()
    {
        if (_values.Count == 0)
        {
            return null;
        }

        var json = _values.ToJsonString(JsonOptions);

        return json.Length <= MaxJsonLength ? json : Shortened();
    }

    // Every single value kept; the lists shortened, longest first, until the whole fits; their full lengths recorded.
    private string Shortened()
    {
        var values = (JsonObject)_values.DeepClone();
        values["truncated"] = true;
        var json = values.ToJsonString(JsonOptions);
        while (json.Length > MaxJsonLength)
        {
            var longest = values
                .Where(x => x.Value is JsonArray { Count: > 0 })
                .OrderByDescending(x => x.Value!.ToJsonString(JsonOptions).Length)
                .Select(x => x.Key)
                .FirstOrDefault();
            if (longest == null)
            {
                // Single values alone do not fit (they never should: keys and codes are short): their number only.
                return new JsonObject { ["truncated"] = true, ["keys"] = _values.Count }.ToJsonString(JsonOptions);
            }

            var list = (JsonArray)values[longest]!;
            var countKey = longest + "Count";
            if (values[countKey] is not JsonValue)
            {
                values[countKey] = list.Count;
            }

            var keep = list.Count / 2;
            while (list.Count > keep)
            {
                list.RemoveAt(list.Count - 1);
            }

            json = values.ToJsonString(JsonOptions);
        }

        return json;
    }

    private static bool NamesText(string key)
    {
        var lower = key.ToLowerInvariant();

        return TextKeyFragments.Any(lower.Contains);
    }

    private static string Checked(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("A security audit detail key is a camelCase identifier of at most 40 characters.", nameof(key));
        }

        return key;
    }

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]{0,39}\z", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,64}\z", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[0-9a-f]{16,64}\z", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex DigestPattern();
}
