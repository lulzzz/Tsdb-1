﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Vibrant.Tsdb
{
   public interface IPerformanceStorage : IStorage
   {
      //Task<ReadResult<IEntry>> ReadAndDelete( string id );
   }
}
