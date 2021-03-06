using System;
using System.Collections.Generic;
using System.Linq;
using SQLite;
using System.Reflection;
using System.Collections;
using System.Threading.Tasks;
//using Java.Lang;
using System.Threading;
using System.Diagnostics;
using System.Linq.Expressions;

namespace SimpleDatabase
{
	public class SimpleDatabaseConnection
	{
		Dictionary<Tuple<Type, string>, Dictionary<int, Dictionary<int, Object>>> MemoryStore = new Dictionary<Tuple<Type, string>, Dictionary<int, Dictionary<int, object>>>();
		Dictionary<Type, Dictionary<object, object>> ObjectsDict = new Dictionary<Type, Dictionary<object, object>>();
		//Dictionary<Type,List<object>> Objects = new Dictionary<Type, List<object>> ();
		Dictionary<Tuple<Type, string>, List<SimpleDatabaseGroup>> Groups = new Dictionary<Tuple<Type, string>, List<SimpleDatabaseGroup>>();
		Dictionary<Type, GroupInfo> GroupInfoDict = new Dictionary<Type, GroupInfo>();
		object groupLocker = new object();
		object memStoreLocker = new object();
		object writeLocker = new object();
		SQLiteConnection connection;
		public SimpleDatabaseConnection(SQLiteConnection sqliteConnection)
		{
			connection = sqliteConnection;
			init();
		}
		public SimpleDatabaseConnection(string databasePath)
		{
			connection = new SQLiteConnection(databasePath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex | SQLiteOpenFlags.Create, true);
			connection.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
			init();
		}

		void init()
		{
#if iOS
			Foundation.NSNotificationCenter.DefaultCenter.AddObserver((Foundation.NSString)"UIApplicationDidReceiveMemoryWarningNotification",delegate{
				ClearMemory();
			});
#endif
		}

		public void MakeClassInstant<T>(GroupInfo info)
		{
			MakeClassInstant(typeof(T), info);
		}

		public void MakeClassInstant<T>()
		{
			var t = typeof(T);
			MakeClassInstant(t);
		}

		public void MakeClassInstant(Type type)
		{
			MakeClassInstant(type, null);

		}

		public void MakeClassInstant(Type type, GroupInfo info)
		{
			if (info == null)
				info = GetGroupInfo(type);
			SetGroups(type, info);
			FillGroups(type, info);
			if (Groups[new Tuple<Type, string>(type, info.ToString())].Count() == 0)
				SetGroups(type, info);

		}

		public GroupInfo GetGroupInfo<T>()
		{
			return GetGroupInfo(typeof(T));
		}

		public GroupInfo GetGroupInfo(Type type)
		{
			if (GroupInfoDict.ContainsKey(type))
				return GroupInfoDict[type];
			bool groupDesc = false;
			var groupBy = GetGroupByProperty(type, out groupDesc);
			bool desc = false;
			var orderBy = GetOrderByProperty(type, out desc);
			var groupInfo = new GroupInfo();
			if (groupBy != null)
			{
				groupInfo.GroupBy = groupBy.Name;
				groupInfo.GroupOrderByDesc = groupDesc;
			}
			if (orderBy != null)
			{
				groupInfo.OrderBy = orderBy.Name;
				groupInfo.OrderByDesc = desc;
			}
			GroupInfoDict.Add(type, groupInfo);
			return groupInfo;
		}

		private void SetGroups(Type type, GroupInfo groupInfo)
		{
			List<SimpleDatabaseGroup> groups = CreateGroupInfo(type, groupInfo);

			var tuple = new Tuple<Type, string>(type, groupInfo.ToString());
			lock (groupLocker)
			{
				if (Groups.ContainsKey(tuple))
					Groups[tuple] = groups;
				else
					Groups.Add(tuple, groups);
			}

		}

		private List<SimpleDatabaseGroup> CreateGroupInfo(Type type, GroupInfo groupInfo)
		{
			List<SimpleDatabaseGroup> groups;
			if (string.IsNullOrEmpty(groupInfo.GroupBy))
				groups = new List<SimpleDatabaseGroup>() { new SimpleDatabaseGroup { GroupString = "" } };
			else
			{
				var query = $"select distinct {groupInfo.GroupBy} as GroupString from {groupInfo.FromString(GetTableName(type))} {groupInfo.FilterString(true)} {groupInfo.OrderByString(true)} {groupInfo.LimitString()}";
				var queryInfo = groupInfo.ConvertSqlFromNamed(query);
				groups = connection.Query<SimpleDatabaseGroup>(queryInfo.Item1, queryInfo.Item2.ToArray()).ToList();
			}

			for (int i = 0; i < groups.Count(); i++)
			{
				var group = groups[i];
				group.ClassName = type.Name;
				group.Filter = groupInfo.Filter ?? "";
				group.GroupBy = groupInfo.GroupBy ?? "";
				group.OrderBy = groupInfo.OrderBy ?? "";
				group.Order = i;
				string rowQuery;
				if (string.IsNullOrEmpty(groupInfo.GroupBy))
					rowQuery = $"select count(*) from {groupInfo.FromString(GetTableName(type))} {groupInfo.FilterString(true)}";
				else
					rowQuery = $"select count(*) from {groupInfo.FromString(GetTableName(type))} where {groupInfo.GroupBy} = @GroupByParam {groupInfo.FilterString(false)}";
				var queryInfo = groupInfo.ConvertSqlFromNamed(rowQuery, new Dictionary<string, object> { { "@GroupByParam", group.GroupString } });
				group.RowCount = connection.ExecuteScalar<int>(queryInfo.Item1, queryInfo.Item2);
				//}
				if (groupInfo.Limit > 0)
					group.RowCount = Math.Min(group.RowCount, groupInfo.Limit);
			}
			return groups;
		}
		string GetTableName(Type type)
		{
			var tableInfo = GetTableMapping(connection, type);
			return tableInfo.TableName;
		}
		public void UpdateInstant<T>(GroupInfo info)
		{
			UpdateInstant(typeof(T), info);
		}

		public void UpdateInstant<T>()
		{
			UpdateInstant(typeof(T));
		}

		public void UpdateInstant(Type type)
		{
			UpdateInstant(type, null);
		}

		public void UpdateInstant(Type type, GroupInfo info)
		{
			if (info == null)
				info = GetGroupInfo(type);
			var tuple = new Tuple<Type, string>(type, info.ToString());
			lock (memStoreLocker)
			{
				if (MemoryStore.ContainsKey(tuple))
				{
					MemoryStore[tuple] = new Dictionary<int, Dictionary<int, object>>();
				}
			}
			FillGroups(type, info);

		}

		public void ClearMemory()
		{
			lock (memStoreLocker)
			{

				ObjectsDict.Clear();
				ClearMemoryStore();
				cacheQueue.Clear();
			}
		}
		public void ClearMemoryStore()
		{
			lock (memStoreLocker)
			{
				MemoryStore.Clear();
				lock (groupLocker)
				{
					Groups.Clear();
					GroupInfoDict.Clear();
				}
			}
		}
		public void ClearMemory<T>()
		{
			var t = typeof(T);
			ClearMemory(t);

		}

		public void ClearMemory(params Type[] types)
		{
			lock (memStoreLocker)
			{
				var toRemove = MemoryStore.Where(x => types.Contains(x.Key.Item1)).ToArray();
				foreach (var item in toRemove)
				{
					MemoryStore.Remove(item.Key);
				}
			}
			lock (groupLocker)
			{
				Groups.Clear();
			}
		}

		public void ClearMemory<T>(GroupInfo groupInfo)
		{
			var t = typeof(T);
			ClearMemory(t, groupInfo);
		}
		public void ClearMemory(Type type, GroupInfo groupInfo)
		{
			var tuple = new Tuple<Type, string>(type, groupInfo.ToString());
			lock (memStoreLocker)
			{
				MemoryStore.Remove(tuple);
			}
			lock (groupLocker)
			{
				Groups.Clear();
			}
		}

		public string SectionHeader<T>(int section)
		{
			return SectionHeader<T>(GetGroupInfo(typeof(T)), section);
		}

		public string SectionHeader<T>(GroupInfo info, int section)
		{
			if (info == null)
				info = GetGroupInfo<T>();

			lock (groupLocker)
			{
				var t = typeof(T);
				var tuple = new Tuple<Type, string>(t, info.ToString());
				if (!Groups.ContainsKey(tuple) || Groups[tuple].Count <= section)
					FillGroups(t, info);
				try
				{
					return Groups[tuple][section].GroupString;
				}
				catch (Exception ex)
				{
					return "";
				}
			}
		}

		public string[] QuickJump<T>()
		{
			return QuickJump<T>(GetGroupInfo<T>());
		}

		public string[] QuickJump<T>(GroupInfo info)
		{
			if (info == null)
				info = GetGroupInfo<T>();
			lock (groupLocker)
			{
				var t = typeof(T);
				var tuple = new Tuple<Type, string>(t, info.ToString());
				if (!Groups.ContainsKey(tuple))
					FillGroups(t, info);
				var groups = Groups[tuple];
				var strings = groups.Select(x => string.IsNullOrEmpty(x.GroupString) ? "" : x.GroupString[0].ToString()).ToArray();
				return strings;
			}
		}

		public int NumberOfSections<T>()
		{
			return NumberOfSections<T>(GetGroupInfo<T>());
		}

		public int NumberOfSections<T>(GroupInfo info)
		{
			if (info == null)
				info = GetGroupInfo<T>();
			lock (groupLocker)
			{
				var t = typeof(T);
				var tuple = new Tuple<Type, string>(t, info.ToString());
				if (!Groups.ContainsKey(tuple))
					FillGroups(t, info);
				return Groups[tuple].Count;
			}
		}

		public int RowsInSection<T>(int section)
		{
			return RowsInSection<T>(GetGroupInfo<T>(), section);
		}

		public int RowsInSection<T>(GroupInfo info, int section)
		{
			if (info == null)
				info = GetGroupInfo<T>();
			lock (groupLocker)
			{
				var group = GetGroup<T>(info, section);
				return group.RowCount;
			}
		}

		private SimpleDatabaseGroup GetGroup<T>(int section)
		{
			return GetGroup<T>(GetGroupInfo<T>(), section);
		}

		private SimpleDatabaseGroup GetGroup<T>(GroupInfo info, int section)
		{
			return GetGroup(typeof(T), info, section);

		}

		private SimpleDatabaseGroup GetGroup(Type t, GroupInfo info, int section)
		{

			var tuple = new Tuple<Type, string>(t, info.ToString());
			List<SimpleDatabaseGroup> group = null;
			int count = 0;
			while ((group == null || group.Count <= section) && count < 5)
			{
				if (count > 0)
					Debug.WriteLine("Trying to fill groups: {0}", count);
				lock (groupLocker)
				{
					Groups.TryGetValue(tuple, out group);
				}
				if (group == null)
				{
					FillGroups(t, info);
				}

				count++;
			}
			if (group == null || section >= group.Count)
				return new SimpleDatabaseGroup();
			return group[section];

		}

		private void FillGroups(Type t, GroupInfo info)
		{
			List<SimpleDatabaseGroup> groups;
			groups = CreateGroupInfo(t, info);
			lock (groupLocker)
			{
				var tuple = new Tuple<Type, string>(t, info.ToString());
				Groups[tuple] = groups;
			}

		}

		public T ObjectForRow<T>(int section, int row) where T : new()
		{
			return ObjectForRow<T>(GetGroupInfo(typeof(T)), section, row);
		}

		public T ObjectForRow<T>(GroupInfo info, int section, int row) where T : new()
		{
			if (info == null)
				info = GetGroupInfo<T>();
			lock (memStoreLocker)
			{
				var type = typeof(T);
				var tuple = new Tuple<Type, string>(type, info.ToString());
				if (MemoryStore.ContainsKey(tuple))
				{
					var groups = MemoryStore[tuple];
					if (groups.ContainsKey(section))
					{
						var g = groups[section];
						if (g.ContainsKey(row))
							return (T)groups[section][row];
					}
				}

				Precache<T>(info, section);
				return getObject<T>(info, section, row);
			}
		}

		public T GetObject<T>(object primaryKey) where T : new()
		{
			try
			{
				var type = typeof(T);
				if (!ObjectsDict.ContainsKey(type))
					ObjectsDict[type] = new Dictionary<object, object>();
				if (ObjectsDict[type].ContainsKey(primaryKey))
					return (T)ObjectsDict[type][primaryKey];
				//Debug.WriteLine("object not in objectsdict");
				var pk = GetTableMapping(connection, type);
				var query = $"select * from {pk.TableName} where {pk.PK.Name} = ? ";

				T item = connection.Query<T>(query, primaryKey).FirstOrDefault();

				return item != null ? GetIfCached(item) : item;
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
				return default(T);
			}

		}

		private T getObject<T>(GroupInfo info, int section, int row) where T : new()
		{
			try
			{
				T item;
				var t = typeof(T);
				var group = GetGroup<T>(info, section);

				string query;
				if (string.IsNullOrEmpty(info.GroupBy))
					query = $"select * from {info.FromString(GetTableName(t))} {info.FilterString(true)} {info.OrderByString(true)} LIMIT {row}, 1";
				else
					query = $"select * from {info.FromString(GetTableName(t))} where {info.GroupBy} = @GroupByParam {info.FilterString(false)} {info.OrderByString(true)} LIMIT @LimitParam , 1";
				var queryInfo = info.ConvertSqlFromNamed(query, new Dictionary<string, object> {
					{"@GroupByParam",group.GroupString},
					{"@LimitParam", row }
				});
				item = connection.Query<T>(queryInfo.Item1, queryInfo.Item2).FirstOrDefault();

				if (item == null)
					return new T();

				var tuple = new Tuple<Type, string>(t, info.ToString());
				lock (memStoreLocker)
				{
					if (!MemoryStore.ContainsKey(tuple))
						MemoryStore.Add(tuple, new Dictionary<int, Dictionary<int, object>>());
					var groups = MemoryStore[tuple];
					if (!groups.ContainsKey(section))
						groups.Add(section, new Dictionary<int, object>());
					if (!groups[section].ContainsKey(row))
						groups[section].Add(row, item);
					else
						groups[section][row] = item;
					return GetIfCached(item);
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
				return default(T);
			}

		}
		public void AddObjectToDict(object item, Type t)
		{
			lock (groupLocker)
			{
				var primaryKey = GetTableMapping(connection, t);
				if (primaryKey == null)
					return;
				object pk = primaryKey.PK.GetValue(item);
				if (!ObjectsDict.ContainsKey(t))
					ObjectsDict.Add(t, new Dictionary<object, object>());
				ObjectsDict[t][pk] = item;
				//				if (!Objects.ContainsKey (t))
				//					Objects.Add (t, new List<object> ());
				//				if (!Objects [t].Contains (item))
				//					Objects [t].Add (item);
			}
		}

		public void AddObjectToDict(object item)
		{
			AddObjectToDict(item, item.GetType());
		}
		public void RemoveObjectFromDict(object item)
		{
			RemoveObjectFromDict(item, item.GetType());
		}

		public void RemoveObjectFromDict(object item, Type t)
		{
			lock (groupLocker)
			{
				var primaryKey = GetTableMapping(connection, t);
				if (primaryKey == null)
					return;
				object pk = primaryKey.PK.GetValue(item);
				if (ObjectsDict.ContainsKey(t))
					ObjectsDict[t].Remove(pk);
			}
		}

		T GetIfCached<T>(T item)
		{
			lock (groupLocker)
			{
				var t = typeof(T);
				var primaryKey = GetTableMapping(connection, t);
				if (primaryKey == null)
					return item;
				var pk = primaryKey.PK.GetValue(item);

				if (!ObjectsDict.ContainsKey(t))
					ObjectsDict.Add(t, new Dictionary<object, object>());
				object oldItem;
				if (ObjectsDict[t].TryGetValue(pk, out oldItem))
				{
					return (T)oldItem;
				}
				ObjectsDict[t][pk] = item;
				return item;
			}
		}

		public int GetObjectCount<T>()
		{
			return GetObjectCount<T>(null);
		}

		public int GetObjectCount<T>(GroupInfo info)
		{
			if (info == null)
				info = GetGroupInfo<T>();
			var filterString = info.FilterString(true);
			var t = typeof(T);
			string query = $"Select count(*) from {info.FromString(GetTableName(t))} {filterString}";
			var queryInfo = info.ConvertSqlFromNamed(query);
			int count = connection.ExecuteScalar<int>(queryInfo.Item1, queryInfo.Item2);

			if (info.Limit > 0)
				return Math.Min(info.Limit, count);
			return count;
		}
		public int GetDistinctObjectCount<T>(string column)
		{
			return GetDistinctObjectCount<T>(null, column);
		}

		public int GetDistinctObjectCount<T>(GroupInfo info, string column)
		{
			if (info == null)
				info = GetGroupInfo<T>();
			var filterString = info.FilterString(true);
			var t = typeof(T);
			string query = $"Select distinct count({column}) from {info.FromString(GetTableName(t))} {filterString} {info.LimitString()}";
			var queryInfo = info.ConvertSqlFromNamed(query);
			int count = connection.ExecuteScalar<int>(queryInfo.Item1, queryInfo.Item2);

			if (info.Limit > 0)
				return Math.Min(info.Limit, count);
			return count;
		}


		public T GetObjectByIndex<T>(int index, GroupInfo info = null) where T : new()
		{
			T item;
			var t = typeof(T);
			if (info == null)
				info = GetGroupInfo<T>();
			var filterString = info.FilterString(true);
			var query = $"select * from {info.FromString(GetTableName(t))} {filterString} {info.OrderByString(true)} LIMIT {index}, 1";
			var queryInfo = info.ConvertSqlFromNamed(query);
			item = connection.Query<T>(queryInfo.Item1, queryInfo.Item2).FirstOrDefault();

			if (item == null)
				return default(T);
			return GetIfCached(item);
		}
		public List<T> GetObjects<T>(GroupInfo info) where T : new()
		{
			if (info == null)
				info = GetGroupInfo<T>();
			var filterString = info.FilterString(true);
			var t = typeof(T);
			string query = $"Select * from {info.FromString(GetTableName(t))} {filterString} {info.LimitString()}";
			var queryInfo = info.ConvertSqlFromNamed(query);
			return connection.Query<T>(queryInfo.Item1, queryInfo.Item2).ToList();

		}

		public void Precache<T>() where T : new()
		{
			Precache<T>(GetGroupInfo(typeof(T)));
		}

		public void Precache<T>(GroupInfo info) where T : new()
		{
			return;
			if (info == null)
				info = GetGroupInfo<T>();
			var type = typeof(T);
			var tuple = new Tuple<Type, string>(type, info.ToString());
			FillGroups(type, info);
			lock (groupLocker)
			{
				if (Groups[tuple].Count() == 0)
					SetGroups(type, info);

				foreach (var group in Groups[tuple])
				{
					if (group.Loaded)
						continue;
					cacheQueue.AddLast(delegate
					{
						LoadItemsForGroup<T>(group);
					});
				}
			}
			StartQueue();

		}

		public void Precache<T>(int section) where T : new()
		{
			Precache<T>(GetGroupInfo(typeof(T)), section);
		}

		public void Precache<T>(GroupInfo info, int section) where T : new()
		{
			try
			{
				if (info == null)
					info = GetGroupInfo<T>();
				var type = typeof(T);
				var group = GetGroup(type, info, section);
				cacheQueue.AddFirst(delegate
				{
					LoadItemsForGroup<T>(group);
				});
				StartQueue();
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
			}
		}

		private void LoadItemsForGroup<T>(SimpleDatabaseGroup group) where T : new()
		{
			try
			{
				if (group.Loaded)
					return;
				Debug.WriteLine("Loading items for group");
				var type = typeof(T);
				string query = $"select * from {group.FromString(type.Name)} where {group.GroupBy} = @GroupByParam {group.FilterString(false)} {group.OrderByString(true)} LIMIT @LimitParam , 50";
				List<T> items;
				int current = 0;
				bool hasMore = true;
				while (hasMore)
				{

					if (string.IsNullOrEmpty(group.GroupBy))
						query = $"select * from {group.FromString(type.Name)} {group.FilterString(true)} {group.OrderByString(true)} LIMIT {current}, 50";
					var queryInfo = group.ConvertSqlFromNamed(query, new Dictionary<string, object> {
						{"@GroupByParam",group.GroupString},
						{"@LimitParam", current }
					});
					items = connection.Query<T>(queryInfo.Item1, queryInfo.Item2).ToList();

					{
						Dictionary<int, object> memoryGroup;
						lock (memStoreLocker)
						{
							var tuple = new Tuple<Type, string>(type, group.ToString());
							if (!MemoryStore.ContainsKey(tuple))
							{
								MemoryStore.Add(tuple, new Dictionary<int, Dictionary<int, object>>());
							}

							if (!MemoryStore[tuple].ContainsKey(group.Order))
								try
								{
									MemoryStore[tuple].Add(group.Order, new Dictionary<int, object>());
								}
								catch (Exception ex)
								{
									Debug.WriteLine(ex);
								}
							memoryGroup = MemoryStore[tuple][group.Order];
						}
						for (int i = 0; i < items.Count; i++)
						{
							lock (groupLocker)
							{
								if (memoryGroup.ContainsKey(i + current))
									memoryGroup[i + current] = items[i];
								else
									memoryGroup.Add(i + current, items[i]);
							}
							GetIfCached(items[i]);

						}

					}
					current += items.Count;
					if (current == group.RowCount)
						hasMore = false;
				}
				Debug.WriteLine("group loaded");
				group.Loaded = true;
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
			}
		}

		LinkedList<Action> cacheQueue = new LinkedList<Action>();
		object locker = new object();
		bool queueIsRunning = false;

		private void StartQueue()
		{
			return;
			lock (locker)
			{
				if (queueIsRunning)
					return;
				if (cacheQueue.Count == 0)
					return;
				queueIsRunning = true;
			}
			Task.Run(() => runQueue());
		}

		void runQueue()
		{
			Action action;
			lock (locker)
			{
				if (cacheQueue.Count == 0)
				{
					queueIsRunning = false;
					return;
				}


				try
				{
					//Task.Factory.StartNew (delegate {
					action = cacheQueue.First();
					cacheQueue.Remove(action);
				}
				catch (Exception ex)
				{
					Debug.WriteLine(ex);
					runQueue();
					return;
				}
			}
			if (action != null)
				action();
			//}).ContinueWith (delegate {
			runQueue();
		}


		static Dictionary<Type, Tuple<PropertyInfo, bool>> groupByProperties = new Dictionary<Type, Tuple<PropertyInfo, bool>>();
		static internal PropertyInfo GetGroupByProperty(Type type, out bool desc)
		{
			Tuple<PropertyInfo, bool> property;
			if (groupByProperties.TryGetValue(type, out property))
			{
				desc = property.Item2;
				return property.Item1;
			}
			foreach (var prop in type.GetProperties())
			{
				var attribtues = prop.GetCustomAttributes(false);
				var visibleAtt = attribtues.Where(x => x is GroupByAttribute).FirstOrDefault() as GroupByAttribute;
				if (visibleAtt != null)
				{
					desc = visibleAtt.Descending;
					groupByProperties[type] = new Tuple<PropertyInfo, bool>(prop, desc);
					return prop;
				}
			}
			desc = false;
			return null;
		}


		static Dictionary<Type, Tuple<PropertyInfo, bool>> orderByProperties = new Dictionary<Type, Tuple<PropertyInfo, bool>>();
		internal static PropertyInfo GetOrderByProperty(Type type, out bool desc)
		{
			Tuple<PropertyInfo, bool> property;
			if (orderByProperties.TryGetValue(type, out property))
			{
				desc = property.Item2;
				return property.Item1;
			}
			foreach (var prop in type.GetProperties())
			{
				var attribtues = prop.GetCustomAttributes(false);
				var visibleAtt = attribtues.Where(x => x is OrderByAttribute).FirstOrDefault() as OrderByAttribute;
				if (visibleAtt != null)
				{
					desc = visibleAtt.Descending;
					orderByProperties[type] = new Tuple<PropertyInfo, bool>(prop, desc);
					return prop;
				}
			}
			desc = false;
			return null;
		}

		static Dictionary<Type, TableMapping> cachedTableMappings = new Dictionary<Type, TableMapping>();
		static TableMapping GetTableMapping(SQLiteConnection connection, Type type)
		{
			TableMapping property;
			if (!cachedTableMappings.TryGetValue(type, out property))
				cachedTableMappings[type] = property = connection.GetMapping(type);
			return property;

		}


		#region sqlite

		public int InsertAll(System.Collections.IEnumerable objects)
		{
			var c = 0;
			var types = new HashSet<Type>();
			lock (writeLocker)
			{
				connection.RunInTransaction(() =>
				{
					foreach (var item in objects)
					{
						var i = connection.Insert(item);
						if (i > 0)
						{
							AddObjectToDict(item);
							types.Add(item.GetType());
							c += i;
						}

					}
				});
			}
			if (c > 0)
				ClearMemory(types.ToArray());
			return c;
		}

		/// <summary>
		/// Inserts all specified objects.
		/// </summary>
		/// <param name="objects">
		/// An <see cref="IEnumerable"/> of the objects to insert.
		/// </param>
		/// <param name="extra">
		/// Literal SQL code that gets placed into the command. INSERT {extra} INTO ...
		/// </param>
		/// <returns>
		/// The number of rows added to the table.
		/// </returns>
		public int InsertAll(System.Collections.IEnumerable objects, string extra)
		{
			var c = 0;
			var types = new HashSet<Type>();
			lock (writeLocker)
			{
				connection.RunInTransaction(() =>
				{
					foreach (var item in objects)
					{
						var i = connection.Insert(item, extra);
						if (i > 0)
						{
							AddObjectToDict(item);
							types.Add(item.GetType());
							c += i;
						}
					}
				});
			}
			if (c > 0)
				ClearMemory(types.ToArray());
			return c;
		}

		public Task<int> InsertAllAsync(System.Collections.IEnumerable objects)
		{
			return Task.Run(() => connection.InsertAll(objects));
		}

		public Task<int> InsertAllAsync(System.Collections.IEnumerable objects, string extra)
		{
			return Task.Run(() => connection.InsertAll(objects, extra));
		}

		public AsyncTableQuery<T> TablesAsync<T>()
			where T : new()
		{
			return new AsyncTableQuery<T>(connection.Table<T>());
		}

		public Task<int> InsertAsync(object item)
		{
			return Task.Run(() => Insert(item));
		}
		public Task<int> InsertAsync(object item, string extra)
		{
			return Task.Run(() => Insert(item, extra));
		}


		/// <summary>
		/// Inserts all specified objects.
		/// </summary>
		/// <param name="objects">
		/// An <see cref="IEnumerable"/> of the objects to insert.
		/// </param>
		/// <param name="objType">
		/// The type of object to insert.
		/// </param>
		/// <returns>
		/// The number of rows added to the table.
		/// </returns>
		public int InsertAll(System.Collections.IEnumerable objects, Type objType)
		{
			var c = 0;
			lock (writeLocker)
			{
				connection.RunInTransaction(() =>
				{
					foreach (var item in objects)
					{
						var i = connection.Insert(item, objType);
						if (i > 0)
						{
							AddObjectToDict(item);
							c += i;
						}
					}
				});
			}
			if (c > 0)
				ClearMemory(objType);
			return c;
		}


		/// <summary>
		/// Inserts the given object and retrieves its
		/// auto incremented primary key if it has one.
		/// </summary>
		/// <param name="obj">
		/// The object to insert.
		/// </param>
		/// <returns>
		/// The number of rows added to the table.
		/// </returns>
		public int Insert(object obj)
		{
			var c = connection.Insert(obj);
			if (c > 0)
			{
				ClearMemory(obj.GetType());
				AddObjectToDict(obj);
			}
			return c;
		}

		/// <summary>
		/// Inserts the given object and retrieves its
		/// auto incremented primary key if it has one.
		/// If a UNIQUE constraint violation occurs with
		/// some pre-existing object, this function deletes
		/// the old object.
		/// </summary>
		/// <param name="obj">
		/// The object to insert.
		/// </param>
		/// <returns>
		/// The number of rows modified.
		/// </returns>
		public int InsertOrReplace(object obj)
		{
			var c = connection.InsertOrReplace(obj);
			if (c > 0)
			{
				ClearMemory(obj.GetType());
				AddObjectToDict(obj);
			}
			return c;
		}

		/// <summary>
		/// Inserts the given object and retrieves its
		/// auto incremented primary key if it has one.
		/// </summary>
		/// <param name="obj">
		/// The object to insert.
		/// </param>
		/// <param name="objType">
		/// The type of object to insert.
		/// </param>
		/// <returns>
		/// The number of rows added to the table.
		/// </returns>
		public int Insert(object obj, Type objType)
		{
			var c = connection.Insert(obj, objType);
			if (c > 0)
			{
				ClearMemory(objType);
				AddObjectToDict(obj);
			}
			return c;
		}

		/// <summary>
		/// Inserts the given object and retrieves its
		/// auto incremented primary key if it has one.
		/// If a UNIQUE constraint violation occurs with
		/// some pre-existing object, this function deletes
		/// the old object.
		/// </summary>
		/// <param name="obj">
		/// The object to insert.
		/// </param>
		/// <param name="objType">
		/// The type of object to insert.
		/// </param>
		/// <returns>
		/// The number of rows modified.
		/// </returns>
		public int InsertOrReplace(object obj, Type objType)
		{
			var c = connection.InsertOrReplace(obj, objType);
			if (c > 0)
			{
				ClearMemory(objType);
				AddObjectToDict(obj);
			}
			return c;
		}

		public int InsertOrReplaceAll(System.Collections.IEnumerable objects)
		{
			var c = 0;
			var types = new HashSet<Type>();
			lock (writeLocker)
			{
				connection.RunInTransaction(() =>
				{
					foreach (var item in objects)
					{
						var i = connection.InsertOrReplace(item);
						if (i > 0)
						{
							AddObjectToDict(item);
							types.Add(item.GetType());
							c += i;
						}
					}
				});
			}
			if (c > 0)
				ClearMemory(types.ToArray());
			return c;

		}

		/// <summary>
		/// Inserts all specified objects.
		/// </summary>
		/// <param name="objects">
		/// An <see cref="IEnumerable"/> of the objects to insert.
		/// </param>
		/// <param name="extra">
		/// Literal SQL code that gets placed into the command. INSERT {extra} INTO ...
		/// </param>
		/// <returns>
		/// The number of rows added to the table.
		/// </returns>
		public int InsertOrReplaceAll(System.Collections.IEnumerable objects, Type objType)
		{
			var c = 0;
			lock (writeLocker)
			{
				connection.RunInTransaction(() =>
				{
					foreach (var item in objects)
					{
						var i = connection.InsertOrReplace(item, objType);
						if (i > 0)
						{
							AddObjectToDict(item);
							c += i;
						}
					}
				});
			}
			if (c > 0)
				ClearMemory(objType);
			return c;
		}

		public void RunInTransaction(Action<SQLiteConnection> action)
		{
			lock (writeLocker)
			{
				connection.RunInTransaction(() => action(connection));
			}
		}

		/// <summary>
		/// Inserts the given object and retrieves its
		/// auto incremented primary key if it has one.
		/// </summary>
		/// <param name="obj">
		/// The object to insert.
		/// </param>
		/// <param name="extra">
		/// Literal SQL code that gets placed into the command. INSERT {extra} INTO ...
		/// </param>
		/// <returns>
		/// The number of rows added to the table.
		/// </returns>
		public int Insert(object obj, string extra)
		{
			var c = connection.Insert(obj, extra);
			if (c > 0)
			{
				ClearMemory(obj.GetType());
				AddObjectToDict(obj);
			}
			return c;
		}

		/// <summary>
		/// Inserts the given object and retrieves its
		/// auto incremented primary key if it has one.
		/// </summary>
		/// <param name="obj">
		/// The object to insert.
		/// </param>
		/// <param name="extra">
		/// Literal SQL code that gets placed into the command. INSERT {extra} INTO ...
		/// </param>
		/// <param name="objType">
		/// The type of object to insert.
		/// </param>
		/// <returns>
		/// The number of rows added to the table.
		/// </returns>
		//public int Insert (object obj, string extra, Type objType)
		//{
		//	return connection.Insert (obj,extra,objType);
		//}

		public int Execute(string query, params object[] args)
		{
			return connection.Execute(query, args);
		}

		public Task<int> ExecuteAsync(string query, params object[] args)
		{
			return Task.Run(() => connection.Execute(query, args));
		}

		public List<T> Query<T>(string query, params object[] args) where T : new()
		{
			return connection.Query<T>(query, args);

		}

		public Task<List<T>> QueryAsync<T>(string query, params object[] args) where T : new()
		{
			return Task.Run(() => connection.Query<T>(query, args));

		}

		public int Delete(object objectToDelete)
		{
			var c = connection.Delete(objectToDelete);
			if (c > 0)
			{
				ClearMemory(objectToDelete.GetType());
				RemoveObjectFromDict(objectToDelete);
			}
			return c;
		}

		public int DeleteAll(System.Collections.IEnumerable objects)
		{
			var c = 0;
			var types = new HashSet<Type>();
			lock (writeLocker)
			{
				connection.RunInTransaction(() =>
				{
					foreach (var item in objects)
					{
						var i = connection.Delete(item);
						if (i > 0)
						{
							RemoveObjectFromDict(item);
							types.Add(item.GetType());
							c += i;
						}
					}
				});
			}
			if (c > 0)
				ClearMemory(types.ToArray());
			return c;
		}


		public int DeleteAll(System.Collections.IEnumerable objects, Type type)
		{
			var c = 0;
			lock (writeLocker)
			{
				connection.RunInTransaction(() =>
				{
					foreach (var item in objects)
					{
						var i = connection.Delete(item, type);
						if (i > 0)
						{
							RemoveObjectFromDict(item);
							c += i;
						}
					}
				});
			}
			if (c > 0)
				ClearMemory(type);
			return c;
		}

		public int Update(object obj)
		{
			var c = connection.Update(obj);
			if (c > 0)
			{
				ClearMemory(obj.GetType());
				AddObjectToDict(obj);
			}
			return c;
		}


		public Task<int> UpdateAsync(object obj)
		{
			return Task.Run(() =>
			{
				AddObjectToDict(obj);
				return connection.Update(obj);
			});
		}

		public int UpdateAll(System.Collections.IEnumerable objects)
		{
			var c = 0;
			var types = new HashSet<Type>();
			lock (writeLocker)
			{
				connection.RunInTransaction(() =>
				{
					foreach (var item in objects)
					{
						var i = connection.Update(item);
						if (i > 0)
						{
							AddObjectToDict(item);
							types.Add(item.GetType());
							c += i;
						}
					}
				});
			}
			if (c > 0)
				ClearMemory(types.ToArray());
			return c;
		}

		public Dictionary<Type, CreateTableResult> CreateTables(params Type[] types)
		{
			return connection.CreateTables(types);
		}
		public CreateTableResult CreateTable<T>() where T : new()
		{
			return connection.CreateTable<T>();
		}
		public T ExecuteScalar<T>(string query, params object[] args) where T : new()
		{
			return connection.ExecuteScalar<T>(query, args);
		}

		#endregion
		public class AsyncTableQuery<T>
		where T : new()
		{
			TableQuery<T> _innerQuery;

			public AsyncTableQuery(TableQuery<T> innerQuery)
			{
				_innerQuery = innerQuery;
			}

			public AsyncTableQuery<T> Where(Expression<Func<T, bool>> predExpr)
			{
				return new AsyncTableQuery<T>(_innerQuery.Where(predExpr));
			}

			public AsyncTableQuery<T> Skip(int n)
			{
				return new AsyncTableQuery<T>(_innerQuery.Skip(n));
			}

			public AsyncTableQuery<T> Take(int n)
			{
				return new AsyncTableQuery<T>(_innerQuery.Take(n));
			}

			public AsyncTableQuery<T> OrderBy<U>(Expression<Func<T, U>> orderExpr)
			{
				return new AsyncTableQuery<T>(_innerQuery.OrderBy<U>(orderExpr));
			}

			public AsyncTableQuery<T> OrderByDescending<U>(Expression<Func<T, U>> orderExpr)
			{
				return new AsyncTableQuery<T>(_innerQuery.OrderByDescending<U>(orderExpr));
			}

			public Task<List<T>> ToListAsync()
			{
				return Task.Factory.StartNew(() =>
				{
					return _innerQuery.ToList();
				});
			}

			public Task<int> CountAsync()
			{
				return Task.Factory.StartNew(() =>
				{
					return _innerQuery.Count();
				});
			}

			public Task<T> ElementAtAsync(int index)
			{
				return Task.Factory.StartNew(() =>
				{
					return _innerQuery.ElementAt(index);
				});
			}

			public Task<T> FirstAsync()
			{
				return Task<T>.Factory.StartNew(() =>
				{
					return _innerQuery.First();
				});
			}

			public Task<T> FirstOrDefaultAsync()
			{
				return Task<T>.Factory.StartNew(() =>
				{
					return _innerQuery.FirstOrDefault();
				});
			}
		}
	}

	[AttributeUsage(AttributeTargets.Property)]
	public class GroupByAttribute : IndexedAttribute
	{

		public bool Descending { get; set; }
		public GroupByAttribute(bool descending = false)
		{
			Descending = descending;
		}
	}

	[AttributeUsage(AttributeTargets.Property)]
	public class OrderByAttribute : IndexedAttribute
	{
		public bool Descending { get; set; }
		public OrderByAttribute(bool descending = false)
		{
			Descending = descending;
		}
	}
}

