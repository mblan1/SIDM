using SIDM.Core;

namespace SIDM.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Name_is_SIDM()
    {
        AppInfo.Name.Should().Be("SIDM");
    }

    [Fact]
    public void Version_is_semver()
    {
        AppInfo.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }
}
