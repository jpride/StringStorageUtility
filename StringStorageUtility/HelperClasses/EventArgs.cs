using System;


namespace StringStorageUtility
{
    public class StringListUpdateEventArgs : EventArgs
    {
        public ushort StringCount {  get; set; }
        public ushort StringIndex { get; set; }
        public string StringValue { get; set; }



        
    }
}
