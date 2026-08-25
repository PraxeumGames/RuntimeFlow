using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Content;
using RuntimeFlow.Demo.Midcore;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// E2E coverage for the midcore demo flow. Currently covers the required-config
    /// failure path; full happy-path coverage requires resolving the same-scope
    /// content-source initialization ordering issue (tracked separately).
    /// </summary>
    public sealed class MidcoreEndToEndTests
    {
        [SetUp]
        public void Reset()
        {
            MidcoreGame.Backend.Reset();
        }

        [Test]
        public async Task RequiredConfig_Down_StartupFails()
        {
            MidcoreGame.Backend.FailConfig = true;

            Exception? caught = null;
            try
            {
                await using var game = await MidcoreGame.Define().StartAsync();
            }
            catch (Exception ex) { caught = ex; }

            Assert.IsNotNull(caught, "Required config failure must fail startup.");
            StringAssert.Contains("remote-config", caught!.Message);
        }
    }
}
