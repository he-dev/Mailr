﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Custom;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Mailr.Extensions;
using Mailr.Extensions.Utilities.Mvc.Filters;
using Mailr.Helpers;
using Mailr.Http;
using Mailr.Middleware;
using Mailr.Mvc;
using Mailr.Mvc.Razor.ViewLocationExpanders;
using Mailr.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reusable;
using Reusable.IOnymous;
using Reusable.OmniLog;
using Reusable.OmniLog.Abstractions;
using Reusable.OmniLog.Attachments;
using Reusable.OmniLog.SemanticExtensions;
using Reusable.Utilities.AspNetCore.ActionFilters;
using Reusable.Utilities.NLog.LayoutRenderers;
using IHostingEnvironment = Microsoft.AspNetCore.Hosting.IHostingEnvironment;

[assembly: AspMvcMasterLocationFormat("~/src/Views/{1}/{0}.cshtml")]
[assembly: AspMvcViewLocationFormat("~/src/Views/{1}/{0}.cshtml")]
[assembly: AspMvcPartialViewLocationFormat("~/src/Views/Shared/{0}.cshtml")]

[assembly: InternalsVisibleTo("Mailr.Tests")]

namespace Mailr
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IHostingEnvironment hostingEnvironment)
        {
            Configuration = configuration;
            HostingEnvironment = hostingEnvironment;
        }

        private IConfiguration Configuration { get; }

        private IHostingEnvironment HostingEnvironment { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            SmartPropertiesLayoutRenderer.Register();

            services.AddSingleton<ILoggerFactory>
            (
                LoggerFactory
                    .Empty
                    .AttachObject("Environment", HostingEnvironment.EnvironmentName)
                    .AttachObject("Product", $"{ProgramInfo.Name}-v{ProgramInfo.Version}")
                    .AttachScope()
                    .AttachSnapshot()
                    .Attach<Timestamp<DateTimeUtc>>()
                    .AttachElapsedMilliseconds()
                    .AddObserver<NLogRx>()
            );

            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            services
                .AddMvc()
                .AddExtensions();

            services.AddApiVersioning(options =>
            {
                //options.ApiVersionReader = new HeaderApiVersionReader("Api-Version");
                options.ReportApiVersions = true;
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.DefaultApiVersion = new ApiVersion(1, 0);
            });

            services.AddScoped<ICssProvider, CssProvider>();
            services.AddRelativeViewLocationExpander();


            var mailProviderRegistrations = new Dictionary<SoftString, Action>
            {
                [nameof(SmtpProvider)] = () => services.AddSingleton<IResourceProvider, SmtpProvider>()
                //[nameof(OutlookProvider)] = () => services.AddSingleton<IResourceProvider, OutlookProvider>(),
            };

            var mailProvider = Configuration["mailProvider"];
            if (mailProviderRegistrations.TryGetValue(mailProvider, out var register))
            {
                register();
            }
            else
            {
                throw new ArgumentOutOfRangeException($"Invalid mail-provider: {mailProvider}. Expected [{mailProviderRegistrations.Keys.Join(", ")}].");
            }

            services.AddSingleton<IHostedService, WorkItemQueueService>();
            services.AddSingleton<IWorkItemQueue, WorkItemQueue>();

            services.AddScoped<ValidateModel>();
            services.AddScoped<SendEmail>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            //app.UseMiddleware<LogScopeMiddleware>();
            app.UseSemanticLogger(config => { config.ConfigureScope = (scope, context) => scope.AttachUserCorrelationId(context).AttachUserAgent(context); });

            if (env.IsDevelopment())
            {
                app.UseBrowserLink();
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }

            //app.UseWhen(httpContext => !httpContext.Request.Method.In(new[] { "GET" }, StringComparer.OrdinalIgnoreCase), UseHeaderValidator());

            app.UseEmail();
            app.UseStaticFiles();

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
                routes.MapRoute(
                    name: RouteNameFactory.CreateCssRouteName(ControllerType.Internal, false),
                    template: "wwwroot/css/{extension}/{controller}/{action}.css");
                routes.MapRoute(
                    name: RouteNameFactory.CreateCssRouteName(ControllerType.External, false),
                    template: "{extension}/wwwroot/css/{controller}/{action}.css");
                routes.MapRoute(
                    name: RouteNameFactory.CreateCssRouteName(ControllerType.External, true),
                    template: "{extension}/wwwroot/css/{controller}/{action}-{theme}.css");
                routes.MapRoute(
                    name: RouteNames.Themes,
                    template: "wwwroot/css/themes/{theme}.css");
            });
        }
    }

    internal static class RouteNames
    {
        public const string Themes = nameof(Themes);
    }

    internal static class RouteNameFactory
    {
        public static string CreateCssRouteName(ControllerType controllerType, bool useCustomTheme)
        {
            return $"{controllerType}-extension{(useCustomTheme ? "-with-theme" : default)}";
        }
    }

    internal static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddRelativeViewLocationExpander(this IServiceCollection services)
        {
            return services.Configure<RazorViewEngineOptions>(options =>
            {
                const string prefix = "src";

                options
                    .ViewLocationExpanders
                    .Add(new RelativeViewLocationExpander(prefix));
            });
        }
    }
}