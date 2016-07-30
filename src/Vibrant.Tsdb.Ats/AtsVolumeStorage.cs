﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Vibrant.Tsdb.Ats.Helpers;
using Vibrant.Tsdb.Ats.Serialization;
using Vibrant.Tsdb.Helpers;
using Vibrant.Tsdb.Serialization;

namespace Vibrant.Tsdb.Ats
{
   public class AtsVolumeStorage : IVolumeStorage
   {
      private object _sync = new object();
      private SemaphoreSlim _getSemaphore;
      private SemaphoreSlim _setSemaphore;
      private string _tableName;
      private CloudStorageAccount _account;
      private CloudTableClient _client;
      private Task<CloudTable> _table;

      public AtsVolumeStorage( string tableName, string connectionString )
      {
         _getSemaphore = new SemaphoreSlim( 25 );
         _setSemaphore = new SemaphoreSlim( 10 );
         _tableName = tableName;
         _account = CloudStorageAccount.Parse( connectionString );
         _client = _account.CreateCloudTableClient();
      }

      public async Task Write( IEnumerable<IEntry> items )
      {
         List<Task> tasks = new List<Task>();

         foreach( var entry in items.SplitEntriesByPartitionAndTable() )
         {
            var id = entry.Id;
            var from = entry.From;
            var to = entry.To.AddTicks( 1 ); // must be inclusive on the last measure point because we may be overriding it
            var entries = entry.Entries;

            tasks.Add( StoreForId( id, entries, from, to ) );
         }

         await Task.WhenAll( tasks ).ConfigureAwait( false );
      }

      public async Task<int> Delete( string id, DateTime from, DateTime to )
      {
         int count = await DeleteForId( id, from, to ).ConfigureAwait( false );

         return count;
      }

      public async Task<int> Delete( string id )
      {
         int count = await DeleteAllForId( id ).ConfigureAwait( false );

         return count;
      }

      public async Task<ReadResult<IEntry>> ReadLatest( string id )
      {
         var results = await RetrieveAllForId( id ).ConfigureAwait( false );

         return new ReadResult<IEntry>(
            id,
            results.SelectMany( x => x.Entries ).Take( 1 )
               .ToList() );
      }

      public async Task<ReadResult<TEntry>> ReadLatestAs<TEntry>( string id )
         where TEntry : IEntry
      {
         var entries = await ReadLatest( id ).ConfigureAwait( false );

         return entries.Cast<TEntry>();
      }

      public async Task<ReadResult<IEntry>> Read( string id )
      {
         var results = await RetrieveAllForId( id ).ConfigureAwait( false );

         return new ReadResult<IEntry>(
            id,
            results.SelectMany( x => x.Entries )
               .ToList() );
      }

      public async Task<ReadResult<TEntry>> ReadAs<TEntry>( string id )
         where TEntry : IEntry
      {
         var entries = await Read( id ).ConfigureAwait( false );

         return entries.Cast<TEntry>();
      }

      public async Task<ReadResult<IEntry>> Read( string id, DateTime from, DateTime to )
      {
         var results = await RetrieveRangeForId( id, from, to ).ConfigureAwait( false );

         return new ReadResult<IEntry>(
            id,
            results.SelectMany( x => x.Entries )
               .Where( x => x.GetTimestamp() >= from && x.GetTimestamp() < to )
               .ToList() );
      }

      public async Task<ReadResult<TEntry>> ReadAs<TEntry>( string id, DateTime from, DateTime to )
         where TEntry : IEntry
      {
         var entries = await Read( id, from, to ).ConfigureAwait( false );

         return entries.Cast<TEntry>();
      }

      private async Task<int> DeleteForId( string id, DateTime from, DateTime to )
      {
         var retrievals = await RetrieveRangeForId( id, from, to ).ConfigureAwait( false );

         var oldEntities = retrievals.ToDictionary( x => x.Row.RowKey, x => x.Row );
         var oldEntries = retrievals.SelectMany( x => x.Entries ).ToList();

         // remove items between from and to
         int count = oldEntries.RemoveAll( x => x.GetTimestamp() >= from && x.GetTimestamp() < to );

         // create new entities
         var newEntities = CreateTableEntitiesFor( id, oldEntries ).ToDictionary( x => x.RowKey );

         var operations = CreateAtsOperations( newEntities, oldEntities );

         await ExecuteAtsOperatioons( operations ).ConfigureAwait( false );

         return count;
      }

      private async Task<int> DeleteAllForId( string id )
      {
         var retrievals = await RetrieveAllForId( id ).ConfigureAwait( false );

         var oldEntities = retrievals.ToDictionary( x => x.Row.RowKey, x => x.Row );
         var oldEntries = retrievals.SelectMany( x => x.Entries ).ToList();

         // remove items between from and to
         int count = oldEntries.Count;
         oldEntries.Clear();

         // create new entities
         var newEntities = CreateTableEntitiesFor( id, oldEntries ).ToDictionary( x => x.RowKey );

         var operations = CreateAtsOperations( newEntities, oldEntities );

         await ExecuteAtsOperatioons( operations ).ConfigureAwait( false );

         return count;
      }

      private async Task StoreForId( string id, IEnumerable<IEntry> newEntries, DateTime from, DateTime to )
      {
         // retrieve existing entries for this period
         var retrievals = await RetrieveRangeForId( id, from, to ).ConfigureAwait( false );
         var oldEntities = retrievals.ToDictionary( x => x.Row.RowKey, x => x.Row );

         // merge results
         var oldEntries = retrievals.SelectMany( x => x.Entries ).ToList();
         var mergedEntries = MergeSort.Sort(
            collections: new IEnumerable<IEntry>[] { newEntries, oldEntries },
            comparer: new EntryComparer<IEntry>(),
            resolveConflict: x => x.First() ); // prioritize the item from the first collection (new one)

         // create new entities
         var newEntities = CreateTableEntitiesFor( id, mergedEntries ).ToDictionary( x => x.RowKey );

         var operations = CreateAtsOperations( newEntities, oldEntities );

         await ExecuteAtsOperatioons( operations ).ConfigureAwait( false );
      }

      private IEnumerable<AtsOperation> CreateAtsOperations( IDictionary<string, TsdbTableEntity> newEntities, IDictionary<string, TsdbTableEntity> oldEntities )
      {
         List<AtsOperation> operations = new List<AtsOperation>();
         foreach( var createdTableEntity in newEntities )
         {
            TsdbTableEntity previousEntity;
            if( !oldEntities.TryGetValue( createdTableEntity.Key, out previousEntity ) )
            {
               operations.Add( new AtsOperation( createdTableEntity.Value, AtsOperationType.Insert ) );
            }
            else
            {
               operations.Add( new AtsOperation( createdTableEntity.Value, AtsOperationType.Replace ) );
            }
         }

         foreach( var oldTableEntity in oldEntities )
         {
            TsdbTableEntity newEntity;
            if( !newEntities.TryGetValue( oldTableEntity.Key, out newEntity ) )
            {
               operations.Add( new AtsOperation( oldTableEntity.Value, AtsOperationType.Delete ) );
            }
         }
         return operations;
      }

      private async Task ExecuteAtsOperatioons( IEnumerable<AtsOperation> operations )
      {
         List<Task> tasks = new List<Task>();

         foreach( var partitionOperations in operations.GroupBy( x => x.Row.PartitionKey ) )
         {
            var batch = new TableBatchOperation();
            foreach( var operation in partitionOperations )
            {
               switch( operation.OperationType )
               {
                  case AtsOperationType.Insert:
                     batch.InsertOrReplace( operation.Row );
                     break;
                  case AtsOperationType.Replace:
                     batch.InsertOrReplace( operation.Row );
                     break;
                  case AtsOperationType.Delete:
                     batch.Delete( operation.Row );
                     break;
                  default:
                     break;
               }

               // only 40, because the request itself can actually become too big... :)
               if( batch.Count == 40 )
               {
                  tasks.Add( ExecuteBatchOperation( batch ) );

                  batch = new TableBatchOperation();
               }
            }
            if( batch.Count != 0 )
            {
               tasks.Add( ExecuteBatchOperation( batch ) );
            }
         }

         await Task.WhenAll( tasks ).ConfigureAwait( false );
      }

      private async Task ExecuteBatchOperation( TableBatchOperation operation )
      {
         await _setSemaphore.WaitAsync().ConfigureAwait( false );
         try
         {
            var table = await GetTable();

            await table.ExecuteBatchAsync( operation ).ConfigureAwait( false );
         }
         finally
         {
            _setSemaphore.Release();
         }
      }

      private List<TsdbTableEntity> CreateTableEntitiesFor( string id, List<IEntry> entries )
      {
         List<TsdbTableEntity> tableEntities = new List<TsdbTableEntity>();

         var results = AtsSerializer.Serialize( entries, TsdbTableEntity.MaxByteCapacity );
         foreach( var result in results )
         {
            var entity = new TsdbTableEntity();
            entity.SetData( result.Data );
            entity.RowKey = AtsKeyCalculator.CalculateRowKey( result.From );
            entity.PartitionKey = AtsKeyCalculator.CalculatePartitionKey( id, result.From );

            tableEntities.Add( entity );
         }

         return tableEntities;
      }

      private async Task<List<AtsQueryResult>> RetrieveAllForId( string id )
      {
         await _getSemaphore.WaitAsync().ConfigureAwait( false );
         try
         {
            var fullQuery = new TableQuery<TsdbTableEntity>()
               .Where( CreatePartitionFilter( id ) );

            return await PerformQuery( fullQuery, true ).ConfigureAwait( false );
         }
         finally
         {
            _getSemaphore.Release();
         }
      }

      private async Task<List<AtsQueryResult>> RetrieveLatestForId( string id )
      {
         await _getSemaphore.WaitAsync().ConfigureAwait( false );
         try
         {
            var fullQuery = new TableQuery<TsdbTableEntity>()
               .Where( CreatePartitionFilter( id ) )
               .Take( 1 );

            return await PerformQuery( fullQuery, false ).ConfigureAwait( false );
         }
         finally
         {
            _getSemaphore.Release();
         }
      }

      private async Task<List<AtsQueryResult>> RetrieveRangeForId( string id, DateTime from, DateTime to )
      {
         await _getSemaphore.WaitAsync().ConfigureAwait( false );
         try
         {
            var generalQuery = new TableQuery<TsdbTableEntity>()
               .Where( CreateGeneralFilter( id, from, to ) );

            var firstQuery = new TableQuery<TsdbTableEntity>()
               .Where( CreateFirstFilter( id, from ) )
               .Take( 1 );

            var generalQueryTask = PerformQuery( generalQuery, true );
            var firstQueryTask = PerformQuery( firstQuery, false );

            await Task.WhenAll( generalQueryTask, firstQueryTask ).ConfigureAwait( false );

            firstQueryTask.Result.AddRange( generalQueryTask.Result );

            return firstQueryTask.Result;
         }
         finally
         {
            _getSemaphore.Release();
         }
      }

      private async Task<List<AtsQueryResult>> PerformQuery( TableQuery<TsdbTableEntity> query, bool takeAll )
      {
         List<AtsQueryResult> results = new List<AtsQueryResult>();

         TableContinuationToken token = null;
         do
         {
            var table = await GetTable().ConfigureAwait( false );
            var rows = await table.ExecuteQuerySegmentedAsync( query, takeAll ? token : null ).ConfigureAwait( false );
            results.AddRange( rows.Select( x => new AtsQueryResult( x ) ) );
            token = rows.ContinuationToken;
         }
         while( token != null && takeAll );

         return results;
      }

      private async Task<CloudTable> GetTableLocked()
      {
         var table = _client.GetTableReference( _tableName );
         await table.CreateIfNotExistsAsync().ConfigureAwait( false );
         return table;
      }

      private Task<CloudTable> GetTable()
      {
         lock( _sync )
         {
            if( _table == null || _table.IsFaulted || _table.IsCanceled )
            {
               _table = GetTableLocked();
            }
            return _table;
         }
      }

      private static string CreateGeneralFilter( string id, DateTime from, DateTime to )
      {
         var fromRowKey = AtsKeyCalculator.CalculateRowKey( from );
         var toRowKey = AtsKeyCalculator.CalculateRowKey( to );
         var fromPartitionKey = AtsKeyCalculator.CalculatePartitionKey( id, from );
         var toPartitionKey = AtsKeyCalculator.CalculatePartitionKey( id, to.AddTicks( -1 ) ); // -1 tick because it is an approximation value and we use gte operation

         return TableQuery.CombineFilters(
               TableQuery.CombineFilters(
                  TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.LessThanOrEqual, fromPartitionKey ),
                  TableOperators.And,
                  TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.GreaterThanOrEqual, toPartitionKey ) ),
            TableOperators.And,
               TableQuery.CombineFilters(
                  TableQuery.GenerateFilterCondition( "RowKey", QueryComparisons.LessThanOrEqual, fromRowKey ),
                  TableOperators.And,
                  TableQuery.GenerateFilterCondition( "RowKey", QueryComparisons.GreaterThan, toRowKey ) ) );
      }

      private static string CreateFirstFilter( string id, DateTime from )
      {
         var fromRowKey = AtsKeyCalculator.CalculateRowKey( from );
         var fromPartitionKey = AtsKeyCalculator.CalculatePartitionKey( id, from ); // 7125
         var toPartitionKey = AtsKeyCalculator.CalculateMinPartitionKey( id ); // 9999

         return TableQuery.CombineFilters(
            TableQuery.CombineFilters(
               TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.GreaterThanOrEqual, fromPartitionKey ),
               TableOperators.And,
               TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.LessThanOrEqual, toPartitionKey ) ),
            TableOperators.And,
            TableQuery.GenerateFilterCondition( "RowKey", QueryComparisons.GreaterThan, fromRowKey ) );
      }

      private static string CreatePartitionFilter( string id )
      {
         var fromPartitionKey = AtsKeyCalculator.CalculateMaxPartitionKey( id ); // 0000
         var toPartitionKey = AtsKeyCalculator.CalculateMinPartitionKey( id ); // 9999

         return TableQuery.CombineFilters(
            TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.GreaterThanOrEqual, fromPartitionKey ),
            TableOperators.And,
            TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.LessThanOrEqual, toPartitionKey ) );
      }
   }
}
