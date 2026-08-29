namespace Arith.Cli.Tests;

[TestClass]
public sealed class BuildInfoTests
{
    [TestMethod]
    public void VersionMatchesProjectVersion()
    {
        Assert.AreEqual("0.1.0", BuildInfo.Version);
    }
}
