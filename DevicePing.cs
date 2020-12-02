using System;

namespace MegaMute
{
    public class DevicePing
    {
        public DevicePing()
        {
            Requested = DateTimeOffset.Now;
        }

        public DateTimeOffset Requested { get; }
        public DateTimeOffset Transmitted { get; internal set; }
        public DateTimeOffset Recieved { get; internal set; }
    }
}
