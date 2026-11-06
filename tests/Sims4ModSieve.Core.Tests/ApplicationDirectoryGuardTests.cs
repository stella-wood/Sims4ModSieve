using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Core.Tests;

[TestClass]
public sealed class ApplicationDirectoryGuardTests
{
    [TestMethod]
    public void DirectoryBoundaryNormalizationAndRefusalNotification()
    {
        using var temp = new TempDirectory();
        var app = temp.CreateDirectory("Mods", "Sieve");
        var refused = new List<string>();
        var guard = new ApplicationDirectoryGuard(app, refused.Add);
        Assert.IsFalse(guard.Contains(app));
        Assert.IsTrue(guard.Contains(Path.Combine(app, "nested", "..", "Sims4ModSieve.runtimeconfig.json")));
        Assert.IsFalse(guard.Contains(Path.Combine(app + "-backup", "settings.json")));
        Assert.IsFalse(guard.Contains(Path.Combine(app, "..", "mod.package")));
        Assert.IsTrue(guard.IsLocatedWithin(Path.GetDirectoryName(app)!));
        Assert.ThrowsExactly<IOException>(() => guard.EnsureAllowed(Path.Combine(app, "Sims4ModSieve.runtimeconfig.json")));
        Assert.AreEqual(1, refused.Count);
        guard.EnsureAllowed(Path.Combine(app + "-backup", "settings.json"));
        Assert.AreEqual(1, refused.Count);
    }

    [TestMethod]
    [DataRow("Sims4ModSieve.exe", true)]
    [DataRow("coreclr.dll", true)]
    [DataRow("runtime.dll.off", true)]
    [DataRow("Sims4ModSieve.deps.json", true)]
    [DataRow("BUILD-INFO.txt", true)]
    [DataRow("Assets/font.otf", true)]
    [DataRow("licenses/LICENSE.txt", true)]
    [DataRow("zh-Hans/PresentationFramework.resources.dll", true)]
    [DataRow("mod.package", false)]
    [DataRow("script.ts4script", false)]
    [DataRow("mod.package.off", false)]
    [DataRow("nested/mod.package", false)]
    [DataRow("settings.json", false)]
    public void OnlyProgramPayloadIsProtected(string relativePath, bool protectedPath)
    {
        using var temp = new TempDirectory();
        var app = temp.CreateDirectory("Desktop");
        var guard = new ApplicationDirectoryGuard(app);
        Assert.AreEqual(protectedPath, guard.Contains(Path.Combine(app, relativePath)));
    }
}
