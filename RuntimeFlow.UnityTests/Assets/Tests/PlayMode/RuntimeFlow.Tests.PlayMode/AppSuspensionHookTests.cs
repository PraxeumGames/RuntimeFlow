using NUnit.Framework;
using RuntimeFlow.Internal;
using UnityEngine;

namespace RuntimeFlow.Tests.PlayMode
{
    /// <summary>
    /// The hidden pause hook of a mobile player: one per player session, never shown or saved, and its
    /// <c>OnApplicationPause</c> drives the suspension clock. Installed directly, since the editor is not a
    /// mobile platform and the automatic installation skips it.
    /// </summary>
    [TestFixture]
    public sealed class AppSuspensionHookTests
    {
        [TearDown]
        public void TearDown()
        {
            foreach (var hook in Resources.FindObjectsOfTypeAll<AppSuspensionHook>())
                Object.DestroyImmediate(hook.gameObject);
            AppSuspension.Resume();
        }

        [Test]
        public void InstallingTwiceCreatesOneHiddenHookThatForwardsPauses()
        {
            AppSuspensionHook.Install();
            AppSuspensionHook.Install();

            var hooks = Resources.FindObjectsOfTypeAll<AppSuspensionHook>();
            Assert.That(hooks, Has.Length.EqualTo(1));
            Assert.That(hooks[0].gameObject.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));

            hooks[0].SendMessage("OnApplicationPause", true);
            Assert.That(AppSuspension.IsSuspended, Is.True, "a paused player stops the clock");

            hooks[0].SendMessage("OnApplicationPause", false);
            Assert.That(AppSuspension.IsSuspended, Is.False, "a resumed player restarts it");
        }
    }
}
