//-----------------------------------------------------------------------
// <copyright file="ShardedDocumentSession.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using Raven.Abstractions.Data;
#if !NET_3_5
using Raven.Client.Connection.Async;
using Raven.Client.Document.Batches;
#endif
using Raven.Abstractions.Extensions;
using Raven.Client.Connection;
using Raven.Client.Document;
using Raven.Client.Document.SessionOperations;
using Raven.Client.Indexes;
using Raven.Client.Linq;
using System;
using Raven.Client.Shard.ShardResolution;
using Raven.Client.Util;
using Raven.Json.Linq;

namespace Raven.Client.Shard
{
#if !SILVERLIGHT
	/// <summary>
	/// Implements Unit of Work for accessing a set of sharded RavenDB servers
	/// </summary>
	public class ShardedDocumentSession : InMemoryDocumentSessionOperations, IDocumentSessionImpl, ITransactionalDocumentSession,
		ISyncAdvancedSessionOperation, IDocumentQueryGenerator
	{
#if !NET_3_5
		//private readonly IDictionary<string, IAsyncDatabaseCommands> asyncShardDbCommands;
		private readonly List<Tuple<ILazyOperation, IList<IDatabaseCommands>>> pendingLazyOperations = new List<Tuple<ILazyOperation, IList<IDatabaseCommands>>>();
		private readonly Dictionary<ILazyOperation, Action<object>> onEvaluateLazy = new Dictionary<ILazyOperation, Action<object>>();
#endif
		private readonly ShardStrategy shardStrategy;
		private readonly IDictionary<string, IDatabaseCommands> shardDbCommands;
		private readonly ShardedDocumentStore documentStore;

		/// <summary>
		/// Initializes a new instance of the <see cref="ShardedDocumentSession"/> class.
		/// </summary>
		/// <param name="shardStrategy">The shard strategy.</param>
		/// <param name="shardDbCommands">The shard IDatabaseCommands.</param>
		/// <param name="id"></param>
		/// <param name="documentStore"></param>
		/// <param name="listeners"></param>
		public ShardedDocumentSession(ShardedDocumentStore documentStore, DocumentSessionListeners listeners, Guid id,
			ShardStrategy shardStrategy, IDictionary<string, IDatabaseCommands> shardDbCommands
//#if !NET_3_5
//, IDictionary<string, IAsyncDatabaseCommands> asyncDatabaseCommands
//#endif
			)
			: base(documentStore, listeners, id)
		{
			this.shardStrategy = shardStrategy;
			this.shardDbCommands = shardDbCommands;
			this.documentStore = documentStore;
//#if !NET_3_5
//            this.asyncShardDbCommands = asyncDatabaseCommands;
//#endif
		}

		private IList<Tuple<string,IDatabaseCommands>> GetShardsToOperateOn(ShardRequestData resultionData)
		{
			var shardIds = shardStrategy.ShardResolutionStrategy.PotentialShardsFor(resultionData);

			IEnumerable<KeyValuePair<string, IDatabaseCommands>> cmds = shardDbCommands;

			if (shardIds != null)
				cmds = shardDbCommands.Where(cmd => shardIds.Contains(cmd.Key));

			return cmds.Select(x => Tuple.Create(x.Key, x.Value)).ToList();
		}

		private IList<IDatabaseCommands> GetCommandsToOperateOn(ShardRequestData resultionData)
		{
			return GetShardsToOperateOn(resultionData).Select(x => x.Item2).ToList();
		}

		protected override JsonDocument GetJsonDocument(string documentKey)
		{
			var dbCommands = GetCommandsToOperateOn(new ShardRequestData
			{
				EntityType = typeof(object),
				Key = documentKey
			});

			var documents = shardStrategy.ShardAccessStrategy.Apply(dbCommands, 
				(commands, i) => commands.Get(documentKey));

			var document = documents.FirstOrDefault(x => x != null);
			if (document != null)
				return document;

			throw new InvalidOperationException("Document '" + documentKey + "' no longer exists and was probably deleted");
		}

		public override void Commit(Guid txId)
		{
			throw new NotSupportedException("DTC support is handled via the internal document stores");
		}

		public override void Rollback(Guid txId)
		{
			throw new NotSupportedException("DTC support is handled via the internal document stores");
		}

		/// <summary>
		/// Promotes a transaction specified to a distributed transaction
		/// </summary>
		/// <param name="fromTxId">From tx id.</param>
		/// <returns>The token representing the distributed transaction</returns>
		public override byte[] PromoteTransaction(Guid fromTxId)
		{
			throw new NotSupportedException("DTC support is handled via the internal document stores");
		}

		/// <summary>
		/// Stores the recovery information for the specified transaction
		/// </summary>
		/// <param name="resourceManagerId">The resource manager Id for this transaction</param>
		/// <param name="txId">The tx id.</param>
		/// <param name="recoveryInformation">The recovery information.</param>
		public void StoreRecoveryInformation(Guid resourceManagerId, Guid txId, byte[] recoveryInformation)
		{
			throw new NotSupportedException("DTC support is handled via the internal document stores");
		}

		protected override void TryEnlistInAmbientTransaction()
		{
			// we DON'T support enlisting at the sharded document store level, only at the managed document stores, which 
			// turns out to be pretty much the same thing
		}

		public ISyncAdvancedSessionOperation Advanced
		{
			get { return this; }
		}

#if !NET_3_5

		/// <summary>
		/// Loads the specified ids and a function to call when it is evaluated
		/// </summary>
		public Lazy<TResult[]> Load<TResult>(IEnumerable<string> ids, Action<TResult[]> onEval)
		{
			return LazyLoadInternal(ids.ToArray(), new string[0], onEval);
		}

		/// <summary>
		/// Loads the specified id.
		/// </summary>
		Lazy<TResult> ILazySessionOperations.Load<TResult>(string id)
		{
			return Lazily.Load(id, (Action<TResult>)null);
		}

		/// <summary>
		/// Loads the specified id and a function to call when it is evaluated
		/// </summary>
		public Lazy<TResult> Load<TResult>(string id, Action<TResult> onEval)
		{
			var cmds = GetCommandsToOperateOn(new ShardRequestData
			                                                 	{
			                                                 		Key = id, EntityType = typeof (TResult)
			                                                 	});

			var lazyLoadOperation = new LazyLoadOperation<TResult>(id, new LoadOperation(this, () =>
			                                                                                   	{
			                                                                                   		var list = cmds.Select(databaseCommands => databaseCommands.DisableAllCaching()).ToList();
			                                                                                   		return new DisposableAction(() => list.ForEach(x => x.Dispose()));
			                                                                                   	}, id));
			return AddLazyOperation(lazyLoadOperation, onEval, cmds);
		}

		internal Lazy<T> AddLazyOperation<T>(ILazyOperation operation, Action<T> onEval, IList<IDatabaseCommands> cmds)
		{
			pendingLazyOperations.Add(Tuple.Create(operation, cmds));
			var lazyValue = new Lazy<T>(() =>
			                            	{
			                            		ExecuteAllPendingLazyOperations();
			                            		return (T) operation.Result;
			                            	});
			if (onEval != null)
				onEvaluateLazy[operation] = result => onEval((T) result);

			return lazyValue;
		}

		/// <summary>
		/// Register to lazily load documents and include
		/// </summary>
		public Lazy<T[]> LazyLoadInternal<T>(string[] ids, string[] includes, Action<T[]> onEval)
		{
			var idsAndShards = ids.Select(id => new
			                                    	{
			                                    		id,
			                                    		shards = GetCommandsToOperateOn(new ShardRequestData
			                                    		                                	{
			                                    		                                		Key = id,
			                                    		                                		EntityType = typeof (T),
			                                    		                                	})
			                                    	})
				.GroupBy(x => x.shards, new DbCmdsListComparer());
			var cmds = idsAndShards.SelectMany(idAndShard => idAndShard.Key).Distinct().ToList();

			var multiLoadOperation = new MultiLoadOperation(this, () =>
			                                                      	{
			                                                      		var list = cmds.Select(cmd => cmd.DisableAllCaching()).ToList();
			                                                      		return new DisposableAction(() => list.ForEach(disposable => disposable.Dispose()));
			                                                      	}, ids);
			var lazyOp = new LazyMultiLoadOperation<T>(multiLoadOperation, ids, includes);
			return AddLazyOperation(lazyOp, onEval, cmds);
		}

		/// <summary>
		/// Loads the specified entities with the specified id after applying
		/// conventions on the provided id to get the real document id.
		/// </summary>
		/// <remarks>
		/// This method allows you to call:
		/// Load{Post}(1)
		/// And that call will internally be translated to 
		/// Load{Post}("posts/1");
		/// 
		/// Or whatever your conventions specify.
		/// </remarks>
		Lazy<TResult> ILazySessionOperations.Load<TResult>(ValueType id)
		{
			return Lazily.Load<TResult>(id, null);
		}

		/// <summary>
		/// Loads the specified entities with the specified id after applying
		/// conventions on the provided id to get the real document id.
		/// </summary>
		/// <remarks>
		/// This method allows you to call:
		/// Load{Post}(1)
		/// And that call will internally be translated to 
		/// Load{Post}("posts/1");
		/// 
		/// Or whatever your conventions specify.
		/// </remarks>
		public Lazy<TResult> Load<TResult>(ValueType id, Action<TResult> onEval)
		{
			var documentKey = Conventions.FindFullDocumentKeyFromNonStringIdentifier(id, typeof(TResult), false);
			return Lazily.Load<TResult>(documentKey);
		}

		/// <summary>
		/// Begin a load while including the specified path 
		/// </summary>
		/// <param name="path">The path.</param>
		ILazyLoaderWithInclude<object> ILazySessionOperations.Include(string path)
		{
			return new LazyMultiLoaderWithInclude<object>(this).Include(path);
		}

		/// <summary>
		/// Begin a load while including the specified path 
		/// </summary>
		/// <param name="path">The path.</param>
		ILazyLoaderWithInclude<T> ILazySessionOperations.Include<T>(Expression<Func<T, object>> path)
		{
			return new LazyMultiLoaderWithInclude<T>(this).Include(path);
		}

		/// <summary>
		/// Loads the specified ids.
		/// </summary>
		/// <param name="ids">The ids.</param>
		Lazy<TResult[]> ILazySessionOperations.Load<TResult>(params string[] ids)
		{
			return Lazily.Load<TResult>(ids, null);
		}

#endif
		public T Load<T>(string id)
		{
			object existingEntity;
			if (entitiesByKey.TryGetValue(id, out existingEntity))
			{
				return (T)existingEntity;
			}

			IncrementRequestCount();
			var dbCommands = GetCommandsToOperateOn(new ShardRequestData
			                                      	{
			                                      		EntityType = typeof (T),
			                                      		Key = id
			                                      	});
			var results = shardStrategy.ShardAccessStrategy.Apply(dbCommands, (commands, i) =>
			                                                                  	{
			                                                                  		var loadOperation = new LoadOperation(this, commands.DisableAllCaching, id);
			                                                                  		bool retry;
			                                                                  		do
			                                                                  		{
			                                                                  			loadOperation.LogOperation();
			                                                                  			using (loadOperation.EnterLoadContext())
			                                                                  			{
			                                                                  				retry = loadOperation.SetResult(commands.Get(id));
			                                                                  			}
			                                                                  		} while (retry);
			                                                                  		return loadOperation.Complete<T>();
			                                                                  	});
			return results.FirstOrDefault(x => !Equals(x, default(T)));
		}

		public T[] Load<T>(params string[] ids)
		{
			return LoadInternal<T>(ids, null);
		}

		public T[] Load<T>(IEnumerable<string> ids)
		{
			return ((IDocumentSessionImpl)this).LoadInternal<T>(ids.ToArray(), null);
		}

		public T Load<T>(ValueType id)
		{
			var documentKey = Conventions.FindFullDocumentKeyFromNonStringIdentifier(id, typeof(T), false);
			return Load<T>(documentKey);
		}

		/// <summary>
		/// Queries the specified index using Linq.
		/// </summary>
		/// <typeparam name="T">The result of the query</typeparam>
		/// <param name="indexName">Name of the index.</param>
		/// <returns></returns>
		public IRavenQueryable<T> Query<T>(string indexName)
		{
			var ravenQueryStatistics = new RavenQueryStatistics();
			var provider = new RavenQueryProvider<T>(this, indexName, ravenQueryStatistics, null
#if !NET_3_5
			                                         , null
#endif
				);
			return new RavenQueryInspector<T>(provider, ravenQueryStatistics, indexName, null, null
#if !NET_3_5
			                                  , null
#endif
				);
		}

		/// <summary>
		/// Query RavenDB dynamically using LINQ
		/// </summary>
		/// <typeparam name="T">The result of the query</typeparam>
		public IRavenQueryable<T> Query<T>()
		{
			var indexName = "dynamic";
			if (typeof(T).IsEntityType())
			{
				indexName += "/" + Conventions.GetTypeTagName(typeof(T));
			}
			return Query<T>(indexName);
		}

		/// <summary>
		/// Queries the index specified by <typeparamref name="TIndexCreator"/> using Linq.
		/// </summary>
		/// <typeparam name="T">The result of the query</typeparam>
		/// <typeparam name="TIndexCreator">The type of the index creator.</typeparam>
		/// <returns></returns>
		public IRavenQueryable<T> Query<T, TIndexCreator>() where TIndexCreator : AbstractIndexCreationTask, new()
		{
			var indexCreator = new TIndexCreator();
			return Query<T>(indexCreator.IndexName);
		}

		public ILoaderWithInclude<object> Include(string path)
		{
			return new MultiLoaderWithInclude<object>(this).Include(path);
		}
#if !NET_3_5

		
#endif
		public ILoaderWithInclude<T> Include<T>(Expression<Func<T, object>> path)
		{
			return new MultiLoaderWithInclude<T>(this).Include(path);
		}

		public override void Defer(params Abstractions.Commands.ICommandData[] commands)
		{
			throw new NotSupportedException("You cannot defer commands using the sharded document session, because we don't know which shard to use");
		}

		/// <summary>
		/// Saves all the changes to the Raven server.
		/// </summary>
		public void SaveChanges()
		{
			using (EntitiesToJsonCachingScope())
			{
				var data = PrepareForSaveChanges();
				if (data.Commands.Count == 0)
					return; // nothing to do here

				IncrementRequestCount();
				LogBatch(data);
				
				// split by shards
				var saveChangesPerShard = new Dictionary<string, SaveChangesData>();
				for (int index = 0; index < data.Entities.Count; index++)
				{
					var entity = data.Entities[index];
					var metadata = GetMetadataFor(entity);
					var shardId = metadata.Value<string>(Constants.RavenShardId);

					if (shardId == null)
					{
						shardId = shardStrategy.ShardResolutionStrategy.GenerateShardIdFor(entity);
						metadata[Constants.RavenShardId] = shardId;
					}

					var shardSaveChangesData = saveChangesPerShard.GetOrAdd(shardId);
					shardSaveChangesData.Entities.Add(entity);
					shardSaveChangesData.Commands.Add(data.Commands[index]);
				}

				// execute on all shards
				foreach (var shardAndObjects in saveChangesPerShard)
				{
					var shardId = shardAndObjects.Key;

					IDatabaseCommands databaseCommands;
					if (shardDbCommands.TryGetValue(shardId, out databaseCommands) == false)
						throw new InvalidOperationException(string.Format("ShardedDocumentStore cannot found a DatabaseCommands for shard id '{0}'.", shardId));

					var results = databaseCommands.Batch(shardAndObjects.Value.Commands);
					UpdateBatchResults(results, shardAndObjects.Value);
				}
			}
		}

#if !NET_3_5
		IAsyncDocumentQuery<T> IDocumentQueryGenerator.AsyncQuery<T>(string indexName)
		{
			throw new NotSupportedException("Shared document store doesn't support async operations");
		}
#endif

		IDocumentQuery<T> IDocumentQueryGenerator.Query<T>(string indexName)
		{
			return Advanced.LuceneQuery<T>(indexName);
		}

		public void Refresh<T>(T entity)
		{
			DocumentMetadata value;
			if (entitiesAndMetadata.TryGetValue(entity, out value) == false)
				throw new InvalidOperationException("Cannot refresh a transient instance");
			IncrementRequestCount();


			var dbCommands = GetCommandsToOperateOn(new ShardRequestData
			{
				EntityType = typeof(T),
				Key = value.Key
			});
			//TODO: shard access
			foreach (var dbCmd in dbCommands)
			{
				var jsonDocument = dbCmd.Get(value.Key);
				if (jsonDocument == null)
					continue;

				value.Metadata = jsonDocument.Metadata;
				value.OriginalMetadata = (RavenJObject)jsonDocument.Metadata.CloneToken();
				value.ETag = jsonDocument.Etag;
				value.OriginalValue = jsonDocument.DataAsJson;
				var newEntity = ConvertToEntity<T>(value.Key, jsonDocument.DataAsJson, jsonDocument.Metadata);
				foreach (var property in entity.GetType().GetProperties())
				{
					if (!property.CanWrite || !property.CanRead || property.GetIndexParameters().Length != 0)
						continue;
					property.SetValue(entity, property.GetValue(newEntity, null), null);
				}
			}

			throw new InvalidOperationException("Document '" + value.Key + "' no longer exists and was probably deleted");
		}

		IDatabaseCommands ISyncAdvancedSessionOperation.DatabaseCommands
		{
			get { throw new NotSupportedException("Not supported in a sharded session"); }
		}

#if !NET_3_5
		/// <summary>
		/// Gets the async database commands.
		/// </summary>
		/// <value>The async database commands.</value>
		public IAsyncDatabaseCommands AsyncDatabaseCommands
		{
			get { throw new NotSupportedException("Not supported in a sharded session"); }
		}

		/// <summary>
		/// Access the lazy operations
		/// </summary>
		public ILazySessionOperations Lazily
		{
			get { return this; }
		}

		/// <summary>
		/// Access the eager operations
		/// </summary>
		public IEagerSessionOperations Eagerly
		{
			get { return this; }
		}
#endif

		public IDocumentQuery<T> LuceneQuery<T, TIndexCreator>() where TIndexCreator : AbstractIndexCreationTask, new()
		{
			var indexName = new TIndexCreator().IndexName;
			return LuceneQuery<T>(indexName);
		}

		public IDocumentQuery<T> LuceneQuery<T>(string indexName)
		{
			return new ShardedDocumentQuery<T>(this, GetShardsToOperateOn, shardStrategy, indexName, null, listeners.QueryListeners);
		}

		public IDocumentQuery<T> LuceneQuery<T>()
		{
			string indexName = "dynamic";
			if (typeof(T).IsEntityType())
			{
				indexName += "/" + Conventions.GetTypeTagName(typeof(T));
			}
			return Advanced.LuceneQuery<T>(indexName);
		}

		public string GetDocumentUrl(object entity)
		{
			DocumentMetadata value;
			if(entitiesAndMetadata.TryGetValue(entity, out value) == false)
				throw new ArgumentException("The entity is not part of the session");

			var shardId = value.Metadata.Value<string>(Constants.RavenShardId);
			IDatabaseCommands commands;
			if(shardDbCommands.TryGetValue(shardId, out commands) == false)
				throw new InvalidOperationException("Could not find matching shard for shard id: " + shardId);
			return commands.UrlFor(value.Key);
		}

		public T[] LoadInternal<T>(string[] ids, string[] includes)
		{
			if (ids.Length == 0)
				return new T[0];

			IncrementRequestCount();
			var idsAndShards = ids.Select(id => new
			                                    	{
			                                    		id,
														shards = GetCommandsToOperateOn(new ShardRequestData
			                                    		                              	{
			                                    		                              		EntityType = typeof (T),
			                                    		                              		Key = id
			                                    		                              	})
			                                    	})
				.GroupBy(x => x.shards, new DbCmdsListComparer());

			var results = new T[ids.Length];
			foreach (var shard in idsAndShards)
			{
				var currentShardIds = shard.Select(x => x.id).ToArray();
				var multiLoadOperations = shardStrategy.ShardAccessStrategy.Apply(shard.Key, (dbCmd, i) =>
				{
					var multiLoadOperation = new MultiLoadOperation(this, dbCmd.DisableAllCaching, currentShardIds);
					MultiLoadResult multiLoadResult;
					do
					{
						multiLoadOperation.LogOperation();
						using (multiLoadOperation.EnterMultiLoadContext())
						{
							multiLoadResult = dbCmd.Get(currentShardIds, includes);
						}
					} while (multiLoadOperation.SetResult(multiLoadResult));
					return multiLoadOperation;
				});
				foreach (var multiLoadOperation in multiLoadOperations)
				{
					var loadResults = multiLoadOperation.Complete<T>();
					for (int i = 0; i < loadResults.Length; i++)
					{
						if(ReferenceEquals(loadResults[i], null))
							continue;
						results[Array.IndexOf(ids, currentShardIds[i])] = loadResults[i];
					}
				}
			}
			return results;
		}

		public T[] LoadInternal<T>(string[] ids)
		{
			throw new NotImplementedException();
		}

		internal class DbCmdsListComparer : IEqualityComparer<IList<IDatabaseCommands>>
		{
			public bool Equals(IList<IDatabaseCommands> x, IList<IDatabaseCommands> y)
			{
				if (x.Count != y.Count)
					return false;

				return !x.Where((t, i) => t != y[i]).Any();
			}

			public int GetHashCode(IList<IDatabaseCommands> obj)
			{
				return obj.Aggregate(obj.Count, (current, item) => (current * 397) ^ item.GetHashCode());
			}

		}

		public void ExecuteAllPendingLazyOperations()
		{
			if (pendingLazyOperations.Count == 0)
				return;

			try
			{
				IncrementRequestCount();
				while (ExecuteLazyOperationsSingleStep())
				{
					Thread.Sleep(100);
				}

				foreach (var pendingLazyOperation in pendingLazyOperations)
				{
					Action<object> value;
					if (onEvaluateLazy.TryGetValue(pendingLazyOperation.Item1, out value))
						value(pendingLazyOperation.Item1.Result);
				}
			}
			finally
			{
				pendingLazyOperations.Clear();
			}
		}

		private bool ExecuteLazyOperationsSingleStep()
		{
			var disposables = pendingLazyOperations.Select(x => x.Item1.EnterContext()).Where(x => x != null).ToList();
			try
			{
				var operationsPerShardGroup = pendingLazyOperations.GroupBy(x => x.Item2, new DbCmdsListComparer());

				foreach (var operationPerShard in operationsPerShardGroup)
				{
					var lazyOperations = operationPerShard.Select(x => x.Item1).ToArray();
					var requests = lazyOperations.Select(x => x.CraeteRequest()).ToArray();
					var multiResponses = shardStrategy.ShardAccessStrategy.Apply(operationPerShard.Key, (commands, i) => commands.MultiGet(requests));

					var sb = new StringBuilder();
					foreach (var response in from shardReponses in multiResponses
											 from getResponse in shardReponses
											 where getResponse.RequestHasErrors()
											 select getResponse)
						sb.AppendFormat("Got an error from server, status code: {0}{1}{2}", response.Status, Environment.NewLine, response.Result)
							.AppendLine();

					if (sb.Length > 0)
						throw new InvalidOperationException(sb.ToString());

					for (int i = 0; i < lazyOperations.Length; i++)
					{
						var copy = i;
						lazyOperations[i].HandleResponses(multiResponses.Select(x=>x[copy]).ToArray(),shardStrategy);
						if (lazyOperations[i].RequiresRetry)
							return true;
					}
				}
				return false;
			}
			finally
			{
				disposables.ForEach(disposable => disposable.Dispose());
			}
		}
	}

#endif
}