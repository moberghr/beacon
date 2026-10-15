using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Tests.Common;

/// <summary>
/// Data Protection for a test host outside Development: the key ring stays in memory (nothing is written to a database
/// or a disk) and is encrypted with a self-signed certificate made for the test run, so the startup check that refuses
/// unencrypted keys outside Development passes the way it does for a real host.
/// </summary>
internal static class TestDataProtection
{
    private static readonly Lazy<X509Certificate2> Certificate = new(CreateCertificate);

    public static void UseEncryptedInMemoryKeyRing(IServiceCollection services)
    {
        services.AddDataProtection().ProtectKeysWithCertificate(Certificate.Value);
        services.Configure<KeyManagementOptions>(x => x.XmlRepository = new InMemoryXmlRepository());
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=beacon-tests-data-protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class InMemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements)
            {
                return _elements
                    .Select(x => new XElement(x))
                    .ToList();
            }
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
            {
                _elements.Add(new XElement(element));
            }
        }
    }
}
