﻿using System;
using System.Collections.Generic;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using System.Xml.Linq;
using Autofac;
using Moq;
using NUnit.Framework;
using Orchard.Caching;
using Orchard.ContentManagement.MetaData;
using Orchard.ContentManagement.MetaData.Models;
using Orchard.ContentManagement.MetaData.Services;
using Orchard.Core.Settings.Metadata;
using Orchard.Data;
using Orchard.DisplayManagement;
using Orchard.DisplayManagement.Descriptors;
using Orchard.DisplayManagement.Implementation;
using Orchard.Environment;
using Orchard.ContentManagement;
using Orchard.ContentManagement.Handlers;
using Orchard.ContentManagement.Records;
using Orchard.Environment.Extensions;
using Orchard.Localization;
using Orchard.Messaging.Events;
using Orchard.Messaging.Services;
using Orchard.Security;
using Orchard.Security.Permissions;
using Orchard.Tests.Stubs;
using Orchard.UI.Notify;
using Orchard.Users.Controllers;
using Orchard.Users.Handlers;
using Orchard.Users.Models;
using Orchard.Users.Services;
using Orchard.Users.ViewModels;
using Orchard.Settings;
using Orchard.Core.Settings.Services;
using Orchard.Tests.Messaging;
using Orchard.Environment.Configuration;
using Orchard.Core.Settings.Models;
using Orchard.Core.Settings.Handlers;
using Orchard.Messaging.Models;
using System.Collections.Specialized;

namespace Orchard.Tests.Modules.Users.Controllers {
    [TestFixture]
    public class AccountControllerTests : DatabaseEnabledTestsBase {
        private AccountController _controller;
        private Mock<IAuthorizer> _authorizer;
        private Mock<WorkContext> _workContext;
        private MessagingChannelStub _channel;

        public override void Register(ContainerBuilder builder) {
            builder.RegisterType<AccountController>().SingleInstance();
            builder.RegisterType<SiteService>().As<ISiteService>();
            builder.RegisterType<DefaultContentManager>().As<IContentManager>();
            builder.RegisterType(typeof(SettingsFormatter))
                .As(typeof(IMapper<XElement, SettingsDictionary>))
                .As(typeof(IMapper<SettingsDictionary, XElement>));
            builder.RegisterType<ContentDefinitionManager>().As<IContentDefinitionManager>();
            builder.RegisterType<DefaultContentManagerSession>().As<IContentManagerSession>();
            builder.RegisterType<DefaultContentQuery>().As<IContentQuery>().InstancePerDependency();
            builder.RegisterType<DefaultMessageManager>().As<IMessageManager>();
            builder.RegisterInstance(_channel = new MessagingChannelStub()).As<IMessagingChannel>();
            builder.RegisterInstance(new Mock<IMessageEventHandler>().Object);
            builder.RegisterInstance(new Mock<IAuthenticationService>().Object);
            builder.RegisterType<MembershipService>().As<IMembershipService>();
            builder.RegisterType<UserService>().As<IUserService>();
            builder.RegisterType<UserPartHandler>().As<IContentHandler>();
            builder.RegisterType<OrchardServices>().As<IOrchardServices>();
            builder.RegisterType<TransactionManager>().As<ITransactionManager>();
            builder.RegisterType<DefaultShapeTableManager>().As<IShapeTableManager>();
            builder.RegisterType<DefaultShapeFactory>().As<IShapeFactory>();
            builder.RegisterType<StubExtensionManager>().As<IExtensionManager>();
            builder.RegisterType<SiteSettingsPartHandler>().As<IContentHandler>();
            builder.RegisterType<RegistrationSettingsPartHandler>().As<IContentHandler>();
            builder.RegisterInstance(new Mock<INotifier>().Object);
            builder.RegisterInstance(new Mock<IContentDisplay>().Object);
            builder.RegisterType<StubCacheManager>().As<ICacheManager>();
            builder.RegisterType<Signals>().As<ISignals>();
            builder.RegisterInstance(new ShellSettings { Name = "Alpha", RequestUrlHost = "wiki.example.com", RequestUrlPrefix = "~/foo" });

            _authorizer = new Mock<IAuthorizer>();
            builder.RegisterInstance(_authorizer.Object);

            _authorizer.Setup(x => x.Authorize(It.IsAny<Permission>(), It.IsAny<LocalizedString>())).Returns(true);

            _workContext = new Mock<WorkContext>();
            _workContext.Setup(w => w.GetState<ISite>(It.Is<string>(s => s == "CurrentSite"))).Returns(() => { return _container.Resolve<ISiteService>().GetSiteSettings(); });

            var _workContextAccessor = new Mock<IWorkContextAccessor>();
            _workContextAccessor.Setup(w => w.GetContext()).Returns(_workContext.Object);
            builder.RegisterInstance(_workContextAccessor.Object).As<IWorkContextAccessor>();
            
        }

        protected override IEnumerable<Type> DatabaseTypes {
            get {
                return new[] { typeof(UserPartRecord),
                    typeof(SiteSettingsPartRecord), 
                    typeof(RegistrationSettingsPartRecord), 
                    typeof(ContentTypeRecord),
                    typeof(ContentItemRecord),
                    typeof(ContentItemVersionRecord), 
                };
            }
        }

        public override void Init() {
            base.Init();

            var manager = _container.Resolve<IContentManager>();

            var superUser = manager.New<UserPart>("User");
            superUser.Record = new UserPartRecord { UserName = "admin", NormalizedUserName = "admin", Email = "admin@orcharproject.com" };
            manager.Create(superUser.ContentItem);

            _controller = _container.Resolve<AccountController>();

            var mockHttpContext = new Mock<HttpContextBase>();
            _controller.ControllerContext = new ControllerContext(
                mockHttpContext.Object,
                new RouteData(
                    new Route("foo", new MvcRouteHandler()),
                    new MvcRouteHandler()),
                _controller);
        }

        [Test]
        public void UsersShouldNotBeAbleToRegisterIfNotAllowed() {

            // enable user registration
            _container.Resolve<IWorkContextAccessor>().GetContext().CurrentSite.As<RegistrationSettingsPart>().UsersCanRegister = false;
            _session.Flush();

            var result = _controller.Register();
            Assert.That(result, Is.TypeOf<HttpNotFoundResult>());

            result = _controller.Register("bar", "bar@baz.com", "66554321", "66554321");
            Assert.That(result, Is.TypeOf<HttpNotFoundResult>());
        }

        [Test]
        public void UsersShouldBeAbleToRegisterIfAllowed() {

            // disable user registration
            _container.Resolve<IWorkContextAccessor>().GetContext().CurrentSite.As<RegistrationSettingsPart>().UsersCanRegister = true;
            _session.Flush();

            var result = _controller.Register();
            Assert.That(result, Is.TypeOf<ViewResult>());
        }

        [Test]
        public void RegisteredUserShouldBeRedirectedToHomePage() {

            var registrationSettings = _container.Resolve<IWorkContextAccessor>().GetContext().CurrentSite.As<RegistrationSettingsPart>();
            registrationSettings.UsersCanRegister = true;
            registrationSettings.UsersAreModerated = false;
            registrationSettings.UsersMustValidateEmail = false;

            _session.Flush();

           var result = _controller.Register("bar", "bar@baz.com", "66554321", "66554321");

           Assert.That(result, Is.TypeOf<RedirectResult>());
           Assert.That(((RedirectResult)result).Url, Is.EqualTo("~/"));
        }


        [Test]
        public void RegisteredUserShouldBeModerated() {

            var registrationSettings = _container.Resolve<IWorkContextAccessor>().GetContext().CurrentSite.As<RegistrationSettingsPart>();
            registrationSettings.UsersCanRegister = true;
            registrationSettings.UsersAreModerated = true;

            _session.Flush();

            var result = _controller.Register("bar", "bar@baz.com", "66554321", "66554321");

            Assert.That(result, Is.TypeOf<RedirectToRouteResult>());
            Assert.That(((RedirectToRouteResult)result).RouteValues["action"], Is.EqualTo("RegistrationPending"));
            Assert.That(_channel.Messages.Count, Is.EqualTo(0));
        }

        [Test]
        public void SuperAdminShouldReceiveAMessageOnUserRegistration() {

            var registrationSettings = _container.Resolve<IWorkContextAccessor>().GetContext().CurrentSite.As<RegistrationSettingsPart>();
            registrationSettings.UsersCanRegister = true;
            registrationSettings.UsersAreModerated = true;
            registrationSettings.NotifyModeration = true;

            _container.Resolve<IWorkContextAccessor>().GetContext().CurrentSite.As<SiteSettingsPart>().SuperUser = "admin";
            _session.Flush();

            var result = _controller.Register("bar", "bar@baz.com", "66554321", "66554321");
            _session.Flush();

            var user = _container.Resolve<IMembershipService>().GetUser("bar");

            Assert.That(result, Is.TypeOf<RedirectToRouteResult>());
            Assert.That(((RedirectToRouteResult)result).RouteValues["action"], Is.EqualTo("RegistrationPending"));
            Assert.That(_channel.Messages.Count, Is.EqualTo(1));
            Assert.That(user, Is.Not.Null);
            Assert.That(user.UserName, Is.EqualTo("bar"));
            Assert.That(user.As<UserPart>(), Is.Not.Null);
            Assert.That(user.As<UserPart>().EmailStatus, Is.EqualTo(UserStatus.Approved));
            Assert.That(user.As<UserPart>().RegistrationStatus, Is.EqualTo(UserStatus.Pending));
        }

        [Test]
        public void InvalidLostPasswordRequestShouldNotResultInAnError() {
            var registrationSettings = _container.Resolve<IWorkContextAccessor>().GetContext().CurrentSite.As<RegistrationSettingsPart>();
            registrationSettings.UsersCanRegister = true;
            _session.Flush();

            _controller.Register("bar", "bar@baz.com", "66554321", "66554321");

            _controller.Url = new UrlHelper(new RequestContext(new HttpContextStub(), new RouteData()));

            var result = _controller.LostPassword("foo");
            
            Assert.That(result, Is.TypeOf<RedirectToRouteResult>());
            Assert.That(((RedirectToRouteResult)result).RouteValues["action"], Is.EqualTo("LogOn"));
            Assert.That(_channel.Messages.Count, Is.EqualTo(0));
        }

        [Test]
        public void ResetPasswordLinkShouldBeSent() {
            var registrationSettings = _container.Resolve<IWorkContextAccessor>().GetContext().CurrentSite.As<RegistrationSettingsPart>();
            registrationSettings.UsersCanRegister = true;
            registrationSettings.EnableLostPassword = true;
            _session.Flush();

            _controller.Register("bar", "bar@baz.com", "66554321", "66554321");
            _session.Flush();

            _controller.Url = new UrlHelper(new RequestContext(new HttpContextStub(), new RouteData()));
            var result = _controller.RequestLostPassword("bar");
            Assert.That(result, Is.TypeOf<RedirectToRouteResult>());

            Assert.That(((RedirectToRouteResult)result).RouteValues["action"], Is.EqualTo("LogOn"));
            Assert.That(_channel.Messages.Count, Is.EqualTo(1));
        }

        [Test]
        [Ignore("To be implemented")]
        public void ChallengeEmailShouldUnlockAccount() {
        }

        [Test]
        [Ignore("To be implemented")]
        public void LostPasswordEmailShouldAuthenticateUser() {
        }

        class HttpContextStub : HttpContextBase {
            public override HttpRequestBase Request {
                get { return new HttpRequestStub(); }
            }

            public override IHttpHandler Handler { get; set; }
        }

        class HttpRequestStub : HttpRequestBase {
            public override bool IsAuthenticated {
                get { return false; }
            }

            public override NameValueCollection Form {
                get {
                    return new NameValueCollection();
                }
            }

            public override Uri Url {
                get {
                    return new Uri("http://orchardproject.net");
                }
            }

            public override NameValueCollection Headers {
                get {
                    var nv = new NameValueCollection();
                    nv["Host"] = "orchardproject.net";
                    return nv;
                }
            }
        }

    }
}