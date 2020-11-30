namespace MegaMute
{
    public class ResponseState
    {
        public int p { get; set; } // port: 0-3 (4 reserved)
        public int v { get; set; } // value: 0/1
        public ulong tc { get; set; } // time current status was set
        public ulong tp { get; set; } // time previous status was set
    }
}
