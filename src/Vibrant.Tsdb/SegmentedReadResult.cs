﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Vibrant.Tsdb
{
   public class SegmentedReadResult<TEntry> : ReadResult<TEntry>
     where TEntry : IEntry
   {
      public SegmentedReadResult( string id, object continuationToken, List<TEntry> entries )
         : base( id, entries )
      {
         ContinuationToken = continuationToken;
      }

      public SegmentedReadResult( string id, object continuationToken )
         : base( id )
      {
         ContinuationToken = continuationToken;
      }

      public object ContinuationToken { get; private set; }

      public new ReadResult<TOutputEntry> As<TOutputEntry>()
         where TOutputEntry : IEntry
      {
         return new ReadResult<TOutputEntry>( Id, Entries.Cast<TOutputEntry>().ToList() );
      }
   }
}