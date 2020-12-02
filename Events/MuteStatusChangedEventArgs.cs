using System.IO.Ports;

namespace MegaMute.Events
{
    public class MuteStatusChangedEventArgs : StatusReceivedEventArgs
    {
        public MuteStatus OldStatus { get; }
        public MuteStatusChangedEventArgs(SerialPort serialPort, PowerStatus powerStatus, MuteStatus newStatus, MuteStatus oldStatus)
            : base(serialPort: serialPort, powerStatus: powerStatus, muteStatus: newStatus)
        {
            this.OldStatus = oldStatus;
        }
    }
}
