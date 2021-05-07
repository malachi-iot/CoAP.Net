using System;
using System.Collections.Generic;
using System.Text;

namespace CoAPNet.Middleware.Experimental
{
    public class SchedulerService
    {
        /// <summary>
        /// FIX: Stop gap -- need to potentially adjust architecture of depependencies
        /// </summary>
        /// <param name="absolute"></param>
        /// <param name="action"></param>
        /// <param name="desc"></param>
        /// <returns></returns>
        public object AddOneShot(DateTime absolute, Action action, string desc)
        {
            return null;
        }
    }
}
