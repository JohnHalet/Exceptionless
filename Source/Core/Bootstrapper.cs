﻿#region Copyright 2014 Exceptionless

// This program is free software: you can redistribute it and/or modify it 
// under the terms of the GNU Affero General Public License as published 
// by the Free Software Foundation, either version 3 of the License, or 
// (at your option) any later version.
// 
//     http://www.gnu.org/licenses/agpl-3.0.html

#endregion

using System;
using System.Configuration;
using System.Net;
using System.Threading.Tasks;
using Elasticsearch.Net.Connection;
using Exceptionless.Core.Dependency;
using Exceptionless.Core.AppStats;
using Exceptionless.Core.Billing;
using Exceptionless.Core.Caching;
using Exceptionless.Core.Component;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Lock;
using Exceptionless.Core.Messaging;
using Exceptionless.Core.Plugins.EventProcessor;
using Exceptionless.Core.Plugins.Formatting;
using Exceptionless.Core.Mail;
using Exceptionless.Core.Models;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Queues;
using Exceptionless.Core.Queues.Models;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Serialization;
using Exceptionless.Core.Storage;
using Exceptionless.Core.Utility;
using Exceptionless.Core.Validation;
using Exceptionless.Models;
using Exceptionless.Models.Admin;
using Exceptionless.Models.Data;
using FluentValidation;
using MongoDB.Driver;
using Nest;
using RazorSharpEmail;
using SimpleInjector;
using SimpleInjector.Packaging;
using StackExchange.Redis;
using Token = Exceptionless.Models.Admin.Token;

namespace Exceptionless.Core {
    public class Bootstrapper : IPackage {
        public void RegisterServices(Container container) {
            container.RegisterSingle<IDependencyResolver>(() => new SimpleInjectorCoreDependencyResolver(container));

            if (Settings.Current.EnableAppStats)
                container.RegisterSingle<IAppStatsClient>(() => new AppStatsClient(Settings.Current.AppStatsServerName, Settings.Current.AppStatsServerPort));
            else
                container.RegisterSingle<IAppStatsClient, InMemoryAppStatsClient>();

            container.RegisterSingle<IDependencyResolver>(() => new SimpleInjectorCoreDependencyResolver(container));

            container.RegisterSingle<MongoDatabase>(() => {
                if (String.IsNullOrEmpty(Settings.Current.MongoConnectionString))
                    throw new ConfigurationErrorsException("MongoConnectionString was not found in the Web.config.");

                MongoDefaults.MaxConnectionIdleTime = TimeSpan.FromMinutes(1);
                var url = new MongoUrl(Settings.Current.MongoConnectionString);
                string databaseName = url.DatabaseName;
                if (Settings.Current.AppendMachineNameToDatabase)
                    databaseName += String.Concat("-", Environment.MachineName.ToLower());

                MongoServer server = new MongoClient(url).GetServer();
                return server.GetDatabase(databaseName);
            });

            container.RegisterSingle<IElasticClient>(() => GetElasticClient(new Uri(Settings.Current.ElasticSearchConnectionString)));

            if (Settings.Current.EnableRedis) {
                var muxer = ConnectionMultiplexer.Connect(Settings.Current.RedisConnectionString);
                container.RegisterSingle(muxer);
                container.Register<IDatabase>(() => container.GetInstance<ConnectionMultiplexer>().GetDatabase());

                container.Register<ICacheClient, RedisCacheClient>();

                container.RegisterSingle<IQueue<EventPostFileInfo>>(() => new RedisQueue<EventPostFileInfo>(muxer, statName: StatNames.PostsQueueSize, stats: container.GetInstance<IAppStatsClient>()));
                container.RegisterSingle<IQueue<EventUserDescription>>(() => new RedisQueue<EventUserDescription>(muxer, statName: StatNames.EventsUserDescriptionQueueSize, stats: container.GetInstance<IAppStatsClient>()));
                container.RegisterSingle<IQueue<EventNotification>>(() => new RedisQueue<EventNotification>(muxer, statName: StatNames.EventNotificationQueueSize, stats: container.GetInstance<IAppStatsClient>()));
                container.RegisterSingle<IQueue<WebHookNotification>>(() => new RedisQueue<WebHookNotification>(muxer, statName: StatNames.WebHookQueueSize, stats: container.GetInstance<IAppStatsClient>()));
                container.RegisterSingle<IQueue<MailMessage>>(() => new RedisQueue<MailMessage>(muxer, statName: StatNames.EmailsQueueSize, stats: container.GetInstance<IAppStatsClient>()));

                container.RegisterSingle<RedisMessageBus>(() => new RedisMessageBus(muxer.GetSubscriber()));
                container.Register<IMessagePublisher>(container.GetInstance<RedisMessageBus>);
                container.Register<IMessageSubscriber>(container.GetInstance<RedisMessageBus>);
            } else {
                container.RegisterSingle<ICacheClient, InMemoryCacheClient>();

                container.RegisterSingle<IQueue<EventPostFileInfo>>(() => new InMemoryQueue<EventPostFileInfo>(statName: StatNames.PostsQueueSize, stats: container.GetInstance<IAppStatsClient>()));
                container.RegisterSingle<IQueue<EventUserDescription>>(() => new InMemoryQueue<EventUserDescription>(statName: StatNames.EventsUserDescriptionQueueSize, stats: container.GetInstance<IAppStatsClient>()));
                container.RegisterSingle<IQueue<EventNotification>>(() => new InMemoryQueue<EventNotification>(statName: StatNames.EventNotificationQueueSize, stats: container.GetInstance<IAppStatsClient>()));
                container.RegisterSingle<IQueue<WebHookNotification>>(() => new InMemoryQueue<WebHookNotification>(statName: StatNames.WebHookQueueSize, stats: container.GetInstance<IAppStatsClient>()));
                container.RegisterSingle<IQueue<MailMessage>>(() => new InMemoryQueue<MailMessage>(statName: StatNames.EmailsQueueSize, stats: container.GetInstance<IAppStatsClient>()));

                container.RegisterSingle<InMemoryMessageBus>();
                container.Register<IMessagePublisher>(container.GetInstance<InMemoryMessageBus>);
                container.Register<IMessageSubscriber>(container.GetInstance<InMemoryMessageBus>);
            }

            if (Settings.Current.EnableAzureStorage)
                container.RegisterSingle<IFileStorage>(new AzureFileStorage(Settings.Current.AzureStorageConnectionString));
            else if (!String.IsNullOrEmpty(Settings.Current.StorageFolder))
                container.RegisterSingle<IFileStorage>(new FolderFileStorage(Settings.Current.StorageFolder));
            else
                container.RegisterSingle<IFileStorage>(new InMemoryFileStorage());

            container.RegisterSingle<IStackRepository, StackRepository>();
            container.RegisterSingle<IEventRepository, EventRepository>();
            container.RegisterSingle<IOrganizationRepository, OrganizationRepository>();
            container.RegisterSingle<IProjectRepository, ProjectRepository>();
            container.RegisterSingle<IUserRepository, UserRepository>();
            container.RegisterSingle<IWebHookRepository, WebHookRepository>();
            container.RegisterSingle<ITokenRepository, TokenRepository>();
            container.RegisterSingle<IApplicationRepository, ApplicationRepository>();

            container.RegisterSingle<IValidator<Application>, ApplicationValidator>();
            container.RegisterSingle<IValidator<Organization>, OrganizationValidator>();
            container.RegisterSingle<IValidator<PersistentEvent>, PersistentEventValidator>();
            container.RegisterSingle<IValidator<Project>, ProjectValidator>();
            container.RegisterSingle<IValidator<Stack>, StackValidator>();
            container.RegisterSingle<IValidator<Token>, TokenValidator>();
            container.RegisterSingle<IValidator<UserDescription>, UserDescriptionValidator>();
            container.RegisterSingle<IValidator<User>, UserValidator>();
            container.RegisterSingle<IValidator<WebHook>, WebHookValidator>();


            container.RegisterSingle<IEmailGenerator>(() => new RazorEmailGenerator(@"Mail\Templates"));
            container.RegisterSingle<IMailer, Mailer>();
            if (Settings.Current.WebsiteMode != WebsiteMode.Dev)
                container.RegisterSingle<IMailSender, SmtpMailSender>();
            else
                container.RegisterSingle<IMailSender>(() => new InMemoryMailSender());

            container.Register<ILockProvider, CacheLockProvider>();
            container.Register<StripeEventHandler>();
            container.RegisterSingle<BillingManager>();
            container.RegisterSingle<DataHelper>();
            container.RegisterSingle<EventStats>();
            container.RegisterSingle<EventPipeline>();
            container.RegisterSingle<EventPluginManager>();
            container.RegisterSingle<FormattingPluginManager>();
        }

        public static IElasticClient GetElasticClient(Uri serverUri, bool deleteExistingIndexes = false) {
            var settings = new ConnectionSettings(serverUri).SetDefaultIndex("_all");
            settings.EnableMetrics();
            settings.SetJsonSerializerSettingsModifier(s => {
                s.ContractResolver = new EmptyCollectionElasticContractResolver(settings);
                s.AddModelConverters();
            });
            settings.MapDefaultTypeNames(m => m.Add(typeof(PersistentEvent), "events").Add(typeof(Stack), "stacks"));
            settings.MapDefaultTypeIndices(m => m.Add(typeof(Stack), ElasticSearchRepository<Stack>.StacksIndexName));
            settings.MapDefaultTypeIndices(m => m.Add(typeof(PersistentEvent), ElasticSearchRepository<PersistentEvent>.EventsIndexName + "-*"));
            settings.SetDefaultPropertyNameInferrer(p => p.ToLowerUnderscoredWords());

            var client = new ElasticClient(settings);
            ConfigureMapping(client, deleteExistingIndexes);

            return client;
        }

        private static void ConfigureMapping(IElasticClient searchclient, bool deleteExistingIndexes = false) {
            if (deleteExistingIndexes)
                searchclient.DeleteIndex(i => i.AllIndices());

            if (!searchclient.IndexExists(new IndexExistsRequest(new IndexNameMarker { Name = ElasticSearchRepository<Stack>.StacksIndexName })).Exists)
                searchclient.CreateIndex(ElasticSearchRepository<Stack>.StacksIndexName, d => d
                    .AddAlias("stacks")
                    .AddMapping<Stack>(map => map
                        .Dynamic(DynamicMappingOption.Ignore)
                        .Transform(t => t.Script(@"ctx._source['fixed'] = !!ctx._source['date_fixed']").Language(ScriptLang.Groovy))
                        .IncludeInAll(false)
                        .Properties(p => p
                            .String(f => f.Name(s => s.OrganizationId).IndexName("organization").Index(FieldIndexOption.NotAnalyzed))
                            .String(f => f.Name(s => s.ProjectId).IndexName("project").Index(FieldIndexOption.NotAnalyzed))
                            .String(f => f.Name(s => s.SignatureHash).IndexName("signature").Index(FieldIndexOption.NotAnalyzed))
                            .String(f => f.Name(e => e.Type).IndexName("type").Index(FieldIndexOption.Analyzed))
                            .Date(f => f.Name(s => s.FirstOccurrence).IndexName("first"))
                            .Date(f => f.Name(s => s.LastOccurrence).IndexName("last"))
                            .String(f => f.Name(s => s.Title).IndexName("title").Index(FieldIndexOption.Analyzed).IncludeInAll().Boost(1.1))
                            .String(f => f.Name(s => s.Description).IndexName("description").Index(FieldIndexOption.Analyzed).IncludeInAll())
                            .String(f => f.Name(s => s.Tags).IndexName("tag").Index(FieldIndexOption.Analyzed).IncludeInAll().Boost(1.2))
                            .String(f => f.Name(s => s.References).IndexName("links").Index(FieldIndexOption.Analyzed).IncludeInAll())
                            .Date(f => f.Name(s => s.DateFixed).IndexName("fixedon"))
                            .Boolean(f => f.Name("fixed"))
                            .Boolean(f => f.Name(s => s.IsHidden).IndexName("hidden"))
                            .Boolean(f => f.Name(s => s.IsRegressed).IndexName("regressed"))
                            .Boolean(f => f.Name(s => s.OccurrencesAreCritical).IndexName("critical"))
                            .Number(f => f.Name(s => s.TotalOccurrences).IndexName("occurrences"))
                        )
                    )
                );

            var response = searchclient.PutTemplate(ElasticSearchRepository<PersistentEvent>.EventsIndexName, d => d
                .Template(ElasticSearchRepository<PersistentEvent>.EventsIndexName + "-*")
                .Settings(s => s.Add("analysis",
                    new {
                        analyzer = new {
                            comma_whitespace = new {
                                type = "pattern",
                                pattern = @"[,\s]+"
                            }
                        }
                    }))
                .AddMapping<PersistentEvent>(map => map
                    .Dynamic(DynamicMappingOption.Ignore)
                    .IncludeInAll(false)
                    .DisableSizeField(false)
                    .Properties(p => p
                        .String(f => f.Name(e => e.OrganizationId).IndexName("organization").Index(FieldIndexOption.NotAnalyzed))
                        .String(f => f.Name(e => e.ProjectId).IndexName("project").Index(FieldIndexOption.NotAnalyzed))
                        .String(f => f.Name(e => e.StackId).IndexName("stack").Index(FieldIndexOption.NotAnalyzed))
                        .String(f => f.Name(e => e.ReferenceId).IndexName("reference").Index(FieldIndexOption.Analyzed))
                        .String(f => f.Name(e => e.SessionId).IndexName("session").Index(FieldIndexOption.Analyzed))
                        .String(f => f.Name(e => e.Type).IndexName("type").Index(FieldIndexOption.Analyzed))
                        .String(f => f.Name(e => e.Source).IndexName("source").Index(FieldIndexOption.Analyzed).IncludeInAll())
                        .Date(f => f.Name(e => e.Date).IndexName("date"))
                        .String(f => f.Name(e => e.Message).IndexName("message").Index(FieldIndexOption.Analyzed).IncludeInAll())
                        .String(f => f.Name(e => e.Tags).IndexName("tag").Index(FieldIndexOption.Analyzed).IncludeInAll().Boost(1.1))
                        .Boolean(f => f.Name(e => e.IsFirstOccurrence).IndexName("first"))
                        .Boolean(f => f.Name(e => e.IsFixed).IndexName("fixed"))
                        .Boolean(f => f.Name(e => e.IsHidden).IndexName("hidden"))
                        .Object<DataDictionary>(f => f.Name(e => e.Data).Properties(p2 => p2
                            .String(f2 => f2.Name(Event.KnownDataKeys.Version).Index(FieldIndexOption.NotAnalyzed)) // TODO: Multifield to anaylize this with multiple tokenizers.
                            .Object<RequestInfo>(f2 => f2.Name(Event.KnownDataKeys.RequestInfo).Path("just_name").Properties(p3 => p3
                                .String(f3 => f3.Name(r => r.ClientIpAddress).IndexName("ip").Index(FieldIndexOption.Analyzed).IncludeInAll().Analyzer("comma_whitespace"))
                                .String(f3 => f3.Name(r => r.UserAgent).IndexName("useragent").Index(FieldIndexOption.Analyzed))
                                .String(f3 => f3.Name(r => r.Path).IndexName("path").Index(FieldIndexOption.Analyzed).IncludeInAll())))
                            .Object<Error>(f2 => f2.Name(Event.KnownDataKeys.Error).Path("just_name").Properties(p3 => p3
                                .String(f3 => f3.Name(r => r.Code).IndexName("errorcode").Index(FieldIndexOption.NotAnalyzed).IncludeInAll().Boost(1.1))
                                .String(f3 => f3.Name(r => r.Message).IndexName("errormessage").Index(FieldIndexOption.Analyzed).IncludeInAll())
                                .String(f3 => f3.Name(r => r.Type).IndexName("errortype").Index(FieldIndexOption.Analyzed).IncludeInAll())))
                            .Object<SimpleError>(f2 => f2.Name(Event.KnownDataKeys.SimpleError).Path("just_name").Properties(p3 => p3
                                .String(f3 => f3.Name(r => r.Message).IndexName("errormessage").Index(FieldIndexOption.Analyzed).IncludeInAll())
                                .String(f3 => f3.Name(r => r.Type).IndexName("errortype").Index(FieldIndexOption.Analyzed).IncludeInAll())))
                            .Object<EnvironmentInfo>(f2 => f2.Name(Event.KnownDataKeys.EnvironmentInfo).Path("just_name").Properties(p3 => p3
                                .String(f3 => f3.Name(r => r.IpAddress).IndexName("ip").Index(FieldIndexOption.Analyzed).IncludeInAll().Analyzer("comma_whitespace"))
                                .String(f3 => f3.Name(r => r.MachineName).IndexName("machine").Index(FieldIndexOption.Analyzed).IncludeInAll().Boost(1.1))))
                            .Object<UserDescription>(f2 => f2.Name(Event.KnownDataKeys.UserDescription).Path("just_name").Properties(p3 => p3
                                .String(f3 => f3.Name(r => r.Description).IndexName("userdescription").Index(FieldIndexOption.Analyzed).IncludeInAll())
                                .String(f3 => f3.Name(r => r.EmailAddress).IndexName("useremail").Index(FieldIndexOption.Analyzed).IncludeInAll().Boost(1.1))))
                            .Object<UserInfo>(f2 => f2.Name(Event.KnownDataKeys.UserInfo).Path("just_name").Properties(p3 => p3
                                .String(f3 => f3.Name(r => r.Identity).IndexName("user").Index(FieldIndexOption.Analyzed).IncludeInAll().Boost(1.1))))))
                    )
                )
            );
        }
    }
}