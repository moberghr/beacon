using Moq;
using Beacon.Core.Services.Providers;

namespace Beacon.Tests.Common;

/// <summary>
/// A provider factory whose every provider accepts any new configuration (the interface's default) and does nothing
/// else, for services that ask the connector to check a configuration before saving it.
/// </summary>
internal static class TestProviderFactory
{
    public static IDataSourceProviderFactory Accepting()
    {
        var factory = new Mock<IDataSourceProviderFactory>();
        factory
            .Setup(x => x.GetProvider(It.IsAny<Core.Data.Enums.DataSourceType>()))
            .Returns(() => Mock.Of<IDataSourceProvider>());

        return factory.Object;
    }
}
