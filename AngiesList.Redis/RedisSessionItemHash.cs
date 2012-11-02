using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.SessionState;
using BookSleeve;
using System.Threading;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;

namespace AngiesList.Redis
{
	public sealed class RedisSessionItemHash : NameObjectCollectionBase,  ISessionStateItemCollection
	{
		private RedisConnection redis;
		private string sessionId;
        private string prefix;
		private int timeoutMinutes;

		private IValueSerializer serializer = new ClrBinarySerializer();

		private IDictionary<string, object> persistentValues = new Dictionary<string, object>();
		private object deserializeLock = new object();

		private const string TYPE_PREFIX = "__CLR_TYPE__";
		private const string VALUE_PREFIX = "val:";

		public RedisSessionItemHash(string sessionId, string appName, int timeoutMinutes, RedisConnection redisConnection)
			: base()
		{
			this.sessionId = sessionId;
            this.prefix = VALUE_PREFIX + appName + ":";
			this.timeoutMinutes = timeoutMinutes;
			this.redis = redisConnection;
			SetTasks = new List<Task>();
		}

		private string GetKeyForSession()
		{
			return "sess:" + sessionId;
		}

		private Dictionary<string, byte[]> rawItems;
		private Dictionary<string, byte[]> GetRawItems()
		{
			if (rawItems == null) {
				rawItems = redis.Hashes.GetAll(0, GetKeyForSession()).Result;
				OneTimeResetTimeout();
			}
			return rawItems;
		}

		private bool keysAdded;
		private void AddKeysToBase()
		{
			if (!keysAdded) {
				string dePrefixedName;
				foreach (var name in GetRawItems().Keys) {
					if (!name.StartsWith(TYPE_PREFIX)) {
						dePrefixedName = name.Substring(prefix.Length);
						BaseAdd(dePrefixedName, null);
					}
				}
				keysAdded = true;
			}
		}

		private void AddFieldToBaseFromRaw(string name)
		{
			AddKeysToBase();
			lock (deserializeLock) {
				if (!GetRawItems().ContainsKey(prefix + name)) { return; }

				//var typeName = Encoding.ASCII.GetString(GetRawItems()[TYPE_PREFIX + name]);
				//var fieldClrType = Type.GetType(typeName);

				var bytes = GetRawItems()[prefix + name];

				var valueToAdd = serializer.Deserialize(bytes);
				var persistentCopy = serializer.Deserialize(bytes);

				BaseSet(name, valueToAdd);
				persistentValues.Add(name, persistentCopy);
			}
		}

		private void AddAllFieldsToBaseFromRaw() 
		{
			AddKeysToBase();
			lock (deserializeLock) {
				foreach (var name in BaseGetAllKeys()) {
					Get(name);
				}
			}
		}

		private HashSet<string> namesAdded = new HashSet<string>();
		private object Get(string name)
		{
			if (!namesAdded.Contains(name)) {
				AddFieldToBaseFromRaw(name);
				namesAdded.Add(name);
			}
			return BaseGet(name);
		}

		private void Set(string name, object value)
		{
			if (value != null && namesAdded.Contains(name)) {
				if (value.Equals(persistentValues.ContainsKey(name) ? persistentValues[name] : null)) {
					return;
				}
			}
			var bytes = serializer.Serialize(value);

			byte[] storedBytes;
			if (GetRawItems().TryGetValue(prefix + name, out storedBytes) &&
				bytes.SequenceEqual(storedBytes)) {
					return;
			}

			var itemsToSet = new Dictionary<string, byte[]>(1);
			itemsToSet.Add(prefix+name, bytes);
			//itemsToSet.Add(TYPE_PREFIX+name, Encoding.ASCII.GetBytes(value.GetType().AssemblyQualifiedName));
			var setTask = redis.Hashes.Set(0, GetKeyForSession(), itemsToSet);
			SetTasks.Add(setTask);

			OneTimeResetTimeout();

			if (rawItems.ContainsKey(prefix + name)) {
				rawItems[prefix + name] = bytes;
			}
			else {
				rawItems.Add(prefix + name, bytes);
			}

			if (persistentValues.ContainsKey(name)) {
				persistentValues.Remove(name);
			}
			var persistentCopy = serializer.Deserialize(bytes);
			persistentValues.Add(name, persistentCopy);

			if (!namesAdded.Contains(name)) {
				namesAdded.Add(name);
				if (keysAdded && BaseGetAllKeys().Contains(name)) {
					BaseSet(name, value);
				}
				else {
					BaseAdd(name, value);
				}
			}
			else {
				BaseSet(name, value);
			}
		}

		internal void PersistChangedReferences()
		{
			var itemsToTryPersist = new Dictionary<string, object>();
			foreach (var name in BaseGetAllKeys()) {
				if (namesAdded.Contains(name)) {
					var item = BaseGet(name);
					if (item is ValueType) { continue; }
					itemsToTryPersist.Add(name, item);
				}
			}
			foreach (var pair in itemsToTryPersist) {
				Set(pair.Key, pair.Value);
			}
		}

		private bool timeoutReset;
		private void OneTimeResetTimeout()
		{
			if (!timeoutReset) {
				redis.Keys.Expire(0, GetKeyForSession(), timeoutMinutes * 60);
				timeoutReset = true;
			}
		}

		public object this[string name]
		{
			get {
				return Get(name);
			}
			set {
				Set(name, value);
			}
		}

		public void Clear()
		{
			redis.Keys.Remove(0, GetKeyForSession());
			BaseClear();
		}

		public void Remove(string name)
		{
			redis.Hashes.Remove(0, GetKeyForSession(), name);
			BaseRemove(name);
		}

		public override int Count
		{
			get {
				AddKeysToBase();
				return base.Count;
			}
		}

		public override NameObjectCollectionBase.KeysCollection Keys
		{
			get {
				AddKeysToBase();
				var copiedCollection = new NameValueCollection();
				foreach (var key in BaseGetAllKeys())
				{
					copiedCollection.Add(key, null);
				}

				return copiedCollection.Keys;
			}
		}

		public override IEnumerator GetEnumerator()
		{
			AddAllFieldsToBaseFromRaw();
			return base.GetEnumerator();
		}

		public bool Dirty
		{
			get { return false; }
			set { }
		}

		public IList<Task> SetTasks { get; private set; }

		//public bool IsSynchronized
		//{
		//   get { return false; }
		//}

		//private object syncRoot;
		//public object SyncRoot
		//{
		//   get
		//   {
		//      if (syncRoot == null) {
		//         Interlocked.CompareExchange(ref syncRoot, new object(), null);
		//      }
		//      return syncRoot;
		//   }
		//}

		public void RemoveAt(int index)
		{
			throw new NotImplementedException();
		}

		public object this[int index]
		{
			get
			{
				throw new NotImplementedException();
			}
			set
			{
				throw new NotImplementedException();
			}
		}

	}
}
