using System.Runtime.Versioning;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

[SupportedOSPlatform("windows")]
public class CredentialStoreTests
{
    [Fact]
    public void SavesLoadsAndDeletes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(CredentialStore.Load(account));
            CredentialStore.Save([1, 2, 3], account);
            CredentialStore.Save([4, 5, 6, 7], account);
            Assert.Equal([4, 5, 6, 7], CredentialStore.Load(account));
        }
        finally
        {
            CredentialStore.Delete(account);
        }
        Assert.Null(CredentialStore.Load(account));
        // Deleting what isn't there is fine.
        CredentialStore.Delete(account);
    }
}
