using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentry.Extensibility;
using Sentry.Protocol;
using Sentry.Reflection;

namespace Sentry.AspNetCore
{
    /// <summary>
    /// Sentry middleware for ASP.NET Core
    /// </summary>
    internal class SentryMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly Func<IHub> _hubAccessor;
        private readonly SentryAspNetCoreOptions _options;
        private readonly IHostingEnvironment _hostingEnvironment;
        private readonly ILogger<SentryMiddleware> _logger;

        internal static readonly SdkVersion NameAndVersion
            = typeof(SentryMiddleware).Assembly.GetNameAndVersion();

        private static readonly string ProtocolPackageName = "nuget:" + NameAndVersion.Name;

        /// <summary>
        /// Initializes a new instance of the <see cref="SentryMiddleware"/> class.
        /// </summary>
        /// <param name="next">The next.</param>
        /// <param name="hubAccessor">The sentry Hub accessor.</param>
        /// <param name="options">The options for this integration</param>
        /// <param name="hostingEnvironment">The hosting environment.</param>
        /// <param name="logger">Sentry logger.</param>
        /// <exception cref="ArgumentNullException">
        /// next
        /// or
        /// sentry
        /// </exception>
        public SentryMiddleware(
            RequestDelegate next,
            Func<IHub> hubAccessor,
            IOptions<SentryAspNetCoreOptions> options,
            IHostingEnvironment hostingEnvironment,
            ILogger<SentryMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _hubAccessor = hubAccessor ?? throw new ArgumentNullException(nameof(hubAccessor));
            _options = options?.Value;
            if (_options != null)
            {
                var hub = _hubAccessor();
                foreach (var callback in _options.ConfigureScopeCallbacks)
                {
                    hub.ConfigureScope(callback);
                }
            }
            _hostingEnvironment = hostingEnvironment;
            _logger = logger;
        }

        /// <summary>
        /// Handles the <see cref="HttpContext"/> while capturing any errors
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns></returns>
        public async Task InvokeAsync(HttpContext context)
        {
            var hub = _hubAccessor();
            if (!hub.IsEnabled)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            using (hub.PushAndLockScope())
            {
                if (_options != null)
                {
                    if (_options.MaxRequestBodySize != RequestSize.None)
                    {
                        context.Request.EnableBuffering();
                    }
                    if (_options.FlushOnCompletedRequest)
                    {
                        context.Response.OnCompleted(async () =>
                        {
                            // Serverless environments flush the queue at the end of each request
                            await hub.FlushAsync(timeout: _options.FlushTimeout).ConfigureAwait(false);
                        });
                    }
                }

                hub.ConfigureScope(scope =>
                {
                    // At the point lots of stuff from the request are not yet filled
                    // Identity for example is added later on in the pipeline
                    // Subscribing to the event so that HTTP data is only read in case an event is going to be
                    // sent to Sentry. This avoid the cost on error-free requests.
                    // In case of event, all data made available through the HTTP Context at the time of the
                    // event creation will be sent to Sentry

                    scope.OnEvaluating += (_, __) => PopulateScope(context, scope);
                });
                try
                {
                    await _next(context).ConfigureAwait(false);

                    // When an exception was handled by other component (i.e: UseExceptionHandler feature).
                    var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                    if (exceptionFeature?.Error != null)
                    {
                        CaptureException(exceptionFeature.Error);
                    }
                }
                catch (Exception e)
                {
                    CaptureException(e);

                    ExceptionDispatchInfo.Capture(e).Throw();
                }

                void CaptureException(Exception e)
                {
                    var evt = new SentryEvent(e);

                    _logger?.LogTrace("Sending event '{SentryEvent}' to Sentry.", evt);

                    var id = hub.CaptureEvent(evt);

                    _logger?.LogInformation("Event '{id}' queued.", id);
                }
            }
        }

        internal void PopulateScope(HttpContext context, Scope scope)
        {
            scope.Sdk.Name = Constants.SdkName;
            scope.Sdk.Version = NameAndVersion.Version;
            scope.Sdk.AddPackage(ProtocolPackageName, NameAndVersion.Version);

            if (_hostingEnvironment != null)
            {
                scope.SetWebRoot(_hostingEnvironment.WebRootPath);
            }

            scope.Populate(context, _options);

            if (_options?.IncludeActivityData == true)
            {
                scope.Populate(Activity.Current);
            }
        }
    }
}
