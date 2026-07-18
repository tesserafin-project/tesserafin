using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Prometheus;
using Reefin.Api.Middleware;
using Reefin.Common.Net;
using Reefin.Controller.Configuration;
using Reefin.Controller.Diagnostics;
using Reefin.Controller.Extensions;
using Reefin.Database.Implementations;
using Reefin.LiveTv.Extensions;
using Reefin.LiveTv.Recordings;
using Reefin.MediaEncoding.Hls.Extensions;
using Reefin.Networking;
using Reefin.Networking.HappyEyeballs;
using Reefin.Server.Core.EntryPoints;
using Reefin.Server.Core.Localization;
using Reefin.Server.Extensions;
using Reefin.Server.HealthChecks;
using Reefin.Server.Implementations.Extensions;
using Reefin.XbmcMetadata;

namespace Reefin.Server
{
    /// <summary>
    /// Startup configuration for the Kestrel webhost.
    /// </summary>
    public class Startup
    {
        private readonly CoreAppHost _serverApplicationHost;
        private readonly IConfiguration _configuration;
        private readonly IServerConfigurationManager _serverConfigurationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="Startup" /> class.
        /// </summary>
        /// <param name="appHost">The server application host.</param>
        /// <param name="configuration">The used Configuration.</param>
        public Startup(CoreAppHost appHost, IConfiguration configuration)
        {
            _serverApplicationHost = appHost;
            _configuration = configuration;
            _serverConfigurationManager = appHost.ConfigurationManager;
        }

        /// <summary>
        /// Configures the service collection for the webhost.
        /// </summary>
        /// <param name="services">The service collection.</param>
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddResponseCompression();
            services.AddHttpContextAccessor();

            // Issue #42: one correlation id per HTTP request, readable from layers below the
            // hosting stack (Reefin.MediaEncoding) through the Reefin.Controller-level abstraction.
            // Singleton is correct despite the per-request value: the accessor is stateless and
            // reads the ambient HttpContext through IHttpContextAccessor on every call.
            services.AddSingleton<IRequestCorrelationAccessor, HttpRequestCorrelationAccessor>();
            services.AddHttpsRedirection(options =>
            {
                options.HttpsPort = _serverApplicationHost.HttpsPort;
            });

            services.AddReefinApi(_serverApplicationHost.GetApiPluginAssemblies(), _serverConfigurationManager.GetNetworkConfiguration());
            services.AddReefinDbContext(_serverApplicationHost.ConfigurationManager, _configuration);
            services.AddReefinApiSwagger();

            // configure custom legacy authentication
            services.AddCustomAuthentication();

            services.AddReefinApiAuthorization();

            var productHeader = new ProductInfoHeaderValue(
                _serverApplicationHost.Name.Replace(' ', '-'),
                _serverApplicationHost.ApplicationVersionString);
            var acceptJsonHeader = new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json, 1.0);
            var acceptXmlHeader = new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Xml, 0.9);
            var acceptAnyHeader = new MediaTypeWithQualityHeaderValue("*/*", 0.8);
            Func<IServiceProvider, HttpMessageHandler> eyeballsHttpClientHandlerDelegate = (_) => new SocketsHttpHandler()
            {
                AutomaticDecompression = DecompressionMethods.All,
                RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
                ConnectCallback = HttpClientExtension.OnConnect
            };

            Func<IServiceProvider, HttpMessageHandler> defaultHttpClientHandlerDelegate = (_) => new SocketsHttpHandler()
            {
                AutomaticDecompression = DecompressionMethods.All,
                RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8
            };

            services.AddHttpClient(NamedClient.Default, c =>
                {
                    c.DefaultRequestHeaders.UserAgent.Add(productHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptJsonHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptXmlHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptAnyHeader);
                })
                .ConfigurePrimaryHttpMessageHandler(eyeballsHttpClientHandlerDelegate);

            services.AddHttpClient(NamedClient.MusicBrainz, c =>
                {
                    c.DefaultRequestHeaders.UserAgent.Add(productHeader);
                    c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"({_serverApplicationHost.ApplicationUserAgentAddress})"));
                    c.DefaultRequestHeaders.Accept.Add(acceptXmlHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptAnyHeader);
                })
                .ConfigurePrimaryHttpMessageHandler(eyeballsHttpClientHandlerDelegate);

            services.AddHttpClient(NamedClient.DirectIp, c =>
                {
                    c.DefaultRequestHeaders.UserAgent.Add(productHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptJsonHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptXmlHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptAnyHeader);
                })
                .ConfigurePrimaryHttpMessageHandler(defaultHttpClientHandlerDelegate);

            services.AddHealthChecks()
                .AddCheck<DbContextFactoryHealthCheck<ReefinDbContext>>(nameof(ReefinDbContext));

            services.AddHlsPlaylistGenerator();
            services.AddLiveTvServices();

            var serverUICulture = _serverConfigurationManager.Configuration.UICulture;
            if (string.IsNullOrEmpty(serverUICulture))
            {
                serverUICulture = "en-US";
            }

            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(serverUICulture);

            services.Configure<RequestLocalizationOptions>(options =>
            {
                var supportedUICultures = LocalizationManager.GetSupportedUICultures();
                options.SupportedCultures = supportedUICultures;
                options.SupportedUICultures = supportedUICultures;
                options.DefaultRequestCulture = new RequestCulture(serverUICulture);
                options.ApplyCurrentCultureToResponseHeaders = true;
                options.FallBackToParentCultures = true;
                options.FallBackToParentUICultures = true;
            });

            services.AddHostedService<RecordingsHost>();
            services.AddHostedService<AutoDiscoveryHost>();
            services.AddHostedService<NfoUserDataSaver>();
            services.AddHostedService<LibraryChangedNotifier>();
            services.AddHostedService<UserDataChangeNotifier>();
            services.AddHostedService<RecordingNotifier>();
        }

        /// <summary>
        /// Configures the app builder for the webhost.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="env">The webhost environment.</param>
        /// <param name="appConfig">The application config.</param>
        public void Configure(
            IApplicationBuilder app,
            IWebHostEnvironment env,
            IConfiguration appConfig)
        {
            app.UseBaseUrlRedirection();

            // Wrap rest of configuration so everything only listens on BaseUrl.
            var config = _serverConfigurationManager.GetNetworkConfiguration();
            app.Map(config.BaseUrl, mainApp =>
            {
                if (env.IsDevelopment())
                {
                    mainApp.UseDeveloperExceptionPage();
                }

                mainApp.UseForwardedHeaders();

                // Issue #42: before ExceptionMiddleware, so that a request which ends in a handled
                // exception still gets its correlation id — both in the log scope covering the
                // exception log line and on the error response's X-Request-Id header.
                mainApp.UseMiddleware<RequestCorrelationMiddleware>();

                mainApp.UseMiddleware<ExceptionMiddleware>();

                mainApp.UseMiddleware<ResponseTimeMiddleware>();

                mainApp.UseWebSockets();

                mainApp.UseResponseCompression();

                mainApp.UseCors();

                mainApp.UseRequestLocalization();

                if (config.RequireHttps && _serverApplicationHost.ListenWithHttps)
                {
                    mainApp.UseHttpsRedirection();
                }

                if (appConfig.HostWebClient())
                {
                    var extensionProvider = new FileExtensionContentTypeProvider();

                    // subtitles octopus requires .data, .mem files.
                    extensionProvider.Mappings.Add(".data", MediaTypeNames.Application.Octet);
                    extensionProvider.Mappings.Add(".mem", MediaTypeNames.Application.Octet);
                    mainApp.UseDefaultFiles(new DefaultFilesOptions
                    {
                        FileProvider = new PhysicalFileProvider(_serverConfigurationManager.ApplicationPaths.WebPath),
                        RequestPath = "/web"
                    });
                    mainApp.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(_serverConfigurationManager.ApplicationPaths.WebPath),
                        RequestPath = "/web",
                        ContentTypeProvider = extensionProvider,
                        OnPrepareResponse = (context) =>
                        {
                            if (Path.GetFileName(context.File.Name).Equals("index.html", StringComparison.Ordinal))
                            {
                                context.Context.Response.Headers.CacheControl = new StringValues("no-cache");
                            }
                        }
                    });

                    mainApp.UseRobotsRedirection();
                }

                mainApp.UseStaticFiles();
                mainApp.UseAuthentication();
                mainApp.UseReefinApiSwagger(_serverConfigurationManager);
                mainApp.UseQueryStringDecoding();
                mainApp.UseRouting();
                mainApp.UseAuthorization();

                mainApp.UseIPBasedAccessValidation();
                mainApp.UseWebSocketHandler();
                mainApp.UseServerStartupMessage();

                if (_serverConfigurationManager.Configuration.EnableMetrics)
                {
                    // Must be registered after any middleware that could change HTTP response codes or the data will be bad
                    mainApp.UseHttpMetrics();
                }

                mainApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    if (_serverConfigurationManager.Configuration.EnableMetrics)
                    {
                        endpoints.MapMetrics();
                    }

                    endpoints.MapHealthChecks("/health");
                });
            });
        }
    }
}
