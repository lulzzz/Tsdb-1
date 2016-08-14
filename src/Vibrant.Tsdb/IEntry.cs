﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Vibrant.Tsdb
{
   public interface IEntry
   {
      DateTime GetTimestamp();

      void SetTimestamp( DateTime timestamp );
   }
}
