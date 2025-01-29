using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StringStorageUtility
{
    public class StringListUpdateEventArgs : EventArgs
    {
        public ushort StringCount {  get; set; }

        public ushort StringIndex { get; set; }
        public string StringValue { get; set; }
    }
}
