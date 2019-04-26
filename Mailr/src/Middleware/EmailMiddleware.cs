﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mailr.Extensions.Abstractions;
using Mailr.Extensions.Utilities;
using Mailr.Http;
using Mailr.Models;
using Mailr.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Reusable.IOnymous;
using Reusable.OmniLog;
using Reusable.OmniLog.Abstractions;
using Reusable.OmniLog.SemanticExtensions;

namespace Mailr.Middleware
{
    using static ItemNames;

    public class EmailMiddleware
    {
        private readonly RequestDelegate _next;

        private readonly ILogger _logger;

        private readonly IWorkItemQueue _workItemQueue;

        private readonly IResourceProvider _mailProvider;

        private readonly IConfiguration _configuration;

        public EmailMiddleware
        (
            RequestDelegate next,
            ILoggerFactory loggerFactory,
            IWorkItemQueue workItemQueue,
            IResourceProvider mailProvider,
            IConfiguration configuration
        )
        {
            _next = next;
            _logger = loggerFactory.CreateLogger<EmailMiddleware>();
            _workItemQueue = workItemQueue;
            _mailProvider = mailProvider;
            _configuration = configuration;
        }

        public async Task Invoke(HttpContext context)
        {
            var originalResponseBody = context.Response.Body;

            using (var responseBody = new MemoryStream())
            {
                context.Response.Body = responseBody;

                try
                {
                    await _next(context);

                    if (context.Items[EmailMetadata] is IEmailMetadata emailMetadata)
                    {
                        // Selecting interface properties because otherwise the body will be dumped too.
                        _logger.Log(Abstraction.Layer.Business().Meta(new { EmailMetadata = new { emailMetadata.From, emailMetadata.To, emailMetadata.Subject, emailMetadata.IsHtml } }));

                        using (var responseBodyCopy = new MemoryStream())
                        {
                            // We need a copy of this because the internal handler might close it and we won't able to restore it.
                            responseBody.Rewind();
                            await responseBody.CopyToAsync(responseBodyCopy);
                            await SendEmailAsync(context, responseBodyCopy, emailMetadata);
                        }

                        // Restore Response.Body
                        responseBody.Rewind();
                        await responseBody.CopyToAsync(originalResponseBody);
                        context.Response.Body = originalResponseBody;
                    }
                }
                catch (Exception inner)
                {
                    _logger.Log(Abstraction.Layer.Network().Routine("next").Faulted(), inner);
                }
            }
        }

        private async Task SendEmailAsync(HttpContext context, Stream responseBody, IEmailMetadata emailMetadata)
        {
            responseBody.Rewind();
            using (var reader = new StreamReader(responseBody))
            {
                var body = await reader.ReadToEndAsync();
                _workItemQueue.Enqueue(async cancellationToken =>
                {
                    // We need to rebuild the scope here because it'll be executed outside the request pipeline.
                    var scope = _logger.BeginScope().AttachElapsed().AttachUserCorrelationId(context).AttachUserAgent(context);

                    try
                    {
                        if (emailMetadata.CanSend)
                        {
                            _logger.Log(Abstraction.Layer.Service().Decision("Send email.").Because("Sending emails is enabled."));

                            var metadata =
                                Metadata
                                    .Empty
                                    .Scope<ISmtpMetadata>(s => s
                                        .Set(x => x.Host, _configuration["Smtp:Host"])
                                        .Set(x => x.Port, int.Parse(_configuration["Smtp:Port"]))
                                    );


                            await _mailProvider.SendEmailAsync(new Email<EmailSubject, EmailBody>
                            {
                                From = emailMetadata.From ?? "mailr@test.com",
                                To = emailMetadata.To,
                                CC = emailMetadata.CC,
                                Subject = new EmailSubject { Value = emailMetadata.Subject },
                                Body = new EmailBody { Value = body },
                                IsHtml = emailMetadata.IsHtml,
                                Attachments = emailMetadata.Attachments
                            }, metadata);
                            _logger.Log(Abstraction.Layer.Network().Routine(nameof(MailProviderExtensions.SendEmailAsync)).Completed());
                        }
                        else
                        {
                            _logger.Log(Abstraction.Layer.Service().Decision("Don't send email.").Because("Sending emails is disabled."));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(Abstraction.Layer.Network().Routine(nameof(MailProviderExtensions.SendEmailAsync)).Faulted(), ex);
                    }
                    finally
                    {
                        scope.Dispose();
                    }
                });
            }
        }
    }

    public static class EmailMiddlewareExtensions
    {
        public static IApplicationBuilder UseEmail(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<EmailMiddleware>();
        }
    }
}