using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Schema;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace MegaMute
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;

        private DateTimeOffset _portOpenTime;
        private DateTimeOffset _timeZero = DateTimeOffset.UnixEpoch;
        private DateTimeOffset _lastPing = DateTimeOffset.UnixEpoch;
        private ulong _megaMuteTimeOffset;
        private IConfiguration Configuration;
        public string PortName { get; }

        public SerialPort SerialPort { get; }

        private byte[] _buffer;


        public Worker(IConfiguration configuration, ILogger<Worker> logger)
        {
            _buffer = new byte[] { };
            _logger = logger;
            this.Configuration = configuration;
            this.PortName = configuration.GetSection("SerialPort")["Name"];
            _logger.LogInformation("Read configuration SerialPort.Name: {string}", this.PortName);
            this.SerialPort = new SerialPort(
                portName: this.PortName,
                baudRate: 115200,
                parity: Parity.None,
                dataBits: 8,
                stopBits: StopBits.One);
            _logger.LogInformation("Serial Port instantiated: {time}", DateTimeOffset.Now);
            this.SerialPort.Handshake = Handshake.None;
            this.SerialPort.Open();
            _portOpenTime = DateTimeOffset.Now;
            _timeZero = _portOpenTime;
            _logger.LogInformation("Serial Port opened: {time}", _portOpenTime);

            // https://www.sparxeng.com/blog/software/must-use-net-system-io-ports-serialport
            byte[] delegateBuffer = new byte[1024];
            Action kickoffRead = null;
            kickoffRead = delegate
            {
                this.SerialPort.BaseStream.BeginRead(delegateBuffer, 0, delegateBuffer.Length, delegate (IAsyncResult ar)
                {
                    try
                    {
                        int actualLength = this.SerialPort.BaseStream.EndRead(ar);
                        byte[] received = new byte[actualLength];
                        Buffer.BlockCopy(delegateBuffer, 0, received, 0, actualLength);
                        SerialPort_DataReceived(received);
                    }
                    catch (IOException exc)
                    {
                        //handleAppSerialError(exc);
                    }
                    if (DateTimeOffset.Now.Subtract(_lastPing).TotalSeconds >= 60)
                        this.SerialPort.Write("p");
                    kickoffRead();
                }, null);
            };
            kickoffRead();
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

            lock (this._buffer)
            {
                List<string> tmpLines = new List<string>(System.Text.ASCIIEncoding.ASCII.GetString(Combine(_buffer, buffer)).Replace("\r", "").Split(separator: new char[] { '\n' }));

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
                            if (_timeZero.Equals(DateTimeOffset.UnixEpoch))
                            {
                                _megaMuteTimeOffset = responseRoot.t;
                                _timeZero = dateTimeOffsetStartRead;
                            }
                            if (responseRoot.c == 1) _logger.LogInformation(message: "CHANGE status update at millis since power on {t}", responseRoot.t);
                            else _logger.LogInformation(message: "interval push status update at millis since power on {t}", responseRoot.t);
                            _logger.LogInformation(message: "status: " + responseRoot.toMuteStatus().ToString());
                        }
                        else if (tmpLines[highestProcessed].Contains("\"command\":"))
                        {
                            PingResponse pingResponse = parsePing(tmpLines[highestProcessed]);
                            _lastPing = dateTimeOffsetStartRead;
                            _megaMuteTimeOffset = pingResponse.time;
                            _timeZero = dateTimeOffsetStartRead;
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
                this._buffer = System.Text.ASCIIEncoding.ASCII.GetBytes(String.Join("\n", tmpLines));
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(1000, stoppingToken);
            }
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // DO YOUR STUFF HERE
            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // DO YOUR STUFF HERE
            await base.StopAsync(cancellationToken);
        }

        public override void Dispose()
        {
            this.SerialPort.Close();
            this.SerialPort.Dispose();
            this._buffer = null;
        }
    }
}
