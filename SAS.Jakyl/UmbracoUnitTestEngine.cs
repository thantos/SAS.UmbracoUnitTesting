﻿using Moq;
using Ploeh.AutoFixture;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Routing;
using System.Web.Mvc;
using System.Web.Routing;
using Umbraco.Core;
using Umbraco.Core.Configuration.UmbracoSettings;
using Umbraco.Core.Dictionary;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Profiling;
using Umbraco.Core.Security;
using Umbraco.Core.Services;
using Umbraco.Web;
using Umbraco.Web.Models;
using Umbraco.Web.Mvc;
using Umbraco.Web.Routing;
using Umbraco.Web.Security;
using Umbraco.Web.WebApi;
using SAS.Jakyl.Core.BootManager;
using SAS.Jakyl.Core.ViewEngine;
using SAS.Jakyl.Core;
using Umbraco.Core.Models.EntityBase;

namespace SAS.Jakyl
{
    public class UmbracoUnitTestEngine : IDisposable
    {
        private Random _rand = new Random();
        private HashSet<int> contentIdCollection = new HashSet<int>();
        private HashSet<Action> ControllerActions = new HashSet<Action>();

        private readonly List<IPublishedContent> Content = new List<IPublishedContent>();
        private readonly List<IPublishedContent> Media = new List<IPublishedContent>();
        private readonly List<PublishedContentType> ContentTypes = new List<PublishedContentType>();
        private readonly List<PublishedContentType> MediaTypes = new List<PublishedContentType>();

        private readonly CustomBoot _boot;

        private readonly MockContainer _mocks;

        public MockServiceContext mockServiceContext { get; private set; } //making this public for now. 

        private UmbracoHelper _umbHelper;
        private RouteData _routeData;
        private HttpRouteData _httpRouteData;
        private PublishedContentRequest _publishedContentRequest;

        private readonly bool EnforceUniqueContentIds;

        private bool _viewEngineCleared;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="autoFixture"></param>
        /// <param name="mockServiceContainer">A service container with mockable services. One is created if it isn't provided. Provide to manually mock all Umbraco services</param>
        /// <param name="EnforceUniqueContentIds"></param>
        public UmbracoUnitTestEngine(Fixture autoFixture = null, MockServiceContext mockServiceContainer = null, bool EnforceUniqueContentIds = true)
        {
            _Fixture = autoFixture ?? new Fixture();

            Content = new List<IPublishedContent>();
            Media = new List<IPublishedContent>();

            _mocks = new MockContainer();
            mockServiceContext = mockServiceContainer ?? new MockServiceContext();

            this.ApplicationContext = UmbracoUnitTestHelper.GetApplicationContext(serviceContext: this.ServiceContext,
                logger: _mocks.ResolveObject<ILogger>());

            this.UmbracoContext = UmbracoUnitTestHelper.GetUmbracoContext(ApplicationContext, 
                httpContext: _mocks.ResolveObject<HttpContextBase>()
                , webRoutingSettings: _mocks.ResolveObject<IWebRoutingSection>(),
                webSecurity: _mocks.ResolveObject<WebSecurity>(null, _mocks.ResolveObject<HttpContextBase>(), null));

            _mocks.Resolve<IWebRoutingSection>().Setup(s => s.UrlProviderMode).Returns(UrlProviderMode.AutoLegacy.ToString()); //needed for currenttemplate, IPublishedContent.UrlAbsolute

            this.EnforceUniqueContentIds = EnforceUniqueContentIds;

            AffectsController(true, GiveRenderMvcControllerPublishedContextRouteData);

            _boot = UmbracoUnitTestHelper.GetCustomBootManager(serviceContext: ServiceContext);
        }

        #region Properties
        public Fixture _Fixture { get; private set; }
        public ServiceContext ServiceContext { get { return this.mockServiceContext.ServiceContext; } }
        public ApplicationContext ApplicationContext { get; private set; }
        public UmbracoContext UmbracoContext { get; private set; }
        public Controller Controller { get; private set; }
        public ApiController ApiController { get; private set; }
        public bool HasAnyController { get { return HasMvcController || HasApiController; } }
        public bool HasMvcController { get { return Controller != null; } }
        public bool HasApiController { get { return ApiController != null; } }
        public UmbracoHelper UmbracoHelper { get { return NeedsUmbracoHelper(); } }
        public IPublishedContent CurrentPage { get { return _mocks.ResolveObject<IPublishedContent>("Current"); } }
        public IUser CurrentUser { get { return _mocks.ResolveObject<IUser>("Current"); } }
        #endregion

        public UmbracoHelper WithUmbracoHelper()
        {
            return NeedsUmbracoHelper();
        }

        public void WithDictionaryValue(string key, string value)
        {
            NeedsUmbracoHelper();
            _mocks.Resolve<ICultureDictionary>().Setup(s => s[key]).Returns(value);
        }

        public IPublishedContent WithCurrentPage(string name = null, int? id = null, string path = null, string url = null, int? templateId = null, DateTime? updateDate = null, DateTime? createDate = null, PublishedContentType contentType = null, IPublishedContent parent = null, IEnumerable<IPublishedContent> Children = null, IEnumerable<IPublishedProperty> properties = null, int? index = null)
        {
            var content = WithPublishedContentPage(_mocks.Resolve<IPublishedContent>("Current"), name, id, path, url,
                templateId, updateDate, createDate
                , contentType, parent, Children, properties, index);

            NeedsPublishedContentRequest(); //Might want to removed, only needed with controller context, which calls this too... needs to have the published content request
            AffectsController(true, GiveControllerContext); //will make sure a controller has the controller context and current page

            return content;
        }

        public IPublishedContent WithPublishedContentPage(Mock<IPublishedContent> mock = null, string name = null, int? id = null, string path = null, string url = null, int? templateId = null, DateTime? updateDate = null, DateTime? createDate = null, PublishedContentType contentType = null, IPublishedContent parent = null, IEnumerable<IPublishedContent> Children = null, IEnumerable<IPublishedProperty> properties = null, int? index = null)
        {
            //TODO handle template alias and template ID and expentions like GetTemplateAlias
            //TODO handle prev/following siblings
            var contentMock = UmbracoUnitTestHelper.SetPublishedContentMock(
                mock ?? new Mock<IPublishedContent>(),
                name ?? _Fixture.Create<string>(),
                ResolveUnqueContentId(id), path ?? _Fixture.Create<string>(), url ?? _Fixture.Create<string>(),
                templateId, updateDate, createDate
                , contentType, parent, Children, properties, index);
            var content = contentMock.Object;

            Content.Add(content);

            return content;
        }

        public IPublishedContent WithPublishedMedia(Mock<IPublishedContent> mock = null, string name = null, int? id = null, string path = null, string url = null, int? templateId = null, DateTime? updateDate = null, DateTime? createDate = null, PublishedContentType contentType = null, IPublishedContent parent = null, IEnumerable<IPublishedContent> Children = null, IEnumerable<IPublishedProperty> properties = null, int? index = null)
        {

            var contentMock = UmbracoUnitTestHelper.SetPublishedContentMock(
                mock ?? new Mock<IPublishedContent>(),
                name ?? _Fixture.Create<string>(),
                ResolveUnqueContentId(id), path, url ?? _Fixture.Create<string>(),
                templateId, updateDate, createDate
                , contentType, parent, Children, properties, index, PublishedItemType.Media);
            var content = contentMock.Object;

            Media.Add(content);

            return content;
        }

        public void WithCurrentTemplate(string action = null, string controller = null)
        {
            var routeData = NeedsRouteData();
            routeData.Values.Add("action", action ?? _Fixture.Create<string>());
            routeData.Values.Add("controller", controller ?? _Fixture.Create<string>());
            _mocks.Resolve<HttpContextBase>().Setup(s => s.Items).Returns(new Dictionary<string, string>());
            NeedsPublishedContentRequest();
            NeedsCustomViewEngine(); //this also clears the standard view engines
        }

        //TODO add MEMBER

        //TODO add current user (Mock WebSecurity and autofixt/mock IUser) - Use Case : WebApi.Security.CurrentUser.*

        public IUser WithCurrentUser(int? id = null, string name = null, string username = null, string email = null, string comments = null, DateTime? createDate = null, DateTime? updateDate = null, string language = null, bool isApproved = true, bool isLocked = false, int? startContentId = null, int? startMediaId = null)
        {
            var usr = UmbracoUnitTestHelper.GetUser(_mocks.Resolve<IUser>("Current"),
                id ?? _Fixture.Create<int>(),
                name ?? _Fixture.Create<string>(),
                username ?? _Fixture.Create<string>(),
                email ?? _Fixture.Create<string>(),
                comments ?? _Fixture.Create<string>(),
                createDate ?? _Fixture.Create<DateTime>(),
                updateDate ?? _Fixture.Create<DateTime>(),
                language ?? _Fixture.Create<string>(),
                isApproved,
                isLocked,
                startContentId ?? _Fixture.Create<int>(),
                startMediaId ?? _Fixture.Create<int>());

            var userData = _mocks.Resolve<UserData>();
            userData.SetupAllProperties();
            userData.Object.Id = usr.Id;
            userData.Object.RealName = usr.Name;
            userData.Object.Username = usr.Username;
            userData.Object.Culture = usr.Language;
            userData.Object.StartContentNode = usr.StartContentId;
            userData.Object.StartMediaNode = usr.StartMediaId;

            var mockIdentity = _mocks.Resolve<UmbracoBackOfficeIdentity>(null, userData.Object);
            mockIdentity.SetupAllProperties();
            mockIdentity.Setup(s => s.IsAuthenticated).Returns(true);

            var mockPricipal = _mocks.Resolve<IPrincipal>();
            mockPricipal.Setup(s => s.Identity).Returns(mockIdentity.Object);

            var httpContextMock = _mocks.Resolve<HttpContextBase>();
            httpContextMock.Setup(s => s.User).Returns(mockPricipal.Object);

            var webSec = _mocks.Resolve<WebSecurity>(null, _mocks.ResolveObject<HttpContextBase>(), null);
            webSec.Setup(s => s.CurrentUser).Returns(usr);
            webSec.Setup(s => s.GetUserId()).Returns(usr.Id);

            return usr;
        }

        /// <summary>
        /// Sets up Security.IsAuthenticated and HttpContext.User.Identity.IsAuthenticated. Calls WithCurrentUser
        /// </summary>
        /// <param name="isAuthed"></param>
        /// <param name=""></param>
        /// <returns></returns>
        public bool WithAuthentication(bool isAuthed = true, int? id = null, string name = null, string username = null, string email = null, string comments = null, DateTime? createDate = null, DateTime? updateDate = null, string language = null, bool isApproved = true, bool isLocked = false)
        {
            if (isAuthed)
            {
                WithCurrentUser(id, name, username, email, comments, createDate, updateDate, language, isApproved, isLocked);
            }
            else
            {
                var mockIdentity = _mocks.Resolve<IIdentity>();
                mockIdentity.Setup(s => s.IsAuthenticated).Returns(isAuthed);

                var mockPricipal = _mocks.Resolve<IPrincipal>();
                mockPricipal.Setup(s => s.Identity).Returns(mockIdentity.Object);

                var httpContextMock = _mocks.Resolve<HttpContextBase>();
                httpContextMock.Setup(s => s.User).Returns(mockPricipal.Object);
            }
            return isAuthed;
        }

        public PublishedContentType WithPublishedContentType(int? id = null, string name = null, string alias = null, IEnumerable<PropertyType> propertyTypes = null)
        {
            alias = alias ?? _Fixture.Create<string>();
            id = id.HasValue ? id : _Fixture.Create<int>(); //use unique ID system?
            name = name ?? _Fixture.Create<string>();
            PublishedContentType contentType = null;
            if ((contentType = this.ContentTypes.FirstOrDefault(c => c.Alias == alias)) != null) //already existings, return
            {
                return contentType;
            }
            if (propertyTypes != null && propertyTypes.Count() > 0) //need boot manager for property types...
                NeedsCoreBootManager();
            mockServiceContext.ContentTypeService.Setup(s => s.GetContentType(alias)).Returns(UmbracoUnitTestHelper.GetContentTypeComposition<IContentType>(alias: alias, name: name, id: id, propertyTypes: propertyTypes));
            contentType = UmbracoUnitTestHelper.GetPublishedContentType(PublishedItemType.Content, alias);
            return contentType;
        }

        public void WithCustomViewEngine(bool clearNonCustom = true, IViewEngine viewEngine = null)
        {
            if (clearNonCustom)
                NeedsClearedViewEngine();
            ViewEngines.Engines.Add(viewEngine);
        }

        private List<IRelation> Relations;
        private List<IRelationType> RelationTypes;

        public IRelation WithRelation(IPublishedContent parent = null, IPublishedContent child = null, string alias = null, int? id = null)
        {
            NeedsRelationsSetup();
            if (parent == null)
                parent = WithPublishedContentPage();
            if (child == null)
                child = WithPublishedContentPage();
            IRelationType relationType = null;
            if(!string.IsNullOrEmpty(alias))
            {
                relationType = this.RelationTypes.FirstOrDefault(r => r.Alias == alias);
            }
            if(relationType == null)
            {
                relationType =  new RelationType(Guid.Empty, Guid.Empty, alias ?? _Fixture.Create<string>());
                relationType.Id = _Fixture.Create<int>();
                RelationTypes.Add(relationType);
            }
            var relation = new Relation(parent.Id, child.Id, relationType);
            relation.Id = id ?? _Fixture.Create<int>();
            this.Relations.Add(relation);
            return relation;
        }

        #region Controller Methods

        public void RegisterController(Controller controller)
        {
            Controller = controller;
            AffectsController(false, ControllerActions.ToArray());
        }

        public void RegisterController(ApiController controller)
        {
            ApiController = controller;
            AffectsController(false, ControllerActions.ToArray());
        }

        #endregion

        #region Needs

        private UmbracoHelper NeedsUmbracoHelper()
        {
            if (_umbHelper == null)
            {
                _umbHelper = UmbracoUnitTestHelper.GetUmbracoHelper(this.UmbracoContext,
                    cultureDictionary: _mocks.ResolveObject<ICultureDictionary>(),
                    content: _mocks.ResolveObject<IPublishedContent>("Current"),
                    typedQuery: _mocks.ResolveObject<ITypedPublishedContentQuery>(),
                    dynamicQuery: _mocks.ResolveObject<IDynamicPublishedContentQuery>());
                NeedsTypedQuery();
                NeedsDynamicQuery();
                AffectsController(true, GiveControllerContext, EnsureControllerHasHelper);
            }
            return _umbHelper;
        }

        private void NeedsRelationsSetup()
        {
            if(Relations == null)
            {
                Relations = new List<IRelation>();
                RelationTypes = new List<IRelationType>();

                mockServiceContext.RelationService.Setup(s => s.GetAllRelations())
                    .Returns(this.Relations);

                mockServiceContext.RelationService.Setup(s => s.GetAllRelations(It.IsAny<int[]>()))
                    .Returns<int[]>(ids => this.Relations.Where(r => ids.Contains(r.Id)));

                mockServiceContext.RelationService.Setup(s => s.GetAllRelationsByRelationType(It.IsAny<int>())).Returns<int>(id => this.Relations.Where(r => r.RelationTypeId == id));
                mockServiceContext.RelationService.Setup(s => s.GetByRelationTypeAlias(It.IsAny<string>())).Returns<string>(a => this.Relations.Where(r => r.RelationType.Alias == a));
                mockServiceContext.RelationService.Setup(s => s.GetByRelationTypeId(It.IsAny<int>())).Returns<int>(a => this.Relations.Where(r => r.RelationType.Id == a));

                mockServiceContext.RelationService.Setup(s => s.GetByChild(It.IsAny<IUmbracoEntity>())).Returns<IUmbracoEntity>(u=>this.Relations.Where(r=>u.Id == r.ChildId));
                mockServiceContext.RelationService.Setup(s => s.GetByChildId(It.IsAny<int>())).Returns<int>(c => this.Relations.Where(r => c == r.ChildId));
                mockServiceContext.RelationService.Setup(s => s.GetByParent(It.IsAny<IUmbracoEntity>())).Returns<IUmbracoEntity>(u => this.Relations.Where(r => u.Id == r.ParentId));
                mockServiceContext.RelationService.Setup(s => s.GetByParentId(It.IsAny<int>())).Returns<int>(c => this.Relations.Where(r => c == r.ParentId));

                mockServiceContext.RelationService.Setup(s => s.GetByParentOrChildId(It.IsAny<int>())).Returns<int>(s => this.Relations.Where(r => r.ParentId == s || r.ChildId == s));

                mockServiceContext.RelationService.Setup(s => s.GetByChild(It.IsAny<IUmbracoEntity>(), It.IsAny<string>())).Returns<IUmbracoEntity,string>((u,a) => this.Relations.Where(r => u.Id == r.ChildId && a== r.RelationType.Alias ));
                mockServiceContext.RelationService.Setup(s => s.GetByParent(It.IsAny<IUmbracoEntity>(), It.IsAny<string>())).Returns<IUmbracoEntity,string>((u,a) => this.Relations.Where(r => u.Id == r.ParentId && a == r.RelationType.Alias));

                mockServiceContext.RelationService.Setup(s => s.GetByParentOrChildId(It.IsAny<int>(), It.IsAny<string>())).Returns<int,string>((s,a) => this.Relations.Where(r => (r.ParentId == s || r.ChildId == s) && a == r.RelationType.Alias));

                mockServiceContext.RelationService.Setup(s => s.GetById(It.IsAny<int>())).Returns<int>(id => this.Relations.FirstOrDefault(r => r.Id == id));

                mockServiceContext.RelationService.Setup(s => s.GetRelationTypeByAlias(It.IsAny<string>())).Returns<string>(a => this.RelationTypes.FirstOrDefault(r => r.Alias == a));
                mockServiceContext.RelationService.Setup(s => s.GetRelationTypeById(It.IsAny<int>())).Returns<int>(a => this.RelationTypes.FirstOrDefault(r => r.Id == a));

                //TODO Find a way to return content instead. We don't currently work with IUmbracoEntities  , so that will be new
                mockServiceContext.RelationService.Setup(s => s.GetEntitiesFromRelation(It.IsAny<IRelation>(),It.IsAny<bool>())).Throws<NotImplementedException>();
                mockServiceContext.RelationService.Setup(s => s.GetEntitiesFromRelations(It.IsAny<IEnumerable<IRelation>>(), It.IsAny<bool>())).Throws<NotImplementedException>();
                mockServiceContext.RelationService.Setup(s => s.GetParentEntitiesFromRelations(It.IsAny<IEnumerable<IRelation>>(), It.IsAny<bool>())).Throws<NotImplementedException>();
                mockServiceContext.RelationService.Setup(s => s.GetParentEntityFromRelation(It.IsAny<IRelation>(), It.IsAny<bool>())).Throws<NotImplementedException>();
            }
        }

        private RouteData NeedsRouteData()
        {
            if (_routeData == null)
                _routeData = new RouteData();
            return _routeData;
        }

        private HttpRouteData NeedsHttpRouteData()
        {
            if (_httpRouteData == null)
            {
                _httpRouteData = new HttpRouteData(_mocks.ResolveObject<IHttpRoute>(), new HttpRouteValueDictionary());
            }
            return _httpRouteData;
        }

        private PublishedContentRequest NeedsPublishedContentRequest()
        {
            if (_publishedContentRequest == null)
            {
                _publishedContentRequest = UmbracoUnitTestHelper.GetPublishedContentRequest(UmbracoContext, currentContent: _mocks.ResolveObject<IPublishedContent>("Current"));
                UmbracoContext.PublishedContentRequest = _publishedContentRequest;
            }
            return _publishedContentRequest;
        }

        private bool typedQuerySetup = false;
        private void NeedsTypedQuery()
        {
            if (!typedQuerySetup)
            {
                var mock =
                    _mocks.Resolve<ITypedPublishedContentQuery>();

                mock.Setup(s => s.TypedContent(It.IsAny<int>())).Returns<int>(id =>
                      this.Content.FirstOrDefault(c => c.Id == id)
                    );

                mock.Setup(s => s.TypedContent(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(ids =>
                      this.Content.Where(c => ids.Contains(c.Id))
                  );

                mock.Setup(s => s.TypedContentAtRoot()).Returns(() => this.Content.Where(s => s.Level == 1));

                mock.Setup(s => s.TypedMedia(It.IsAny<int>())).Returns<int>(id =>
                    this.Media.FirstOrDefault(c => c.Id == id)
                );

                mock.Setup(s => s.TypedMedia(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(ids =>
                    this.Media.Where(c => ids.Contains(c.Id))
                );

                mock.Setup(s => s.TypedMediaAtRoot()).Returns(() => this.Media.Where(s => s.Level == 1));

                //TODO typed search

                typedQuerySetup = true;
            }
        }

        private bool dynamicQuerySetup = false;
        private void NeedsDynamicQuery()
        {
            if (!dynamicQuerySetup)
            {
                var mock =
                    _mocks.Resolve<IDynamicPublishedContentQuery>();

                mock.Setup(s => s.Content(It.IsAny<int>())).Returns<int>(id =>
                      this.Content.FirstOrDefault(c => c.Id == id)
                    );

                mock.Setup(s => s.Content(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(ids =>
                      this.Content.Where(c => ids.Contains(c.Id))
                  );

                mock.Setup(s => s.ContentAtRoot()).Returns(() => this.Content.Where(s => s.Level == 1));

                mock.Setup(s => s.Media(It.IsAny<int>())).Returns<int>(id =>
                    this.Media.FirstOrDefault(c => c.Id == id)
                );

                mock.Setup(s => s.Media(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(ids =>
                    this.Media.Where(c => ids.Contains(c.Id))
                );

                mock.Setup(s => s.MediaAtRoot()).Returns(() => this.Media.Where(s => s.Level == 1));

                //TODO typed search

                dynamicQuerySetup = true;
            }
        }

        private void NeedsCoreBootManager()
        {
            if (!_boot.Initialized)
                _boot.Initialize();
            if (!_boot.Started)
                _boot.Startup(null);
            if (!_boot.Completed)
                _boot.Complete(null);
        }

        private void NeedsClearedViewEngine()
        {
            if (!_viewEngineCleared)
            {
                _viewEngineCleared = true;
                ViewEngines.Engines.RemoveAll((s) => true);
            }
        }

        private void NeedsCustomViewEngine()
        {
            if (!ViewEngines.Engines.Any(c => c.GetType() == typeof(CustomViewEngine)))
                WithCustomViewEngine(true, new CustomViewEngine());
        }

        #endregion

        #region Affects
        private void AffectsController(bool store, params Action[] actions)
        {
            foreach (var action in actions)
            {
                if (!store || ControllerActions.Add(action))//add the action
                    if (HasAnyController) //only preform the action if it is new and the controller exists
                        action();
            }
        }

        #endregion

        #region Give
        private void GiveControllerContext()
        {
            if (HasAnyController)
            {
                if (HasApiController)
                {
                    if (ApiController.ControllerContext == null)
                        ApiController.ControllerContext = UmbracoUnitTestHelper.GetApiControllerContext(NeedsHttpRouteData());
                }
                else if (HasMvcController)
                {
                    if (Controller.ControllerContext == null)
                        Controller.ControllerContext = UmbracoUnitTestHelper.GetControllerContext(UmbracoContext, Controller, NeedsPublishedContentRequest(), NeedsRouteData());
                }
            }
        }
        #endregion

        #region Ensure
        private void EnsureControllerHasHelper()
        {
            if (HasAnyController)
            {
                UmbracoHelper _assignedHelper = null;
                if (HasApiController)
                {
                    if (ApiController is UmbracoApiController)
                        _assignedHelper = ((UmbracoApiController)ApiController).Umbraco;
                }
                else if (HasMvcController)
                {
                    if (Controller is RenderMvcController)
                        _assignedHelper = ((RenderMvcController)Controller).Umbraco;
                    else if (Controller is SurfaceController)
                        _assignedHelper = ((SurfaceController)Controller).Umbraco;
                }
                if (_assignedHelper != UmbracoHelper) // should be our object!!
                {
                    throw new Exception(string.Format("{0} must implement and use base constructor which takes in the Umrabco Helper. This allows the Umbraco Helper with mocked data to be passed in.", Controller.GetType().Name));//make a better excpetion class?
                }
            }
        }

        private void GiveRenderMvcControllerPublishedContextRouteData()
        {
            if (HasMvcController && Controller is RenderMvcController)
            {
                var routeData = NeedsRouteData();
                routeData.DataTokens.Add(UmbConstants.Web.PublishedDocumentRequestDataToken, NeedsPublishedContentRequest()); //changed to local constant to allow for 7.4.0
                AffectsController(false, GiveControllerContext);
            }
        }

        #endregion

        private int ResolveUnqueContentId(int? id = null, bool errorOnDuplicateProvided = true)
        {
            if (id.HasValue)
                if (contentIdCollection.Add(id.Value) || !errorOnDuplicateProvided || !EnforceUniqueContentIds)
                {
                    return id.Value;
                }
                else
                {
                    throw new Exception("Duplicate ID provided. Enforcement of Unique Ids can be disabled using the EnforceUniqueContentIds paramater. If unique ID enforcement is prefered, generally providing a null ID will assign a random ID");
                }
            do
            {
                id = _rand.Next();
            } while (!contentIdCollection.Add(id.Value));
            return id.Value;
        }

        public void Dispose()
        {
            UmbracoUnitTestHelper.CleanupCoreBootManager(this.ApplicationContext);
        }



    }
}
