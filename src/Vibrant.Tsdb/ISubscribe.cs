﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Vibrant.Tsdb
{
   public interface ISubscribe
   {
      Task<Func<Task>> Subscribe( IEnumerable<string> ids, Action<List<IEntry>> callback );

      Task<Func<Task>> SubscribeToAll( Action<List<IEntry>> callback );
   }
}