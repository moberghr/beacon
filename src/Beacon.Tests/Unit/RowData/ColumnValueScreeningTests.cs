using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// What Beacon never reads, or drops, when it describes a column by its values (sample values, value grounding, enum
/// discovery): secret-like names matched on name segments both ways, credential-shaped values, banking identifiers by
/// their check digits, banking PII names, financial amounts, and the stored-sample screen that applies them all at read
/// time. The host hard-exclude keeps its own, narrower list.
/// </summary>
[TestFixture]
public class ColumnValueScreeningTests
{
    [TestCase("api_key")]
    [TestCase("ApiKey")]
    [TestCase("apikey")]
    [TestCase("key")]
    [TestCase("KEY")]
    [TestCase("signing_key")]
    [TestCase("encryption_key")]
    [TestCase("EncryptionKey")]
    [TestCase("MASTER_KEY")]
    [TestCase("license_key")]
    [TestCase("private_key_pem")]
    [TestCase("AccessKey")]
    [TestCase("client_credential")]
    [TestCase("Credentials")]
    [TestCase("session_id")]
    [TestCase("SessionId")]
    [TestCase("sessionid")]
    [TestCase("auth_cookie")]
    [TestCase("request_signature")]
    [TestCase("SignatureValue")]
    [TestCase("signature")]
    [TestCase("refresh_token")]
    [TestCase("client_secret")]
    [TestCase("password_hash")]
    [TestCase("user_pwd")]
    [TestCase("pin_offset")]
    [TestCase("PIN")]
    [TestCase("otp_seed")]
    [TestCase("totp_secret")]
    [TestCase("Salt")]
    [TestCase("row_hash")]
    [TestCase("passcode")]
    [TestCase("passphrase")]
    [TestCase("db_connection_string")]
    [TestCase("ConnectionString")]
    [TestCase("dsn")]
    [TestCase("sentry_dsn")]
    [TestCase("webhook_url")]
    [TestCase("bearer")]
    [TestCase("auth_bearer")]
    [TestCase("jwt")]
    [TestCase("refresh_jwt")]
    [TestCase("hmac_key")]
    [TestCase("HmacKey")]
    [TestCase("hmac_secret")]
    [TestCase("HMAC_SECRET")]
    [TestCase("hmac")]
    [TestCase("request_hmac")]
    [TestCase("aes_key")]
    [TestCase("AesKey")]
    [TestCase("aes256_key")]
    [TestCase("rsa_key")]
    [TestCase("RsaPrivateKey")]
    [TestCase("pgp_key")]
    [TestCase("GpgKey")]
    [TestCase("ssh_key")]
    [TestCase("SshHostKey")]
    [TestCase("ClientSecret")]
    [TestCase("oauth_client_secret")]
    [TestCase("consumer_secret")]
    public void SecretLikeForValues_MatchesSecretNames(string name)
    {
        SecretLikeColumnNames.IsSecretLikeForValues(name).Should().BeTrue();
    }

    [TestCase("monkey")]
    [TestCase("turnkey_id")]
    [TestCase("keyboard_layout")]
    [TestCase("pinned")]
    [TestCase("is_pinned")]
    [TestCase("hashtag")]
    [TestCase("Opinion")]
    [TestCase("Shipping")]
    [TestCase("status")]
    [TestCase("otpremnica")]
    [TestCase("saltwater_region")]
    [TestCase("privateer")]
    [TestCase("sessions_total")]
    [TestCase("ProductKey")]
    [TestCase("CurrencyKey")]
    [TestCase("customer_key")]
    [TestCase("date_key")]
    [TestCase("private_note")]
    [TestCase("IsPrivate")]
    [TestCase("signatory")]
    [TestCase("dsnap")]
    [TestCase("webhook_count")]
    [TestCase("ClientKey")]
    [TestCase("client_key")]
    [TestCase("rsa")]
    [TestCase("aes_mode")]
    [TestCase("ssh_port")]
    [TestCase("Aesthetic")]
    [TestCase("AestheticKey")]
    [TestCase("Versailles")]
    [TestCase("hmacs_total")]
    [TestCase("")]
    [TestCase(null)]
    public void SecretLikeForValues_LeavesOrdinaryNames(string? name)
    {
        SecretLikeColumnNames.IsSecretLikeForValues(name).Should().BeFalse();
    }

    // The host hard-exclude hides a column from the MCP surface: a false positive costs a column, so it keeps its list.
    [TestCase("session_id")]
    [TestCase("request_signature")]
    [TestCase("dsn")]
    [TestCase("cookie_value")]
    public void TheHostHardExclude_KeepsItsNarrowerList(string name)
    {
        SecretLikeColumnNames.IsSecretLike(name).Should().BeFalse();
        SecretLikeColumnNames.IsSecretLikeForValues(name).Should().BeTrue();
    }

    [TestCase("-----BEGIN RSA PRIVATE KEY-----MIIEowIBAAKCAQEA")]
    [TestCase("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.sig")]
    [TestCase(TestCredentials.ProviderSecretKey)]
    [TestCase("sk-ant-api03-AbCdEfGhIjKlMnOpQrStUv")]
    [TestCase("AKIAIOSFODNN7EXAMPLE")]
    [TestCase("ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8")]
    [TestCase("xoxb-1234567890-abcdefghij")]
    [TestCase("ATATT3xFfGF0abcdefghijklmnop")]
    [TestCase("AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p")]
    [TestCase("glpat-AbCdEfGhIjKlMnOpQrSt")]
    [TestCase("$2b$12$KIXQJbH4l2Zf1Q9e8a7b6uR0t1Y2u3I4o5P6a7S8d9F0g1H2j3K4l")]
    [TestCase("$argon2id$v=19$m=65536,t=3,p=4$c29tZXNhbHQ")]
    [TestCase("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [TestCase("Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFiQ2RFZkdo")]
    [TestCase("pwd=hunter2")]
    [TestCase("password: Tr0ub4dor")]
    [TestCase("note: secret=abc then more text")]
    [TestCase("api_key=abc123")]
    [TestCase("API-KEY: x")]
    [TestCase("token : 1")]
    [TestCase("postgres://app:S3cret@db.internal:5432/core")]
    [TestCase("see https://user:pass@example.test/path")]
    public void TheCredentialScreen_FindsKeysTokensAndHashes(string value)
    {
        CredentialValueScreen.LooksLikeCredential(value).Should().BeTrue();
    }

    [TestCase("PAID")]
    [TestCase("Order shipped on time")]
    [TestCase("2024-05-01T10:00:00.0000000Z")]
    [TestCase("550e8400-e29b-41d4-a716-446655440000")]
    [TestCase("Pneumonoultramicroscopicsilicovolcanoconiosis")]
    [TestCase("sk_short")]
    [TestCase("ACCOUNTSPAYABLEDEPARTMENTZAGREBHQ")]
    [TestCase("best-selling-Product-2024-Edition-XL-Blue")]
    [TestCase("Customer_Reference_2024_Q3_Region_EU_West")]
    [TestCase("/var/data/Archive2024/Customers/ExportFiles")]
    [TestCase("6F9619FF8B86D011B42D00C04FC964FF")]
    [TestCase("d41d8cd98f00b204e9800998ecf8427e")]
    [TestCase("customerAccountReferenceId")]
    [TestCase("password reset requested")]
    [TestCase("tokens issued: none")]
    [TestCase("https://example.test/path?x=1")]
    [TestCase(null)]
    public void TheCredentialScreen_LeavesOrdinaryValues(string? value)
    {
        CredentialValueScreen.LooksLikeCredential(value).Should().BeFalse();
    }

    [TestCase("HR1210010051863000160", true)]
    [TestCase("HR12 1001 0051 8630 0016 0", true)]
    [TestCase("DE89370400440532013000", true)]
    [TestCase("GB82WEST12345698765432", true)]
    [TestCase("HR1310010051863000160", false)]
    [TestCase("GB83WEST12345698765432", false)]
    public void Iban_IsRecognisedByItsCheckDigits(string value, bool valid)
    {
        BankingIdentifiers.IsIban(value).Should().Be(valid);
    }

    [TestCase("69435151530", true)]
    [TestCase("12345678903", true)]
    [TestCase("12345678904", false)]
    [TestCase("69435151531", false)]
    [TestCase("1234567890", false)]
    public void Oib_IsRecognisedByItsIso7064CheckDigit(string value, bool valid)
    {
        BankingIdentifiers.IsOib(value).Should().Be(valid);
    }

    [TestCase("0101990500003", true)]
    [TestCase("0101990500004", false)]
    [TestCase("3213990500003", false)]
    [TestCase("010199050000", false)]
    public void Jmbg_IsRecognisedByItsCheckDigitAndDate(string value, bool valid)
    {
        BankingIdentifiers.IsJmbg(value).Should().Be(valid);
    }

    // Within free text: an IBAN followed by words, an OIB or a JMBG with correct check digits.
    [TestCase("paid from HR12 1001 0051 8630 0016 0 yesterday", true)]
    [TestCase("refund to GB82WEST12345698765432, ref 7", true)]
    [TestCase("OIB 69435151530", true)]
    [TestCase("JMBG: 0101990500003", true)]
    [TestCase("ref HR13 1001 0051 8630 0016 0", false)]
    [TestCase("order 12345678904", false)]
    [TestCase("ACME Ltd", false)]
    public void BankingIdentifiers_AreFoundInText(string value, bool found)
    {
        BankingIdentifiers.ContainsBankingIdentifier(value).Should().Be(found);
    }

    [Test]
    public void ThePiiValueScreen_DropsBankingIdentifiers()
    {
        PiiValueScreen.ContainsPiiValue(["pay to HR12 1001 0051 8630 0016 0 today"], null).Should().BeTrue();
        PiiValueScreen.ContainsPiiValue(["NL91ABNA0417164300"], null).Should().BeTrue();
        PiiValueScreen.ContainsPiiValue(["PAID", "OPEN"], null).Should().BeFalse();
    }

    [TestCase("iban", true)]
    [TestCase("IBAN", true)]
    [TestCase("customer_iban", true)]
    [TestCase("CUSTOMER_IBAN", true)]
    [TestCase("CustomerIban", true)]
    [TestCase("CustomerIBAN", true)]
    [TestCase("IbanNumber", true)]
    [TestCase("IBANNumber", true)]
    [TestCase("iban2", true)]
    [TestCase("payee_ibans", true)]
    [TestCase("oib", true)]
    [TestCase("ClientOib", true)]
    [TestCase("oibKlijenta", true)]
    [TestCase("OIBKlijenta", true)]
    [TestCase("jmbg", true)]
    [TestCase("osoba_jmbg", true)]
    [TestCase("JMBG", true)]
    [TestCase("Libanon", false)]
    [TestCase("LIBANON", false)]
    [TestCase("Kibana", false)]
    [TestCase("kibana_index", false)]
    [TestCase("ribbon", false)]
    [TestCase("bioniche", false)]
    [TestCase("foible", false)]
    [TestCase("Stoiber", false)]
    [TestCase("account_number", true)]
    [TestCase("AccountNo", true)]
    [TestCase("acct_num", true)]
    [TestCase("account_id", false)]
    [TestCase("account_notes", false)]
    [TestCase("account_type", false)]
    public void ThePiiNamePattern_CoversBankingIdentifiers(string name, bool pii)
    {
        new QueryGuardrailService().IsPiiColumn(name).Should().Be(pii);
    }

    // In SQL text the banking words are found as identifiers, not inside other words.
    [TestCase("SELECT c.iban, c.name FROM customers c", true)]
    [TestCase("SELECT \"ClientOib\" FROM clients", true)]
    [TestCase("SELECT country FROM regions WHERE country = 'Libanon'", false)]
    [TestCase("SELECT kibana_index FROM dashboards", false)]
    public void ThePiiNamePattern_InSqlText_FindsTheBankingWordsAsIdentifiers(string sql, bool pii)
    {
        var result = new QueryGuardrailService().ValidateQuery(sql, new QueryGuardrailOptions { DetectPii = true });

        (result.PiiColumns is { Count: > 0 }).Should().Be(pii);
    }

    // Financial amounts are left out of value descriptions but stay unmasked in query results, where they are aggregated;
    // so are a person's name and Croatian personal data.
    [TestCase("salary", true)]
    [TestCase("MonthlySalaries", true)]
    [TestCase("current_balance", true)]
    [TestCase("AccountBalance", true)]
    [TestCase("annual_income", true)]
    [TestCase("hourly_wage", true)]
    [TestCase("balance_type", false)]
    [TestCase("BalanceTypeId", false)]
    [TestCase("income_category", false)]
    [TestCase("IncomeCode", false)]
    [TestCase("income_id", false)]
    [TestCase("signing_key", true)]
    [TestCase("iban", true)]
    [TestCase("first_name", true)]
    [TestCase("LastName", true)]
    [TestCase("middlename", true)]
    [TestCase("given_name", true)]
    [TestCase("FamilyName", true)]
    [TestCase("full_name", true)]
    [TestCase("surname", true)]
    [TestCase("customer_name", true)]
    [TestCase("CustomerFirstName", true)]
    [TestCase("ime", true)]
    [TestCase("prezime", true)]
    [TestCase("ime_kupca", true)]
    [TestCase("adresa", true)]
    [TestCase("telefon", true)]
    [TestCase("mobitel", true)]
    [TestCase("placa", true)]
    [TestCase("stanje_racuna", true)]
    [TestCase("broj_racuna", true)]
    [TestCase("BrojRacuna", true)]
    [TestCase("connection_string", true)]
    [TestCase("dsn", true)]
    [TestCase("webhook_url", true)]
    [TestCase("ProductKey", false)]
    [TestCase("username", false)]
    [TestCase("company_name", false)]
    [TestCase("file_name", false)]
    [TestCase("time", false)]
    [TestCase("regime", false)]
    [TestCase("status", false)]
    [TestCase("branch", false)]
    public void ColumnValueExclusions_LeaveOutPiiSecretAndFinancialNames(string name, bool excluded)
    {
        var guardrail = new QueryGuardrailService();

        ColumnValueExclusions.IsExcludedColumn(guardrail, name, null).Should().Be(excluded);
        guardrail.IsPiiColumn("current_balance").Should().BeFalse("a balance is not masked in query results");
    }

    // The boundaries of the credential shapes: a mixed base64-like run counts from 32 characters, a hex run from 33 (a
    // dash-less 32-digit GUID or MD5 is an id), key material is never shorter than 16 characters — but a credential written
    // into free text counts at any length.
    [TestCase("aB3aB3aB3aB3aB3aB3aB3aB3aB3aB3a", false)]
    [TestCase("aB3aB3aB3aB3aB3aB3aB3aB3aB3aB3aB", true)]
    [TestCase("0123456789abcdef0123456789abcdef", false)]
    [TestCase("0123456789abcdef0123456789abcdef0", true)]
    [TestCase("da39a3ee5e6b4b0d3255bfef95601890afd80709", true)]
    [TestCase("Ab3Cd4Ef5Gh6Ij7", false)]
    [TestCase("pwd=x", true)]
    public void TheCredentialShapes_HaveTheirBoundaries(string value, bool credential)
    {
        CredentialValueScreen.LooksLikeCredential(value).Should().Be(credential);
    }

    // A JMBG's day and month bound it on both sides, whatever its check digit says; an IBAN may be up to 34 characters.
    [TestCase("0001990500009", false)]
    [TestCase("3201990500009", false)]
    [TestCase("0100990500007", false)]
    [TestCase("0113990500001", false)]
    [TestCase("3112990500006", true)]
    public void Jmbg_DayAndMonth_AreBoundedBothWays(string value, bool valid)
    {
        BankingIdentifiers.IsJmbg(value).Should().Be(valid);
    }

    [TestCase("MT84MALT011000012345MTLCAST001S", true)]
    [TestCase("BR1800360305000010009795493C1", true)]
    [TestCase("LC31HEMM00010001001200120002301500", true)]
    [TestCase("LC68HEMM000100010012001200023015001", false)]
    public void Iban_LongerThanTwentyTwo_IsRecognised_UpToThirtyFour(string value, bool valid)
    {
        BankingIdentifiers.IsIban(value).Should().Be(valid);
        BankingIdentifiers.ContainsBankingIdentifier($"paid from {value} today").Should().Be(valid);
    }

    // At read time the stored samples are screened with today's rules: samples stored before these screens existed are
    // withheld until a refresh replaces them.
    [Test]
    public void TheStoredSampleScreen_WithholdsSecretLikeColumnsAndCredentialShapedValues()
    {
        var guardrail = new QueryGuardrailService();
        var settings = new McpSettingsData { EnableSampleValueCollection = true };

        StoredSampleScreen.Screen(guardrail, "signing_key", ["abc"], settings).Should().BeNull();
        StoredSampleScreen.Screen(guardrail, "current_balance", ["100.00"], settings).Should().BeNull();
        StoredSampleScreen.Screen(guardrail, "reference", ["ORD-1", TestCredentials.ProviderSecretKey], settings).Should().BeNull();
        StoredSampleScreen.Screen(guardrail, "status", ["PAID", "OPEN"], settings).Should().Equal("PAID", "OPEN");
        StoredSampleScreen.Screen(guardrail, "status", ["PAID"], new McpSettingsData()).Should().BeNull("sample values are off by default");
    }
}
