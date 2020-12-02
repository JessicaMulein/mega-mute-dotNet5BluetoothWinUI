using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Schema;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;

namespace MegaMute
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private ILogger _logger { get; set; }
        private ulong _megaMuteTimeOffset;
        private SerialPort _serialPort { get; set; }
        private byte[] _serialPortBuffer;

        // Public properties
        public string PortName { get; internal set; }
        public DateTimeOffset PortOpenTime { get; internal set; } = DateTimeOffset.UnixEpoch;
        public DateTimeOffset TimeZero { get; internal set; } = DateTimeOffset.UnixEpoch;
        public DateTimeOffset LastPing { get; internal set; } = DateTimeOffset.UnixEpoch;
        public MuteStatus MuteStatus { get; internal set; } = MuteStatus.Disconnected;
        public PowerStatus PowerStatus { get; internal set; } = PowerStatus.Unknown;
        public bool SystemOnline { get; internal set; } = false;
        // Semaphores
        private Mutex _muteStatusLock = new Mutex(initiallyOwned: true, name: nameof(MuteStatus));
        private Mutex _powerStatusLock = new Mutex(initiallyOwned: true, name: nameof(MuteStatus));

        // Events

        public IServiceProvider ServiceProvider { get; private set; }

        public IConfiguration Configuration { get; private set; }

        public Task AppTask { get; }
        public App()
        {
            string[] args = { };
            var hostBuilder = Host.CreateDefaultBuilder(args);

            hostBuilder
                   //.UseContentRoot()
                   .ConfigureAppConfiguration((hostingContext, config) =>
                   {
                       IHostEnvironment env = hostingContext.HostingEnvironment;
                       config.Sources.Clear();
                       config
                           .SetBasePath(env.ContentRootPath)
                           .AddJsonFile(
                               path: "appsettings.json",
                               optional: false,
                               reloadOnChange: true)
                           .AddJsonFile(
                               path: $"appsettings.{env.EnvironmentName}.json",
                               optional: true,
                               reloadOnChange: true)
                           .AddEnvironmentVariables();

                       if (args != null)
                       {
                           config.AddCommandLine(args);
                       }

                       this.Configuration = config.Build();
                   })
                   .UseWindowsService()
                   .ConfigureLogging((hostingContext, logging) =>
                   {
                       logging.AddConsole();
                   })
                   .ConfigureServices((hostingContext, services) =>
                   {
                       services
                        .AddSingleton<MegaMuteWindow>();

                       ILoggerFactory loggerFactory = new LoggerFactory();
                       this._logger = loggerFactory.CreateLogger<App>();
                   });

            this.AppTask = hostBuilder.Build().RunAsync();
        }

        private void updateMuteStatus(MuteStatus muteStatus)
        {
            lock (this._muteStatusLock)
            {
                this.MuteStatus = muteStatus;
            }
        }

        private void updatePowerStatus(PowerStatus powerStatus)
        {
            lock (this._powerStatusLock)
            {
                this.PowerStatus = powerStatus;
            }
        }

        public static byte[] Combine(byte[] first, byte[] second)
        {
            byte[] ret = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, ret, 0, first.Length);
            Buffer.BlockCopy(second, 0, ret, first.Length, second.Length);
            return ret;
        }

        PingResponse parsePing(string pingResponse)
        {
            string trimmed = pingResponse.Replace("\n", "").Trim();
            if (trimmed.Length == 0)
                return null;

            JsonTextReader reader = new JsonTextReader(new StringReader(trimmed));

            // { "time":385000, "command":"p"}
            JSchemaValidatingReader validatingReader = new JSchemaValidatingReader(reader);
            validatingReader.Schema = JSchema.Parse(@"{
              'type': 'object',
              'properties': {
                'command': {'type':'string'},
                'time': {'type':'integer'}
              }
            }");
            JsonSerializer serializer = new JsonSerializer();
            return serializer.Deserialize<PingResponse>(validatingReader);
        }

        ResponseRoot parseResponse(string stateResponse)
        {
            string trimmed = stateResponse.Replace("\n", "").Trim();
            if (trimmed.Length == 0)
                return null;

            JsonTextReader reader = new JsonTextReader(new StringReader(trimmed));
            // { "c":0, "t":385000, "s":[{ "p":0, "v":1, "tc":42498, "tp":37195},{ "p":1, "v":1, "tc":25053, "tp":17403},{ "p":2, "v":1, "tc":21906, "tp":19430},{ "p":3, "v":1, "tc":1, "tp":0}]}
            JSchemaValidatingReader validatingReader = new JSchemaValidatingReader(reader);
            validatingReader.Schema = JSchema.Parse(@"{
              'type': 'object',
              'properties': {
                'c': {'type':'integer'},
                't': {'type':'integer'},
                's': {
                  'type': 'array',
                  'items': {'type':'object'},
                  'properties': {
                    'p': {'type':'integer'},
                    'v': {'type':'integer'},
                    'tc': {'type':'integer'},
                    'tp': {'type':'integer'}
                  }
                }
              }
            }");
            JsonSerializer serializer = new JsonSerializer();
            return serializer.Deserialize<ResponseRoot>(validatingReader);
        }

        private void SerialPort_DataReceived(byte[] buffer)
        {
            DateTimeOffset dateTimeOffsetStartRead = DateTimeOffset.Now;

            lock (this._serialPortBuffer)
            {
                List<string> tmpLines = new List<string>(System.Text.ASCIIEncoding.ASCII.GetString(Combine(_serialPortBuffer, buffer)).Replace("\r", "").Split(separator: new char[] { '\n' }));

                bool processedAny = false;
                int highestProcessed = 0;
                // first entry may be incomplete, be ok skipping it if there are more, but keep it if there are only one
                for (highestProcessed = 0; highestProcessed < tmpLines.Count; highestProcessed++)
                {
                    try
                    {
                        if (tmpLines[highestProcessed].Contains("\"c\":"))
                        {
                            ResponseRoot responseRoot = parseResponse(tmpLines[highestProcessed]);
                            updateMuteStatus(responseRoot.toMuteStatus());
                            updatePowerStatus(responseRoot.toPowerStatus());
                            _logger.LogInformation(message: "status: " + this.MuteStatus.ToString());
                            if (TimeZero.Equals(DateTimeOffset.UnixEpoch))
                            {
                                _megaMuteTimeOffset = responseRoot.t;
                                TimeZero = dateTimeOffsetStartRead;
                            }
                            if (responseRoot.c == 1) _logger.LogInformation(message: "CHANGE status update at millis since power on {t}", responseRoot.t);
                            else _logger.LogInformation(message: "interval push status update at millis since power on {t}", responseRoot.t);
                        }
                        else if (tmpLines[highestProcessed].Contains("\"command\":"))
                        {
                            PingResponse pingResponse = parsePing(tmpLines[highestProcessed]);
                            LastPing = dateTimeOffsetStartRead;
                            _megaMuteTimeOffset = pingResponse.time;
                            TimeZero = dateTimeOffsetStartRead;
                            _logger.LogInformation(message: "PING response to command {c} at millis since power on {t}", pingResponse.command, pingResponse.time);
                        }
                        else
                        {
                            continue;
                        }
                        processedAny = true;
                    }
                    catch (Exception e)
                    {
                        if (highestProcessed == 0) continue;
                        else break;
                    }
                }
                // if we processed any, remove any below where we are
                if (processedAny)
                {
                    tmpLines.RemoveRange(0, highestProcessed);
                }
                this._serialPortBuffer = System.Text.ASCIIEncoding.ASCII.GetBytes(String.Join("\n", tmpLines));
            }
        }

        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }

        public override string ToString()
        {
            return base.ToString();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            this._serialPort.Close();
            this._serialPort.Dispose();
            this._serialPortBuffer = null;
            this.AppTask.Dispose();
            base.OnExit(e);
        }

        protected override void OnFragmentNavigation(FragmentNavigationEventArgs e)
        {
            base.OnFragmentNavigation(e);
        }

        protected override void OnLoadCompleted(NavigationEventArgs e)
        {
            base.OnLoadCompleted(e);
        }

        protected override void OnNavigated(NavigationEventArgs e)
        {
            base.OnNavigated(e);
        }

        protected override void OnNavigating(NavigatingCancelEventArgs e)
        {
            base.OnNavigating(e);
        }

        protected override void OnNavigationFailed(NavigationFailedEventArgs e)
        {
            base.OnNavigationFailed(e);
        }

        protected override void OnNavigationProgress(NavigationProgressEventArgs e)
        {
            base.OnNavigationProgress(e);
        }

        protected override void OnNavigationStopped(NavigationEventArgs e)
        {
            base.OnNavigationStopped(e);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            _serialPortBuffer = new byte[] { };
            var serialPortConfiguration = Configuration.GetSection("SerialPort");
            this.PortName = serialPortConfiguration["Name"];
            if (this.PortName.Length == 0)
            {
                _logger.LogInformation("Unable to locate 'Name' field in configuration");
                throw new Exception();
            }
            _logger.LogInformation("Read configuration SerialPort.Name: {string}", this.PortName);

            this._serialPort = new SerialPort(
                portName: this.PortName,
                baudRate: 115200,
                parity: Parity.None,
                dataBits: 8,
                stopBits: StopBits.One);
            _logger.LogInformation("Serial Port instantiated: {time}", DateTimeOffset.Now);
            this._serialPort.Handshake = Handshake.None;
            this._serialPort.DtrEnable = false;
            this._serialPort.Open();

            PortOpenTime = DateTimeOffset.Now;
            TimeZero = PortOpenTime;
            _logger.LogInformation("Serial Port opened: {time}", PortOpenTime);

            // https://www.sparxeng.com/blog/software/must-use-net-system-io-ports-serialport
            byte[] delegateBuffer = new byte[1024];
            Action kickoffRead = null;
            kickoffRead = delegate
            {
                this._serialPort.BaseStream.BeginRead(delegateBuffer, 0, delegateBuffer.Length, delegate (IAsyncResult ar)
                {
                    try
                    {
                        int actualLength = this._serialPort.BaseStream.EndRead(ar);
                        byte[] received = new byte[actualLength];
                        Buffer.BlockCopy(delegateBuffer, 0, received, 0, actualLength);
                        SerialPort_DataReceived(received);
                    }
                    catch (IOException exc)
                    {
                        //handleAppSerialError(exc);
                    }

                    // periodic hardware ping
                    // TODO: task with timeout on response.
                    if (DateTimeOffset.Now.Subtract(LastPing).TotalSeconds >= 60)
                        this._serialPort.Write("p");
                    kickoffRead();
                }, null);
            };
            kickoffRead();

            base.OnStartup(e);
        }
    }
}
