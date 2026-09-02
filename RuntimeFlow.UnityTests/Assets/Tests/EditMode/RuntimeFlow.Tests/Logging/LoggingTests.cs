using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Internal;
using UnityEngine;
using UnityEngine.TestTools;

namespace RuntimeFlow.Tests.Logging
{
    /// <summary>UnityConsoleLogger maps Microsoft.Extensions.Logging levels onto the Unity console.</summary>
    [TestFixture]
    public sealed class LoggingTests
    {
        [Test]
        public void InformationBecomesDebugLog()
        {
            LogAssert.Expect(LogType.Log, "[RuntimeFlow] session: started");
            new UnityConsoleLogger().Info("[RuntimeFlow] session: started");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void DebugBecomesDebugLog()
        {
            LogAssert.Expect(LogType.Log, "[RuntimeFlow] session: Alpha started");
            new UnityConsoleLogger().Debug("[RuntimeFlow] session: Alpha started");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void WarningBecomesLogWarning()
        {
            LogAssert.Expect(LogType.Warning, "[RuntimeFlow] session: no progress for 10.0s.");
            new UnityConsoleLogger().Warn("[RuntimeFlow] session: no progress for 10.0s.");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ErrorBecomesLogError()
        {
            LogAssert.Expect(LogType.Error, "[RuntimeFlow] session: Alpha failed");
            new UnityConsoleLogger().Error("[RuntimeFlow] session: Alpha failed");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void CriticalBecomesLogError()
        {
            LogAssert.Expect(LogType.Error, "[RuntimeFlow] fatal");
            var logger = new UnityConsoleLogger();
            logger.Log(LogLevel.Critical, default, "fatal", null, (state, _) => state);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void PrefixIsAddedOnceOnly()
        {
            LogAssert.Expect(LogType.Log, "[RuntimeFlow] plain message");
            LogAssert.Expect(LogType.Log, "[RuntimeFlow] already prefixed");
            var logger = new UnityConsoleLogger();
            logger.Info("plain message");
            logger.Info("[RuntimeFlow] already prefixed");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AnErrorWithAnExceptionKeepsTheClickableStackTrace()
        {
            LogAssert.Expect(LogType.Error, "[RuntimeFlow] session: Alpha failed");
            LogAssert.Expect(LogType.Exception, new Regex("^InvalidOperationException: boom"));

            new UnityConsoleLogger().Error("[RuntimeFlow] session: Alpha failed", new InvalidOperationException("boom"));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AWarningWithAnExceptionStaysOneConsoleLine()
        {
            LogAssert.Expect(LogType.Warning, new Regex("^\\[RuntimeFlow\\] careful[\\s\\S]*InvalidOperationException"));

            new UnityConsoleLogger().Log(LogLevel.Warning, default, "careful",
                new InvalidOperationException("boom"), (state, _) => state);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void MinLevelSuppressesLowerLevels()
        {
            var logger = new UnityConsoleLogger(LogLevel.Warning);
            Assert.That(logger.IsEnabled(LogLevel.Debug), Is.False);
            Assert.That(logger.IsEnabled(LogLevel.Warning), Is.True);
            logger.Debug("must not reach the console");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void NoneIsNeverEnabled()
        {
            Assert.That(new UnityConsoleLogger(LogLevel.Trace).IsEnabled(LogLevel.None), Is.False);
        }
    }
}
