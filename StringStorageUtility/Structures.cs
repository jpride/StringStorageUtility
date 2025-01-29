using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StringStorageUtility
{
    public class JsonStringObject
    {
        public string LastUpdated { get; set; }
        public long StringCount { get; set; }
        public List<string> Strings { get; set; }
    }
}
