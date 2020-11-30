using System.Collections.Generic;

namespace MegaMute
{

    public class ResponseRoot
    {
        public List<ResponseState> s { get; set; } // state
        public ulong t { get; set; } // microseconds since Pon
        public int c { get; set; } // changed 0/1
        public MuteStatus toMuteStatus()
        {
            int highestPort = -1;
            foreach (ResponseState responseState in this.s)
            {
                if ((responseState.p == 3) && (responseState.v == 0) && (highestPort < 3)) highestPort = 3;
                else if ((responseState.p == 2) && (responseState.v == 0) && (highestPort < 2)) highestPort = 2;
                else if ((responseState.p == 1) && (responseState.v == 0) && (highestPort < 1)) highestPort = 1;

                if ((responseState.p == 0) && (responseState.v == 1) && (highestPort == -1))
                    highestPort = 0;
            }
            switch (highestPort)
            {
                case 0:
                    return MuteStatus.Online;
                case 1:
                    return MuteStatus.Armed;
                case 2:
                    return MuteStatus.RemoteEnabled;
                case 3:
                    return MuteStatus.OnAir;
                default:
                    return MuteStatus.Error;
            }

        }
    }
}
