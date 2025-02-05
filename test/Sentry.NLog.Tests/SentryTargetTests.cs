using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using NSubstitute;
using Sentry.Extensibility;
using Sentry.Infrastructure;
using Sentry.Protocol;
using Sentry.Reflection;

using Xunit;

namespace Sentry.NLog.Tests
{
    using static DsnSamples;

    public class SentryTargetTests
    {
        private const string DefaultMessage = "This is a logged message";

        private class Fixture
        {
            public SentryNLogOptions Options { get; set; } = new SentryNLogOptions { Dsn = Valid };

            public IHub Hub { get; set; } = Substitute.For<IHub>();

            public Func<IHub> HubAccessor { get; set; }

            public ISystemClock Clock { get; set; } = Substitute.For<ISystemClock>();

            public IDisposable SdkDisposeHandle { get; set; } = Substitute.For<IDisposable>();

            public Scope Scope { get; }

            public Fixture()
            {
                Hub.IsEnabled.Returns(true);
                HubAccessor = () => Hub;
                Scope = new Scope(new SentryOptions());
                Hub.ConfigureScope(Arg.Invoke(Scope));
            }

            public Target GetTarget(bool asyncTarget = false)
            {
                Target target = new SentryTarget(
                    Options,
                    HubAccessor,
                    SdkDisposeHandle,
                    Clock)
                {
                    Name = "sentry",
                    Dsn = Options.Dsn?.ToString(),
                };

                if (asyncTarget)
                {
                    target = new AsyncTargetWrapper(target)
                    {
                        Name = "sentry_async"
                    };
                }
                return target;
            }

            public LogFactory GetLoggerFactory(bool asyncTarget = false)
            {
                var target = GetTarget(asyncTarget);

                var factory = new LogFactory();

                var config = new LoggingConfiguration(factory);
                config.AddTarget("sentry", target);
                config.AddRule(LogLevel.Trace, LogLevel.Fatal, target);

                factory.Configuration = config;
                return factory;
            }

            public Logger GetLogger() => GetLoggerFactory().GetLogger("sentry");
        }

        private readonly Fixture _fixture = new Fixture();

        [Fact]
        public void Can_configure_from_xml_file()
        {
            var configXml = $@"
                <nlog throwConfigExceptions='true'>
                    <extensions>
                        <add type='{typeof(SentryTarget).AssemblyQualifiedName}' />
                    </extensions>
                    <targets>
                        <target type='Sentry' name='sentry' dsn='{ValidDsnWithoutSecret}'>
                            <options>
                                <environment>Development</environment>
                            </options>
                        </target>
                    </targets>
                </nlog>";

            var stringReader = new System.IO.StringReader(configXml);
            var xmlReader = System.Xml.XmlReader.Create(stringReader);
            var c = new XmlLoggingConfiguration(xmlReader, null);

            var t = c.FindTargetByName("sentry") as SentryTarget;
            Assert.NotNull(t);
            Assert.Equal(ValidDsnWithoutSecret, t.Options.Dsn.ToString());
            Assert.Equal("Development", t.Options.Environment);
        }

        [Fact]
        public void Shutdown_DisposesSdk()
        {
            _fixture.Options.InitializeSdk = false;
            var target = _fixture.GetTarget();
            SimpleConfigurator.ConfigureForTargetLogging(target);

            var sut = LogManager.GetCurrentClassLogger();

            sut.Error(DefaultMessage);

            _fixture.SdkDisposeHandle.DidNotReceive().Dispose();

            LogManager.Shutdown();

            _fixture.SdkDisposeHandle.Received(1).Dispose();
        }

        [Fact]
        public void Shutdown_NoDisposeHandleProvided_DoesNotThrow()
        {
            _fixture.Options.InitializeSdk = false;
            var factory = _fixture.GetLoggerFactory();

            var sut = factory.GetCurrentClassLogger();

            sut.Error(DefaultMessage);
            LogManager.Shutdown();
        }

        [Fact]
        public void Log_WithException_CreatesEventWithException()
        {
            var expected = new Exception("expected");

            var logger = _fixture.GetLoggerFactory().GetLogger("sentry");

            logger.Error(expected, DefaultMessage);

            _fixture.Hub.Received(1)
                    .CaptureEvent(Arg.Is<SentryEvent>(e => e.Exception == expected));
        }

        [Fact]
        public void Log_WithOnlyException_GeneratesBreadcrumbFromException()
        {
            var expectedException = new Exception("expected message");

            const BreadcrumbLevel expectedLevel = BreadcrumbLevel.Error;

            _fixture.Options.MinimumEventLevel = LogLevel.Fatal;
            var logger = _fixture.GetLogger();

            logger.Error(expectedException);

            var b = _fixture.Scope.Breadcrumbs.First();

            Assert.Equal(b.Message, $"{expectedException.GetType()}: {expectedException.Message}");
            Assert.Equal(b.Timestamp, _fixture.Clock.GetUtcNow());
            Assert.Null(b.Category);
            Assert.Equal(b.Level, expectedLevel);
            Assert.Null(b.Type);
            Assert.NotNull(b.Data);
            Assert.Equal(expectedException.GetType().ToString(), b.Data["exception_type"]);
            Assert.Equal(expectedException.Message, b.Data["exception_message"]);
        }

        [Fact]
        public void Log_NLogSdk_Name()
        {
            _fixture.Options.MinimumEventLevel = LogLevel.Info;
            var logger = _fixture.GetLogger();

            var expected = typeof(SentryTarget).Assembly.GetNameAndVersion();
            logger.Info(DefaultMessage);

            _fixture.Hub.Received(1)
                    .CaptureEvent(Arg.Is<SentryEvent>(e => e.Sdk.Name == Constants.SdkName
                                                           && e.Sdk.Version == expected.Version));
        }

        [Fact]
        public void Log_NLogSdk_Packages()
        {
            _fixture.Options.MinimumEventLevel = LogLevel.Info;
            var logger = _fixture.GetLogger();

            SentryEvent actual = null;
            _fixture.Hub.When(h => h.CaptureEvent(Arg.Any<SentryEvent>()))
                    .Do(c => actual = c.Arg<SentryEvent>());

            logger.Info(DefaultMessage);

            var expected = typeof(SentryTarget).Assembly.GetNameAndVersion();

            Assert.NotNull(actual);
            var package = Assert.Single(actual.Sdk.Packages);
            Assert.Equal("nuget:" + expected.Name, package.Name);
            Assert.Equal(expected.Version, package.Version);
        }

        [Theory]
        [ClassData(typeof(LogLevelData))]
        public void Log_LoggerLevel_Set(LogLevel nlogLevel, SentryLevel? sentryLevel)
        {
            // Make sure test cases are not filtered out by the default min levels:
            _fixture.Options.MinimumEventLevel = LogLevel.Trace;
            _fixture.Options.MinimumBreadcrumbLevel = LogLevel.Trace;

            var logger = _fixture.GetLogger();

            var evt = new LogEventInfo()
            {
                Message = DefaultMessage,
                Level = nlogLevel
            };

            logger.Log(evt);

            _fixture.Hub.Received(1)
                .CaptureEvent(Arg.Is<SentryEvent>(e => e.Level == sentryLevel));
        }

        [Fact]
        public void Log_RenderedMessage_Set()
        {
            const string unFormatted = "This is the message: {data}";
            object[] args = { "data" };

            var manager = _fixture.GetLoggerFactory();
            var target = manager.Configuration.FindTargetByName<SentryTarget>("sentry");

            var evt = new LogEventInfo(LogLevel.Error, "sentry", null, unFormatted, args);

            var expected = target.Layout.Render(evt);

            manager.GetLogger("sentry").Log(evt);

            _fixture.Hub.Received(1)
                    .CaptureEvent(Arg.Is<SentryEvent>(e => e.LogEntry.Formatted == expected));
        }

        [Fact]
        public void Log_HubAccessorReturnsNull_DoesNotThrow()
        {
            _fixture.HubAccessor = () => null;
            var sut = _fixture.GetLogger();
            sut.Error(DefaultMessage);
        }

        [Fact]
        public void Log_DisabledHub_CaptureNotCalled()
        {
            _fixture.Hub.IsEnabled.Returns(false);
            var sut = _fixture.GetLogger();

            sut.Error(DefaultMessage);

            _fixture.Hub.DidNotReceive().CaptureEvent(Arg.Any<SentryEvent>());
        }

        [Fact]
        public void Log_EnabledHub_CaptureCalled()
        {
            _fixture.Hub.IsEnabled.Returns(true);
            var sut = _fixture.GetLogger();

            sut.Error(DefaultMessage);

            _fixture.Hub.Received(1).CaptureEvent(Arg.Any<SentryEvent>());
        }

        [Fact]
        public void Log_NullLogEvent_CaptureNotCalled()
        {
            var sut = _fixture.GetLogger();
            string message = null;

            // ReSharper disable once AssignNullToNotNullAttribute
            sut.Error(message);

            _fixture.Hub.DidNotReceive().CaptureEvent(Arg.Any<SentryEvent>());
        }

        [Fact]
        public void Log_Properties_AsExtra()
        {
            const string expectedIp = "127.0.0.1";

            var sut = _fixture.GetLogger();

            sut.Error("Something happened: {IPAddress}", expectedIp);

            _fixture.Hub.Received(1)
                    .CaptureEvent(Arg.Is<SentryEvent>(e => e.Extra["IPAddress"].ToString() == expectedIp));
        }

        [Fact]
        public void Log_WithFormat_EventCaptured()
        {
            const string expectedMessage = "Test {structured} log";
            const int param = 10;

            var sut = _fixture.GetLogger();

            sut.Error(expectedMessage, param);

            _fixture.Hub.Received(1).CaptureEvent(Arg.Is<SentryEvent>(p =>
                p.LogEntry.Formatted == $"Test {param} log"
                && p.LogEntry.Message == expectedMessage));
        }

        [Fact]
        public void Log_SourceContextMatchesSentry_NoScopeConfigured()
        {
            var sut = _fixture.GetLogger();

            sut.Error("message {SourceContext}", "Sentry.NLog");

            _fixture.Hub.DidNotReceive().ConfigureScope(Arg.Any<Action<BaseScope>>());
        }

        [Fact]
        public void Log_SourceContextContainsSentry_NoScopeConfigured()
        {
            var sut = _fixture.GetLogger();

            sut.Error("message {SourceContext}", "Sentry");

            _fixture.Hub.DidNotReceive().ConfigureScope(Arg.Any<Action<BaseScope>>());
        }

        [Fact]
        public void Log_WithCustomBreadcrumbLayout_RendersCorrectly()
        {
            _fixture.Options.BreadcrumbLayout = "${logger}: ${message}";
            _fixture.Options.MinimumBreadcrumbLevel = LogLevel.Trace;

            var factory = _fixture.GetLoggerFactory();
            var sentryTarget = factory.Configuration.FindTargetByName<SentryTarget>("sentry");
            sentryTarget.IncludeEventDataOnBreadcrumbs = true;
            var logger = factory.GetLogger("sentry");

            const string message = "This is a breadcrumb";

            var evt = LogEventInfo.Create(LogLevel.Debug, logger.Name, message);
            evt.Properties["a"] = "b";
            logger.Log(evt);

            var b = _fixture.Scope.Breadcrumbs.First();
            Assert.Equal($"{logger.Name}: {message}", b.Message);
            Assert.Equal("b", b.Data["a"]);
        }

        [Fact]
        public async Task LogManager_WhenFlushCalled_CallsSentryFlushAsync()
        {
            const int NLogTimeout = 2;
            var timeout = TimeSpan.FromSeconds(NLogTimeout);

            _fixture.Options.FlushTimeout = timeout;
            var factory = _fixture.GetLoggerFactory(asyncTarget: true);

            // Verify that it's asynchronous
            Assert.NotEmpty(factory.Configuration.AllTargets.OfType<AsyncTargetWrapper>());

            var logger = factory.GetLogger("sentry");

            var hub = _fixture.Hub;

            logger.Info("Here's a message");
            logger.Debug("Here's another message");
            logger.Error(new Exception(DefaultMessage));

            var testDisposable = Substitute.For<IDisposable>();

            AsyncContinuation continuation = e =>
            {
                testDisposable.Dispose();
            };

            factory.Flush(continuation, timeout);

            await Task.Delay(timeout);

            testDisposable.Received().Dispose();
            hub.Received().FlushAsync(Arg.Any<TimeSpan>()).GetAwaiter().GetResult();
        }

        [Fact]
        public void InitializeTarget_InitializesSdk()
        {
            _fixture.Options.Dsn = null;
            _fixture.Options.Debug = true;
            _fixture.SdkDisposeHandle = null;
            _fixture.Options.InitializeSdk = true;
            var logger = Substitute.For<IDiagnosticLogger>();

            logger.IsEnabled(SentryLevel.Warning).Returns(true);
            _fixture.Options.DiagnosticLogger = logger;

            _ = _fixture.GetLoggerFactory();
            logger.Received(1).Log(SentryLevel.Warning,
                    "Init was called but no DSN was provided nor located. Sentry SDK will be disabled.", null);
        }

        [Fact]
        public void Dsn_ReturnsDsnFromOptions_Null()
        {
            _fixture.Options.Dsn = null;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Null(target.Dsn);
        }

        [Fact]
        public void Dsn_ReturnsDsnFromOptions_Instance()
        {
            var expectedDsn = new Dsn("https://a@sentry.io/1");
            _fixture.Options.Dsn = expectedDsn;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(expectedDsn.ToString(), target.Dsn);
        }

        [Fact]
        public void MinimumEventLevel_SetInOptions_ReturnsValue()
        {
            var expected = LogLevel.Warn;
            _fixture.Options.MinimumEventLevel = expected;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(expected.ToString(), target.MinimumEventLevel);
        }

        [Fact]
        public void MinimumEventLevel_Null_ReturnsLogLevelOff()
        {
            _fixture.Options.MinimumEventLevel = null;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(LogLevel.Off.ToString(), target.MinimumEventLevel);
        }

        [Fact]
        public void MinimumEventLevel_SetterReplacesOptions()
        {
            _fixture.Options.MinimumEventLevel = LogLevel.Fatal;
            var target = (SentryTarget)_fixture.GetTarget();
            const string expected = "Debug";
            target.MinimumEventLevel = expected;
            Assert.Equal(expected, target.MinimumEventLevel);
        }

        [Fact]
        public void MinimumBreadcrumbLevel_SetInOptions_ReturnsValue()
        {
            var expected = LogLevel.Warn;
            _fixture.Options.MinimumBreadcrumbLevel = expected;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(expected.ToString(), target.MinimumBreadcrumbLevel);
        }

        [Fact]
        public void MinimumBreadcrumbLevel_Null_ReturnsLogLevelOff()
        {
            _fixture.Options.MinimumBreadcrumbLevel = null;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(LogLevel.Off.ToString(), target.MinimumBreadcrumbLevel);
        }

        [Fact]
        public void MinimumBreadcrumbLevel_SetterReplacesOptions()
        {
            _fixture.Options.MinimumBreadcrumbLevel = LogLevel.Fatal;
            var target = (SentryTarget)_fixture.GetTarget();
            const string expected = "Debug";
            target.MinimumBreadcrumbLevel = expected;
            Assert.Equal(expected, target.MinimumBreadcrumbLevel);
        }

        [Fact]
        public void SendEventPropertiesAsData_Default_True()
        {
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.True(target.SendEventPropertiesAsData);
        }

        [Fact]
        public void SendEventPropertiesAsData_ValueFromOptions()
        {
            _fixture.Options.SendEventPropertiesAsData = false;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.False(target.SendEventPropertiesAsData);
        }

        [Fact]
        public void SendEventPropertiesAsData_SetterReplacesOptions()
        {
            _fixture.Options.SendEventPropertiesAsData = true;
            var target = (SentryTarget)_fixture.GetTarget();
            target.SendEventPropertiesAsData = false;
            Assert.False(target.SendEventPropertiesAsData);
        }

        [Fact]
        public void SendEventPropertiesAsTags_Default_False()
        {
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.False(target.SendEventPropertiesAsTags);
        }

        [Fact]
        public void SendEventPropertiesAsTags_ValueFromOptions()
        {
            _fixture.Options.SendEventPropertiesAsTags = false;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.False(target.SendEventPropertiesAsTags);
        }

        [Fact]
        public void SendEventPropertiesAsTags_SetterReplacesOptions()
        {
            _fixture.Options.SendEventPropertiesAsTags = true;
            var target = (SentryTarget)_fixture.GetTarget();
            target.SendEventPropertiesAsTags = false;
            Assert.False(target.SendEventPropertiesAsTags);
        }

        [Fact]
        public void IncludeEventDataOnBreadcrumbs_Default_False()
        {
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.False(target.IncludeEventDataOnBreadcrumbs);
        }

        [Fact]
        public void IncludeEventDataOnBreadcrumbs_ValueFromOptions()
        {
            _fixture.Options.IncludeEventDataOnBreadcrumbs = false;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.False(target.IncludeEventDataOnBreadcrumbs);
        }

        [Fact]
        public void IncludeEventDataOnBreadcrumbs_SetterReplacesOptions()
        {
            _fixture.Options.IncludeEventDataOnBreadcrumbs = true;
            var target = (SentryTarget)_fixture.GetTarget();
            target.IncludeEventDataOnBreadcrumbs = false;
            Assert.False(target.IncludeEventDataOnBreadcrumbs);
        }

        [Fact]
        public void ShutdownTimeoutSeconds_ValueFromOptions()
        {
            const int expected = 60;
            _fixture.Options.ShutdownTimeoutSeconds = expected;
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(expected, target.ShutdownTimeoutSeconds);
        }

        [Fact]
        public void ShutdownTimeoutSeconds_Default_2Seconds()
        {
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(2, target.ShutdownTimeoutSeconds);
        }

        [Fact]
        public void ShutdownTimeoutSeconds_SetterReplacesOptions()
        {
            var expected = 60;
            _fixture.Options.ShutdownTimeoutSeconds = int.MinValue;
            var target = (SentryTarget)_fixture.GetTarget();
            target.ShutdownTimeoutSeconds = expected;
            Assert.Equal(expected, target.ShutdownTimeoutSeconds);
        }

        [Fact]
        public void FlushTimeoutSeconds_ValueFromOptions()
        {
            var expected = 10;
            _fixture.Options.FlushTimeout = TimeSpan.FromSeconds(expected);
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(expected, target.FlushTimeoutSeconds);
        }

        [Fact]
        public void FlushTimeoutSeconds_SetterReplacesOptions()
        {
            var expected = 100;
            _fixture.Options.FlushTimeout = TimeSpan.FromSeconds(expected);
            var target = (SentryTarget)_fixture.GetTarget();
            target.FlushTimeoutSeconds = expected;
            Assert.Equal(expected, target.FlushTimeoutSeconds);
        }

        [Fact]
        public void IgnoreEventsWithNoException_SetterReplacesOptions()
        {
            _fixture.Options.IgnoreEventsWithNoException = false;
            var target = (SentryTarget)_fixture.GetTarget();
            target.IgnoreEventsWithNoException = true;
            Assert.True(target.IgnoreEventsWithNoException);
        }

        [Fact]
        public void FlushTimeoutSeconds_Default_15Seconds()
        {
            var target = (SentryTarget)_fixture.GetTarget();
            Assert.Equal(15, target.FlushTimeoutSeconds);
        }

        [Fact]
        public void BreadcrumbLayout_Null_FallsBackToLayout()
        {
            var target = (SentryTarget)_fixture.GetTarget();
            target.BreadcrumbLayout = null;
            Assert.Equal(target.Layout, target.BreadcrumbLayout);
        }

        [Fact]
        public void Ctor_Options_UseHubAdapter()
            => Assert.Equal(HubAdapter.Instance, new SentryTarget(new SentryNLogOptions()).HubAccessor());

        [Fact]
        public void GetTagsFromLogEvent_ContextProperties()
        {
            var factory = _fixture.GetLoggerFactory();
            var sentryTarget = factory.Configuration.FindTargetByName<SentryTarget>("sentry");
            sentryTarget.Tags.Add(new TargetPropertyWithContext("Logger", "${logger:shortName=true}"));
            sentryTarget.SendEventPropertiesAsTags = true;

            var logger = factory.GetLogger("sentry");
            logger.Fatal(DefaultMessage);

            _fixture.Hub.Received(1)
                .CaptureEvent(Arg.Is<SentryEvent>(e => e.Tags["Logger"] == "sentry"));
        }


        [Fact]
        public void GetTagsFromLogEvent_PropertiesMapped()
        {
            var factory = _fixture.GetLoggerFactory();
            var sentryTarget = factory.Configuration.FindTargetByName<SentryTarget>("sentry");
            sentryTarget.SendEventPropertiesAsTags = true;

            var logger = factory.GetLogger("sentry");
            logger.Fatal("{a}", "b");

            _fixture.Hub.Received(1)
                .CaptureEvent(Arg.Is<SentryEvent>(e => e.Tags["a"] == "b"));
        }

        internal class LogLevelData : IEnumerable<object[]>
        {
            public IEnumerator<object[]> GetEnumerator()
            {
                yield return new object[] { LogLevel.Debug, SentryLevel.Debug };
                yield return new object[] { LogLevel.Trace, SentryLevel.Debug };
                yield return new object[] { LogLevel.Info, SentryLevel.Info };
                yield return new object[] { LogLevel.Warn, SentryLevel.Warning };
                yield return new object[] { LogLevel.Error, SentryLevel.Error };
                yield return new object[] { LogLevel.Fatal, SentryLevel.Fatal };
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
