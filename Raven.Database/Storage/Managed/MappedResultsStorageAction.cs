//-----------------------------------------------------------------------
// <copyright file="MappedResultsStorageAction.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raven.Abstractions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.MEF;
using Raven.Abstractions.Util;
using Raven.Database.Extensions;
using Raven.Database.Impl;
using Raven.Database.Indexing;
using Raven.Database.Plugins;
using Raven.Database.Storage;
using Raven.Database.Util;
using Raven.Json.Linq;
using Raven.Storage.Managed.Impl;
using Table = Raven.Munin.Table;

namespace Raven.Storage.Managed
{

	public class MappedResultsStorageAction : IMappedResultsStorageAction
	{
		private readonly TableStorage storage;
		private readonly IUuidGenerator generator;
		private readonly OrderedPartCollection<AbstractDocumentCodec> documentCodecs;

		public MappedResultsStorageAction(TableStorage storage, IUuidGenerator generator, OrderedPartCollection<AbstractDocumentCodec> documentCodecs)
		{
			this.storage = storage;
			this.generator = generator;
			this.documentCodecs = documentCodecs;
		}

		public void PutMappedResult(string view, string docId, string reduceKey, RavenJObject data)
		{
			var ms = new MemoryStream();

			using (var stream = documentCodecs.Aggregate((Stream)ms, (ds, codec) => codec.Value.Encode(reduceKey, data, null, ds)))
			{
				data.WriteTo(stream);
			}
			var byteArray = generator.CreateSequentialUuid(UuidType.MappedResults).ToByteArray();
			var key = new RavenJObject
			{
				{"view", view},
				{"reduceKey", reduceKey},
				{"docId", docId},
				{"etag", byteArray},
				{"bucket", IndexingUtil.MapBucket(docId)},
				{"timestamp", SystemTime.UtcNow}
			};
			storage.MappedResults.Put(key, ms.ToArray());

			IncrementReduceKeyCounter(view, reduceKey, 1);
		}

		private RavenJObject LoadMappedResult(Table.ReadResult readResult)
		{
			var key = readResult.Key.Value<string>("reduceKey");

			Stream memoryStream = new MemoryStream(readResult.Data());
			using (var stream = documentCodecs.Aggregate(memoryStream, (ds, codec) => codec.Decode(key, null, ds)))
			{
				return stream.ToJObject();
			}
		}

		public void DeleteMappedResultsForDocumentId(string documentId, string view, HashSet<ReduceKeyAndBucket> removed)
		{
			foreach (var key in storage.MappedResults["ByViewAndDocumentId"].SkipTo(new RavenJObject
			{
				{"view", view},
				{"docId", documentId}
			}).TakeWhile(x => StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("view"), view) &&
							  StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("docId"), documentId)))
			{
				storage.MappedResults.Remove(key);

				var reduceKey = key.Value<string>("reduceKey");
				removed.Add(new ReduceKeyAndBucket(key.Value<int>("bucket"), reduceKey));

				IncrementReduceKeyCounter(view, reduceKey, -1);
			}
		}

		public void DeleteMappedResultsForView(string view)
		{
			foreach (var key in storage.MappedResults["ByViewAndReduceKey"].SkipTo(new RavenJObject { { "view", view } })
			.TakeWhile(x => StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("view"), view)))
			{
				storage.MappedResults.Remove(key);

				IncrementReduceKeyCounter(view, key.Value<string>("reduceKey"), -1);
			}
		}

		public void ScheduleReductions(string view, int level, IEnumerable<ReduceKeyAndBucket> reduceKeysAndBuckets)
		{
			foreach (var reduceKeysAndBucket in reduceKeysAndBuckets)
			{
				var etag = generator.CreateSequentialUuid(UuidType.ScheduledReductions).ToByteArray();
				storage.ScheduleReductions.UpdateKey(new RavenJObject
					{
						{"view", view},
						{"reduceKey", reduceKeysAndBucket.ReduceKey},
						{"bucket", reduceKeysAndBucket.Bucket},
						{"level", level},
						{"etag", etag},
						{"timestamp", SystemTime.UtcNow}
					});
			}
		}

		public ScheduledReductionInfo DeleteScheduledReduction(List<object> itemsToDelete)
		{
			var result = new ScheduledReductionInfo();
			var hasResult = false;
			var currentEtagBinary = Guid.Empty.ToByteArray();
			foreach (RavenJToken token in itemsToDelete)
			{
				var readResult = storage.ScheduleReductions.Read(token);
				if (readResult == null)
					continue;

				var etagBinary = readResult.Key.Value<byte[]>("etag");
				if (new ComparableByteArray(etagBinary).CompareTo(currentEtagBinary) > 0)
				{
					hasResult = true;
					var timestamp = readResult.Key.Value<DateTime>("timestamp");
					result.Etag = etagBinary.TransfromToGuidWithProperSorting();
					result.Timestamp = timestamp;
				}

				storage.ScheduleReductions.Remove(token);
			}
			return hasResult ? result : null;
		}

		public IEnumerable<MappedResultInfo> GetItemsToReduce(string index, string[] reduceKeys, int level, bool loadData, List<object> itemsToDelete)
		{
			var seen = new HashSet<Tuple<string, int>>();

			foreach (var reduceKey in reduceKeys)
			{
				var keyCriteria = new RavenJObject
			                  {
				                  {"view", index},
				                  {"level", level},
								  {"reduceKey", reduceKey}
			                  };

				foreach (var result in storage.ScheduleReductions["ByViewLevelReduceKeyAndBucket"].SkipTo(keyCriteria))
				{
					var indexFromDb = result.Value<string>("view");
					var levelFromDb = result.Value<int>("level");
					var reduceKeyFromDb = result.Value<string>("reduceKey");

					if (string.Equals(indexFromDb, index, StringComparison.InvariantCultureIgnoreCase) == false ||
						levelFromDb != level)
						break;

					if (string.Equals(reduceKeyFromDb, reduceKey, StringComparison.Ordinal) == false)
					{
						break;
					}

					var bucket = result.Value<int>("bucket");

					if (seen.Add(Tuple.Create(reduceKeyFromDb, bucket)))
					{
						foreach (var mappedResultInfo in GetResultsForBucket(index, level, reduceKeyFromDb, bucket, loadData))
						{
							yield return mappedResultInfo;
						}
					}
					itemsToDelete.Add(result);
				}
			}
		}

		private IEnumerable<MappedResultInfo> GetResultsForBucket(string index, int level, string reduceKey, int bucket, bool loadData)
		{
			switch (level)
			{
				case 0:
					return GetMappedResultsForBucket(index, reduceKey, bucket, loadData);
				case 1:
				case 2:
					return GetReducedResultsForBucket(index, reduceKey, level, bucket, loadData);
				default:
					throw new ArgumentException("Invalid level: " + level);
			}
		}

		private IEnumerable<MappedResultInfo> GetReducedResultsForBucket(string index, string reduceKey, int level, int bucket, bool loadData)
		{
			var results = storage.ReduceResults["ByViewReduceKeyLevelAndBucket"]
				.SkipTo(new RavenJObject
				{
					{"view", index},
					{"reduceKey", reduceKey},
					{"level", level},
					{"bucket", bucket}
				})
				.TakeWhile(x => string.Equals(index, x.Value<string>("view"), StringComparison.InvariantCultureIgnoreCase) &&
								string.Equals(reduceKey, x.Value<string>("reduceKey"), StringComparison.InvariantCultureIgnoreCase) &&
								level == x.Value<int>("level") &&
								bucket == x.Value<int>("bucket"));

			bool hasResults = false;
			foreach (var result in results)
			{
				hasResults = true;
				var readResult = storage.ReduceResults.Read(result);

				var mappedResultInfo = new MappedResultInfo
				{
					ReduceKey = readResult.Key.Value<string>("reduceKey"),
					Etag = new Guid(readResult.Key.Value<byte[]>("etag")),
					Timestamp = readResult.Key.Value<DateTime>("timestamp"),
					Bucket = readResult.Key.Value<int>("bucket"),
					Source = readResult.Key.Value<int>("sourceBucket").ToString(),
					Size = readResult.Size,
					Data = loadData ? LoadMappedResult(readResult) : null
				};

				yield return mappedResultInfo;
			}

			if (hasResults)
				yield break;

			yield return new MappedResultInfo
			{
				Bucket = bucket,
				ReduceKey = reduceKey
			};
		}

		private IEnumerable<MappedResultInfo> GetMappedResultsForBucket(string index, string reduceKey, int bucket, bool loadData)
		{
			var results = storage.MappedResults["ByViewReduceKeyAndBucket"]
				.SkipTo(new RavenJObject
				{
					{"view", index},
					{"reduceKey", reduceKey},
					{"bucket", bucket}
				})
				.TakeWhile(x => string.Equals(index, x.Value<string>("view"), StringComparison.InvariantCultureIgnoreCase) &&
								string.Equals(reduceKey, x.Value<string>("reduceKey"), StringComparison.InvariantCultureIgnoreCase) &&
								bucket == x.Value<int>("bucket"));

			bool hasResults = false;
			foreach (var result in results)
			{
				hasResults = true;
				var readResult = storage.MappedResults.Read(result);

				yield return new MappedResultInfo
				{
					ReduceKey = readResult.Key.Value<string>("reduceKey"),
					Etag = new Guid(readResult.Key.Value<byte[]>("etag")),
					Timestamp = readResult.Key.Value<DateTime>("timestamp"),
					Bucket = readResult.Key.Value<int>("bucket"),
					Source = readResult.Key.Value<string>("docId"),
					Size = readResult.Size,
					Data = loadData ? LoadMappedResult(readResult) : null
				};
			}

			if (hasResults)
				yield break;

			yield return new MappedResultInfo
			{
				Bucket = bucket,
				ReduceKey = reduceKey
			};
		}

		public void PutReducedResult(string name, string reduceKey, int level, int sourceBucket, int bucket, RavenJObject data)
		{
			var ms = new MemoryStream();

			using (var stream = documentCodecs.Aggregate((Stream)ms, (ds, codec) => codec.Value.Encode(reduceKey, data, null, ds)))
			{
				data.WriteTo(stream);
			}

			var etag = generator.CreateSequentialUuid(UuidType.ReduceResults).ToByteArray();

			storage.ReduceResults.Put(new RavenJObject
			{
				{"view", name},
				{"etag", etag},
				{"reduceKey", reduceKey},
				{"level", level},
				{"sourceBucket", sourceBucket},
				{"bucket", bucket},
				{"timestamp", SystemTime.UtcNow}
			}, ms.ToArray());
		}

		public void RemoveReduceResults(string indexName, int level, string reduceKey, int sourceBucket)
		{
			var results = storage.ReduceResults["ByViewReduceKeyAndSourceBucket"].SkipTo(new RavenJObject
			{
				{"view", indexName},
				{"reduceKey", reduceKey},
				{"level", level},
				{"sourceBucket", sourceBucket},
			}).TakeWhile(x => string.Equals(indexName, x.Value<string>("view"), StringComparison.InvariantCultureIgnoreCase) &&
								string.Equals(reduceKey, x.Value<string>("reduceKey"), StringComparison.InvariantCultureIgnoreCase) &&
								sourceBucket == x.Value<int>("sourceBucket") &&
								level == x.Value<int>("level"));

			foreach (var result in results)
			{
				storage.ReduceResults.Remove(result);
			}
		}

		public IEnumerable<ReduceTypePerKey> GetReduceTypesPerKeys(string indexName, int take, int limitOfItemsToReduceInSingleStep)
		{
			var allKeysToReduce = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

			foreach (var reduction in storage.ScheduleReductions["ByViewLevelReduceKeyAndBucket"].SkipTo(new RavenJObject
			{
				{"view", indexName},
				{"level", 0}
			}).TakeWhile(x => string.Equals(indexName, x.Value<string>("view"), StringComparison.InvariantCultureIgnoreCase) &&
								x.Value<int>("level") == 0)
								.Take(take))
			{
				allKeysToReduce.Add(reduction.Value<string>("reduceKey"));
			}

			var reduceTypesPerKeys = allKeysToReduce.ToDictionary(x => x, x => ReduceType.SingleStep);

			foreach (var reduceKey in allKeysToReduce)
			{
				var count = GetNumberOfMappedItemsPerReduceKey(indexName, reduceKey);
				if (count >= limitOfItemsToReduceInSingleStep)
				{
					reduceTypesPerKeys[reduceKey] = ReduceType.MultiStep;
				}
			}

			return reduceTypesPerKeys.Select(x => new ReduceTypePerKey(x.Key, x.Value));
		}

		public void UpdatePerformedReduceType(string indexName, string reduceKey, ReduceType reduceType)
		{
			var readResult = storage.ReduceKeys.Read(new RavenJObject { { "view", indexName }, { "reduceKey", reduceKey } });

			if (readResult == null)
			{
				storage.ReduceKeys.Put(new RavenJObject
				{
					                       {"view", indexName},
					                       {"reduceKey", reduceKey},
					                       {"reduceType", (int) reduceType},
					                       {"mappedItemsCount", 0}
				                       }, null);
				return;
			}

			var key = (RavenJObject)readResult.Key.CloneToken();
			key["reduceType"] = (int)reduceType;
			storage.ReduceKeys.UpdateKey(key);
		}

		public ReduceType GetLastPerformedReduceType(string indexName, string reduceKey)
		{
			var readResult = storage.ReduceKeys.Read(new RavenJObject { { "view", indexName }, { "reduceKey", reduceKey } });

			if (readResult == null)
				return ReduceType.None;

			return (ReduceType)readResult.Key.Value<int>("reduceType");
		}

		public IEnumerable<int> GetMappedBuckets(string indexName, string reduceKey)
		{
			return storage.MappedResults["ByViewAndReduceKey"].SkipTo(new RavenJObject
			{
				{"view", indexName},
				{"reduceKey", reduceKey}
			}).TakeWhile(x => string.Equals(indexName, x.Value<string>("view"), StringComparison.InvariantCultureIgnoreCase) &&
								string.Equals(reduceKey, x.Value<string>("reduceKey"), StringComparison.InvariantCultureIgnoreCase))
				.Select(x => x.Value<int>("bucket"))
				.Distinct();
		}

		public IEnumerable<MappedResultInfo> GetMappedResults(string indexName, string[] keysToReduce, bool loadData)
		{
			foreach (var reduceKey in keysToReduce)
			{
				string key = reduceKey;

				foreach (var item in storage.MappedResults["ByViewAndReduceKey"].SkipTo(new RavenJObject
				{
					{"view", indexName},
					{"reduceKey", reduceKey}
				})
				.TakeWhile(
					x => StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("view"), indexName) &&
						 StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("reduceKey"), key)))
				{
					var readResult = storage.MappedResults.Read(item);

					yield return new MappedResultInfo
					{
						ReduceKey = readResult.Key.Value<string>("reduceKey"),
						Etag = new Guid(readResult.Key.Value<byte[]>("etag")),
						Timestamp = readResult.Key.Value<DateTime>("timestamp"),
						Bucket = readResult.Key.Value<int>("bucket"),
						Source = readResult.Key.Value<string>("docId"),
						Size = readResult.Size,
						Data = loadData ? LoadMappedResult(readResult) : null
					};
				}
			}
		}

		public IEnumerable<string> GetKeysForIndexForDebug(string indexName, int start, int take)
		{
			return storage.MappedResults["ByViewReduceKeyAndBucket"].SkipTo(new RavenJObject
			{
				{"view", indexName},
			}).TakeWhile(x => string.Equals(indexName, x.Value<string>("view"), StringComparison.InvariantCultureIgnoreCase))
				.Select(x => x.Value<string>("reduceKey"))
				.Distinct()
				.Skip(start)
				.Take(take);
		}

		public IEnumerable<MappedResultInfo> GetMappedResultsForDebug(string indexName, string key, int take)
		{
			var results = storage.MappedResults["ByViewReduceKeyAndBucket"].SkipTo(new RavenJObject
			{
				{"view", indexName},
				{"reduceKey", key},
			}).TakeWhile(x => string.Equals(indexName, x.Value<string>("view"), StringComparison.InvariantCultureIgnoreCase) &&
							  string.Equals(key, x.Value<string>("reduceKey"), StringComparison.InvariantCultureIgnoreCase))
				.Take(take);

			return from result in results
				   select storage.MappedResults.Read(result)
					   into readResult
					   where readResult != null
					   select new MappedResultInfo
					   {
						   ReduceKey = readResult.Key.Value<string>("reduceKey"),
						   Etag = new Guid(readResult.Key.Value<byte[]>("etag")),
						   Timestamp = readResult.Key.Value<DateTime>("timestamp"),
						   Bucket = readResult.Key.Value<int>("bucket"),
						   Source = readResult.Key.Value<string>("docId"),
						   Size = readResult.Size,
						   Data = LoadMappedResult(readResult)
					   };
		}

		public IEnumerable<MappedResultInfo> GetReducedResultsForDebug(string indexName, string key, int level, int take)
		{
			var results = storage.ReduceResults["ByViewReduceKeyLevelAndBucket"].SkipTo(new RavenJObject
			{
				{"view", indexName},
				{"reduceKey", key},
				{"level", level}
			}).TakeWhile(x => string.Equals(indexName, x.Value<string>("view"), StringComparison.InvariantCultureIgnoreCase) &&
							  string.Equals(key, x.Value<string>("reduceKey"), StringComparison.InvariantCultureIgnoreCase) &&
							  level == x.Value<int>("level"))
				.Take(take);

			return from result in results
				   select storage.ReduceResults.Read(result)
					   into readResult
					   where readResult != null
					   select new MappedResultInfo
					   {
						   ReduceKey = readResult.Key.Value<string>("reduceKey"),
						   Etag = new Guid(readResult.Key.Value<byte[]>("etag")),
						   Timestamp = readResult.Key.Value<DateTime>("timestamp"),
						   Bucket = readResult.Key.Value<int>("bucket"),
						   Source = readResult.Key.Value<string>("docId"),
						   Size = readResult.Size,
						   Data = LoadMappedResult(readResult)
					   };
		}

		public IEnumerable<ReduceKeyAndCount> GetKeysStats(string view, int start, int pageSize)
		{
			return storage.ReduceKeys["ByView"].SkipTo(new RavenJObject { { "view", view } })
				.TakeWhile(x => StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("view"), view))
				.Skip(start)
				.Take(pageSize)
				.Select(token => new ReduceKeyAndCount
				{
					Count = token.Value<int>("mappedItemsCount"),
					Key = token.Value<string>("reduceKey")
				});
		}

		Dictionary<Tuple<string, string>, int> reduceKeyChanges;
		private void IncrementReduceKeyCounter(string view, string reduceKey, int val)
		{
			if (reduceKeyChanges == null)
			{
				reduceKeyChanges = new Dictionary<Tuple<string, string>, int>();
			}

			var key = Tuple.Create(view, reduceKey);
			reduceKeyChanges[key] = reduceKeyChanges.GetOrAdd(key) + val;
		}

		private void UpdateReduceKeyCounter(string view, string reduceKey, int value)
		{
			var readResult = storage.ReduceKeys.Read(new RavenJObject { { "view", view }, { "reduceKey", reduceKey } });

			if (readResult == null)
			{
				if (value <= 0)
					return;
				storage.ReduceKeys.Put(new RavenJObject()
				                       {
					                       {"view", view},
					                       {"reduceKey", reduceKey},
					                       {"reduceType", (int) ReduceType.None},
					                       {"mappedItemsCount", value}
				                       }, null);
				return;
			}

			var rkey = (RavenJObject)readResult.Key.CloneToken();

			var decrementedValue = rkey.Value<int>("mappedItemsCount") + value;

			if (decrementedValue > 0)
			{
				rkey["mappedItemsCount"] = decrementedValue;
				storage.ReduceKeys.UpdateKey(rkey);
			}
			else
			{
				storage.ReduceKeys.Remove(rkey);
			}
		}

		private int GetNumberOfMappedItemsPerReduceKey(string view, string reduceKey)
		{
			var readResult = storage.ReduceKeys.Read(new RavenJObject { { "view", view }, { "reduceKey", reduceKey } });

			if (readResult == null)
				return 0;

			return readResult.Key.Value<int>("mappedItemsCount");
		}

		public void PreCommit()
		{
			if (reduceKeyChanges == null)
				return;

			foreach (var reduceKeyChange in reduceKeyChanges)
			{
				UpdateReduceKeyCounter(reduceKeyChange.Key.Item1, reduceKeyChange.Key.Item2, reduceKeyChange.Value);
			}
		}
	}
}
