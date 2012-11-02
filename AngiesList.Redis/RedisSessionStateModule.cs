using System;
using System.Linq;
using System.Web;
using System.Web.SessionState;
using System.Collections;
using System.Threading;
using System.Configuration;
using BookSleeve;
using System.Threading.Tasks;

namespace AngiesList.Redis
{

	public sealed class RedisSessionStateModule : IHttpModule, IDisposable
	{
		private bool initialized = false;
		private bool releaseCalled = false;
		private ISessionIDManager sessionIDManager;

		private RedisConnection redisConnection;
        private RedisSessionStateConfiguration redisConfig;


		public void Init(HttpApplication app)
		{
			if (!initialized) {
				lock (typeof(RedisSessionStateModule)) {
					if (!initialized) {

                        redisConfig = RedisSessionStateConfiguration.GetConfiguration();
			
						// Add event handlers.
						app.AcquireRequestState += new EventHandler(this.OnAcquireRequestState);
						app.ReleaseRequestState += new EventHandler(this.OnReleaseRequestState);
						app.EndRequest += new EventHandler(this.OnEndRequest);

						// Create a SessionIDManager.
						sessionIDManager = new SessionIDManager();
						sessionIDManager.Initialize();
						
						redisConnection = new RedisConnection(redisConfig.Host, redisConfig.Port);

						initialized = true;
					}
				}
			}
		}

		private RedisConnection GetRedisConnection()
		{
			if (redisConnection.NeedsReset()) {
				lock (typeof(RedisSessionStateModule)) {
					if (redisConnection.NeedsReset()) {

                        redisConnection = new RedisConnection(redisConfig.Host, redisConfig.Port);
						redisConnection.Closed += (object sender, EventArgs e) => {
							//Debug.WriteLine("redisConnection closed");
						};
						redisConnection.Open();
					}
				}
			}
			return redisConnection;
		}


		public void Dispose()
		{
			redisConnection.Dispose();
		}

		private bool RequiresSessionState(HttpContextBase context)
		{
			if (context.Session != null && (context.Session.Mode == null || context.Session.Mode == SessionStateMode.Off)) {
				return false;
			}
			return (context.Handler is IRequiresSessionState ||
				context.Handler is IReadOnlySessionState);
		}

		private void OnAcquireRequestState(object source, EventArgs args)
		{
			HttpApplication app = (HttpApplication)source;
			HttpContext context = app.Context;
			bool isNew = false;
			string sessionId;

            string appName = AppDomain.CurrentDomain.FriendlyName;

			RedisSessionItemHash sessionItemCollection = null;
			bool supportSessionIDReissue = true;

			sessionIDManager.InitializeRequest(context, false, out supportSessionIDReissue);
			sessionId = sessionIDManager.GetSessionID(context);

			if (sessionId == null) {
				bool redirected, cookieAdded;

				sessionId = sessionIDManager.CreateSessionID(context);
				sessionIDManager.SaveSessionID(context, sessionId, out redirected, out cookieAdded);

				isNew = true;

				if (redirected)
					return;
			}

			if (!RequiresSessionState(new HttpContextWrapper(context))) { return; }

			releaseCalled = false;

			sessionItemCollection = new RedisSessionItemHash(sessionId, appName, redisConfig.SessionTimeout, GetRedisConnection());

			if (sessionItemCollection.Count == 0) {
				isNew = true;
			}
			
			// Add the session data to the current HttpContext.
			SessionStateUtility.AddHttpSessionStateToContext(context,
								  new TaskWaitRedisHttpSessionStateContainer(sessionId,
																		 sessionItemCollection,
																		 SessionStateUtility.GetSessionStaticObjects(context),
																		 redisConfig.SessionTimeout,
																		 isNew,
																		 redisConfig.CookieMode,
																		 SessionStateMode.Custom,
																		 false));

			// Execute the Session_OnStart event for a new session.
			if (isNew && Start != null) {
				Start(this, EventArgs.Empty);
			}
		}

		public event EventHandler Start;

		private void OnReleaseRequestState(object source, EventArgs args)
		{
			HttpApplication app = (HttpApplication)source;
			HttpContext context = app.Context;

			if (context == null || context.Session == null) { return; }

			releaseCalled = true;

			// Read the session state from the context
			var stateContainer =
			  (TaskWaitRedisHttpSessionStateContainer)SessionStateUtility.GetHttpSessionStateFromContext(context);

			// If Session.Abandon() was called, remove the session data from the local Hashtable
			// and execute the Session_OnEnd event from the Global.asax file.
			if (stateContainer.IsAbandoned) {
				stateContainer.Clear();
				SessionStateUtility.RaiseSessionEnd(stateContainer, this, EventArgs.Empty);
			}
			else {
				stateContainer.WaitOnAllPersistent();
			}

			SessionStateUtility.RemoveHttpSessionStateFromContext(context);
		}

		private void OnEndRequest(object source, EventArgs eventArgs)
		{
			if (!releaseCalled) {
				OnReleaseRequestState(source, eventArgs);
			}
		}
		
	}
}