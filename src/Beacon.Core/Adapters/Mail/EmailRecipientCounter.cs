using System.Net.Mail;

namespace Beacon.Core.Adapters.Mail;

internal static class EmailRecipientCounter
{
    /// <summary>
    /// How many distinct addresses an email destination lists (commas or semicolons between them). Parsed as mail
    /// addresses, so a display name holding a comma counts once and a repeated address counts once; a destination that
    /// does not parse counts every separated part, which never counts fewer than the addresses it holds.
    /// </summary>
    public static int Count(string destination)
    {
        try
        {
            var addresses = new MailAddressCollection();
            addresses.Add(destination.Replace(';', ','));

            return addresses
                .Select(x => x.Address.ToLowerInvariant())
                .Distinct()
                .Count();
        }
        catch (FormatException)
        {
            return destination
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length;
        }
    }
}
