using System.IO.Ports;

namespace MegaMute.Events
{
    public class StatusReceivedEventArgs
    {
        public SerialPort SerialPort { get; }
        public PowerStatus PowerStatus { get; }
        public MuteStatus MuteStatus { get; }

        public StatusReceivedEventArgs(SerialPort serialPort, PowerStatus powerStatus, MuteStatus muteStatus)
        {
            this.SerialPort = serialPort;
            this.MuteStatus = muteStatus;
        }
    }
}
