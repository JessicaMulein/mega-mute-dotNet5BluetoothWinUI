using System.IO.Ports;

namespace MegaMute.Events
{
    public class PingReceivedEventArgs
    {
        public SerialPort SerialPort { get; }
        public DevicePing DevicePing { get; }

        public PingReceivedEventArgs(SerialPort serialPort, DevicePing devicePing)
        {
            this.SerialPort = serialPort;
            this.DevicePing = devicePing;
        }
    }
}
