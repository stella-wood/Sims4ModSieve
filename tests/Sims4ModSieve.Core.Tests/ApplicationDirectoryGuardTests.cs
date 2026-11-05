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
        Assert.IsTrue(guard.Contains(app));
        Assert.IsTrue(guard.Contains(Path.Combine(app, "nested", "..", "settings.json")));
        Assert.IsFalse(guard.Contains(Path.Combine(app + "-backup", "settings.json")));
        Assert.IsFalse(guard.Contains(Path.Combine(app, "..", "mod.package")));
        Assert.IsTrue(guard.IsLocatedWithin(Path.GetDirectoryName(app)!));
        Assert.ThrowsExactly<IOException>(() => guard.EnsureAllowed(Path.Combine(app, "settings.json")));
        Assert.AreEqual(1, refused.Count);
        guard.EnsureAllowed(Path.Combine(app + "-backup", "settings.json"));
        Assert.AreEqual(1, refused.Count);
    }
}
