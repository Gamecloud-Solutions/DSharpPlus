﻿using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.VoiceNext.Codec;
using DSharpPlus.VoiceNext.VoiceEntities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DSharpPlus.VoiceNext
{
    internal delegate void VoiceDisconnectedEventHandler(DiscordGuild guild);

    /// <summary>
    /// VoiceNext connection to a voice channel.
    /// </summary>
    public sealed class VoiceNextConnection : IDisposable
    {
        /// <summary>
        /// Triggered whenever a user speaks in the connected voice channel.
        /// </summary>
        public event AsyncEventHandler<UserSpeakingEventArgs> UserSpeaking
        {
            add { this._user_speaking.Register(value); }
            remove { this._user_speaking.Unregister(value); }
        }
        private AsyncEvent<UserSpeakingEventArgs> _user_speaking;

#if !NETSTANDARD1_1
        /// <summary>
        /// Triggered whenever voice data is received from the connected voice channel.
        /// </summary>
        public event AsyncEventHandler<VoiceReceivedEventArgs> VoiceReceived
        {
            add { this._voice_received.Register(value); }
            remove { this._voice_received.Unregister(value); }
        }
        private AsyncEvent<VoiceReceivedEventArgs> _voice_received;
#endif

        /// <summary>
        /// Triggered whenever voice WebSocket throws an exception.
        /// </summary>
        public event AsyncEventHandler<SocketErrorEventArgs> VoiceSocketError
        {
            add { this._voice_socket_error.Register(value); }
            remove { this._voice_socket_error.Unregister(value); }
        }
        private AsyncEvent<SocketErrorEventArgs> _voice_socket_error;

        internal event VoiceDisconnectedEventHandler VoiceDisconnected;

        private const string VOICE_MODE = "xsalsa20_poly1305";
        private static DateTime UnixEpoch { get { return _unix_epoch.Value; } }
        private static Lazy<DateTime> _unix_epoch;

        private DiscordClient Discord { get; }
        private DiscordGuild Guild { get; }
        private ConcurrentDictionary<uint, ulong> SSRCMap { get; }

        private BaseUdpClient UdpClient { get; }
        private BaseWebSocketClient VoiceWs { get; set; }
        private Task HeartbeatTask { get; set; }
        private int HeartbeatInterval { get; set; }
        private DateTime LastHeartbeat { get; set; }

        private CancellationTokenSource TokenSource { get; }
        private CancellationToken Token => this.TokenSource.Token;

        private VoiceServerUpdatePayload ServerData { get; set; }
        private VoiceStateUpdatePayload StateData { get; set; }
        private bool Resume { get; set; }

        private VoiceNextConfiguration Configuration { get; }
        private OpusCodec Opus { get; set; }
        private SodiumCodec Sodium { get; set; }
        private RtpCodec Rtp { get; set; }
        private double SynchronizerTicks { get; set; }
        private double SynchronizerResolution { get; set; }
        private TimeSpan UdpLatency { get; }

        private ushort Sequence { get; set; }
        private uint Timestamp { get; set; }
        private uint SSRC { get; set; }
        private byte[] Key { get; set; }
#if !NETSTANDARD1_1
        private IpEndpoint DiscoveredEndpoint { get; set; }
#endif
        private ConnectionEndpoint ConnectionEndpoint { get; set; }

        private TaskCompletionSource<bool> ReadyWait { get; set; }
        private bool IsInitialized { get; set; }
        private bool IsDisposed { get; set; }

        private TaskCompletionSource<bool> PlayingWait { get; set; }
        private SemaphoreSlim PlaybackSemaphore { get; set; }

#if !NETSTANDARD1_1
        private Task ReceiverTask { get; set; }
#endif

        /// <summary>
        /// Gets whether this connection is still playing audio.
        /// </summary>
        public bool IsPlaying => this.PlaybackSemaphore.CurrentCount == 0 || (this.PlayingWait != null && !this.PlayingWait.Task.IsCompleted);

        /// <summary>
        /// Gets the websocket round-trip time in ms.
        /// </summary>
        public int Ping => Volatile.Read(ref this._ping);
        private int _ping = 0;

        /// <summary>
        /// Gets the channel this voice client is connected to.
        /// </summary>
        public DiscordChannel Channel { get; private set; }

        internal VoiceNextConnection(DiscordClient client, DiscordGuild guild, DiscordChannel channel, VoiceNextConfiguration config, VoiceServerUpdatePayload server, VoiceStateUpdatePayload state)
        {
            this.Discord = client;
            this.Guild = guild;
            this.Channel = channel;
            this.SSRCMap = new ConcurrentDictionary<uint, ulong>();

            this._user_speaking = new AsyncEvent<UserSpeakingEventArgs>(this.Discord.EventErrorHandler, "USER_SPEAKING");
#if !NETSTANDARD1_1
            this._voice_received = new AsyncEvent<VoiceReceivedEventArgs>(this.Discord.EventErrorHandler, "VOICE_RECEIVED");
#endif
            this._voice_socket_error = new AsyncEvent<SocketErrorEventArgs>(this.Discord.EventErrorHandler, "VOICE_WS_ERROR");
            this.TokenSource = new CancellationTokenSource();

            this.Configuration = config;
            this.Opus = new OpusCodec(48000, 2, this.Configuration.VoiceApplication);
            this.Sodium = new SodiumCodec();
            this.Rtp = new RtpCodec();
            this.UdpLatency = TimeSpan.FromMilliseconds(0.1);

            this.ServerData = server;
            this.StateData = state;

            var eps = this.ServerData.Endpoint;
            var epi = eps.LastIndexOf(':');
            var eph = string.Empty;
            var epp = 80;
            if (epi != -1)
            {
                eph = eps.Substring(0, epi);
                epp = int.Parse(eps.Substring(epi + 1));
            }
            else
            {
                eph = eps;
            }
            this.ConnectionEndpoint = new ConnectionEndpoint { Hostname = eph, Port = epp };

            this.ReadyWait = new TaskCompletionSource<bool>();
            this.IsInitialized = false;
            this.IsDisposed = false;

            this.PlayingWait = null;
            this.PlaybackSemaphore = new SemaphoreSlim(1, 1);

            this.UdpClient = BaseUdpClient.Create();
            this.VoiceWs = BaseWebSocketClient.Create();
            this.VoiceWs.OnDisconnect += this.VoiceWS_SocketClosed;
            this.VoiceWs.OnMessage += this.VoiceWS_SocketMessage;
            this.VoiceWs.OnConnect += this.VoiceWS_SocketOpened;
            this.VoiceWs.OnError += this.VoiceWs_SocketErrored;
        }

        static VoiceNextConnection()
        {
            _unix_epoch = new Lazy<DateTime>(() => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        ~VoiceNextConnection()
        {
            this.Dispose();
        }

        /// <summary>
        /// Connects to the specified voice channel.
        /// </summary>
        /// <returns>A task representing the connection operation.</returns>
        internal async Task ConnectAsync()
        {
            await Task.Run(() => this.VoiceWs.ConnectAsync($"wss://{this.ConnectionEndpoint.Hostname}/?encoding=json&v=3")).ConfigureAwait(false);
        }

        internal Task StartAsync()
        {
            // Let's announce our intentions to the server
            var vdp = new VoiceDispatch();

            if (!this.Resume)
            {
                vdp.OpCode = 0;
                vdp.Payload = new VoiceIdentifyPayload
                {
                    ServerId = this.ServerData.GuildId,
                    UserId = this.StateData.UserId.Value,
                    SessionId = this.StateData.SessionId,
                    Token = this.ServerData.Token
                };
                this.Resume = true;
            }
            else
            {
                vdp.OpCode = 7;
                vdp.Payload = new VoiceIdentifyPayload
                {
                    ServerId = this.ServerData.GuildId,
                    SessionId = this.StateData.SessionId,
                    Token = this.ServerData.Token
                };
            }
            var vdj = JsonConvert.SerializeObject(vdp, Formatting.None);
            this.VoiceWs.SendMessage(vdj);

            return Task.Delay(0);
        }

        internal async Task WaitForReady()
        {
            await this.ReadyWait.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Encodes, encrypts, and sends the provided PCM data to the connected voice channel.
        /// </summary>
        /// <param name="pcm">PCM data to encode, encrypt, and send.</param>
        /// <param name="blocksize">Millisecond length of the PCM data.</param>
        /// <param name="bitrate">Bitrate of the PCM data.</param>
        /// <returns>Task representing the sending operation.</returns>
        public async Task SendAsync(byte[] pcm, int blocksize, int bitrate = 16)
        {
            if (!this.IsInitialized)
                throw new InvalidOperationException("The connection is not initialized");

            await this.PlaybackSemaphore.WaitAsync();
            if (this.SynchronizerTicks == 0)
            {
                this.SynchronizerTicks = Stopwatch.GetTimestamp();
                this.SynchronizerResolution = (Stopwatch.Frequency * 0.02);
                this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", $"Timer accuracy: {Stopwatch.Frequency.ToString("#,##0")}/{this.SynchronizerResolution} (high resolution? {Stopwatch.IsHighResolution})", DateTime.Now);
            }

            var rtp = this.Rtp.Encode(this.Sequence, this.Timestamp, this.SSRC);

            var dat = this.Opus.Encode(pcm, 0, pcm.Length, bitrate);
            dat = this.Sodium.Encode(dat, this.Rtp.MakeNonce(rtp), this.Key);
            dat = this.Rtp.Encode(rtp, dat);

            await this.SendSpeakingAsync(true);
            await this.UdpClient.SendAsync(dat, dat.Length);

            this.Sequence++;
            this.Timestamp += 48 * (uint)blocksize;

            // Provided by Laura#0090 (214796473689178133); this is Python, but adaptable:
            // 
            // delay = max(0, self.delay + ((start_time + self.delay * loops) + - time.time()))
            // 
            // self.delay
            //   sample size
            // start_time
            //   time since streaming started
            // loops
            //   number of samples sent
            // time.time()
            //   DateTime.Now

            //await Task.Delay(ts);
            while (Stopwatch.GetTimestamp() - this.SynchronizerTicks < this.SynchronizerResolution) ;
            this.SynchronizerTicks += this.SynchronizerResolution;

            this.PlaybackSemaphore.Release();
        }

#if !NETSTANDARD1_1
        private async Task VoiceReceiverTask()
        {
            var token = this.Token;
            var client = this.UdpClient;
            while (!token.IsCancellationRequested)
            {
                if (client.DataAvailable <= 0)
                    continue;

                byte[] data = null, header = null;
                ushort seq = 0;
                uint ts = 0, ssrc = 0;
                try
                {
                    data = await client.ReceiveAsync();

                    header = new byte[RtpCodec.SIZE_HEADER];
                    data = this.Rtp.Decode(data, header);

                    var nonce = this.Rtp.MakeNonce(header);
                    data = this.Sodium.Decode(data, nonce, this.Key);

                    // following is thanks to code from Eris
                    // https://github.com/abalabahaha/eris/blob/master/lib/voice/VoiceConnection.js#L623
                    var doff = 0;
                    this.Rtp.Decode(header, out seq, out ts, out ssrc, out var has_ext);
                    if (has_ext)
                    {
                        if (data[0] == 0xBE && data[1] == 0xDE)
                        {
                            // RFC 5285, 4.2 One-Byte header
                            // http://www.rfcreader.com/#rfc5285_line186

                            var hlen = data[2] << 8 | data[3];
                            var i = 4;
                            for (; i < hlen + 4; i++)
                            {
                                var b = data[i];
                                // This is unused(?)
                                //var id = (b >> 4) & 0x0F;
                                var len = (b & 0x0F) + 1;
                                i += len;
                            }
                            while (data[i] == 0)
                                i++;
                            doff = i;
                        }
                        // TODO: consider implementing RFC 5285, 4.3. Two-Byte Header
                    }

                    data = this.Opus.Decode(data, doff, data.Length - doff);
                }
                catch { continue; }

                // TODO: wait for ssrc map?
                DiscordUser user = null;
                if (this.SSRCMap.ContainsKey(ssrc))
                {
                    var id = this.SSRCMap[ssrc];
                    if (this.Guild != null)
                        user = this.Guild._members.FirstOrDefault(xm => xm.Id == id) ?? await this.Guild.GetMemberAsync(id);

                    if (user == null)
                        user = this.Discord.InternalGetCachedUser(id);

                    if (user == null)
                        user = new DiscordUser { Discord = this.Discord, Id = id };
                }

                await this._voice_received.InvokeAsync(new VoiceReceivedEventArgs(this.Discord)
                {
                    SSRC = ssrc,
                    Voice = new ReadOnlyCollection<byte>(data),
                    VoiceLength = 20,
                    User = user
                });
            }
        }
#endif

        /// <summary>
        /// Sends a speaking status to the connected voice channel.
        /// </summary>
        /// <param name="speaking">Whether the current user is speaking or not.</param>
        /// <returns>A task representing the sending operation.</returns>
        public async Task SendSpeakingAsync(bool speaking = true)
        {
            if (!this.IsInitialized)
                throw new InvalidOperationException("The connection is not initialized");

            if (!speaking)
            {
                this.SynchronizerTicks = 0;
                if (this.PlayingWait != null)
                    this.PlayingWait.SetResult(true);
            }
            else
            {
                if (this.PlayingWait == null || this.PlayingWait.Task.IsCompleted)
                    this.PlayingWait = new TaskCompletionSource<bool>();
            }

            var pld = new VoiceDispatch
            {
                OpCode = 5,
                Payload = new VoiceSpeakingPayload
                {
                    Speaking = speaking,
                    Delay = 0
                }
            };

            var plj = JsonConvert.SerializeObject(pld, Formatting.None);
            await Task.Run(() => this.VoiceWs.SendMessage(plj));
        }

        /// <summary>
        /// Asynchronously waits for playback to be finished. Playback is finished when speaking = false is signalled.
        /// </summary>
        /// <returns>A task representing the waiting operation.</returns>
        public async Task WaitForPlaybackFinishAsync()
        {
            if (this.PlayingWait != null)
                await this.PlayingWait.Task;
        }

        /// <summary>
        /// Disconnects and disposes this voice connection.
        /// </summary>
        public void Disconnect() =>
            this.Dispose();

        /// <summary>
        /// Disconnects and disposes this voice connection.
        /// </summary>
        public void Dispose()
        {
            if (this.IsDisposed)
                return;

            this.TokenSource.Cancel();

            this.IsDisposed = true;
            this.IsInitialized = false;
            try
            {
                this.VoiceWs.InternalDisconnectAsync(null).GetAwaiter().GetResult();
                this.UdpClient.Close();
            }
            catch (Exception)
            { }

            this.Opus?.Dispose();
            this.Opus = null;
            this.Sodium = null;
            this.Rtp = null;

            if (this.VoiceDisconnected != null)
                this.VoiceDisconnected(this.Guild);
        }

        private async Task Heartbeat()
        {
            await Task.Yield();

            while (true)
            {
                try
                {
                    this.Token.ThrowIfCancellationRequested();

                    var dt = DateTime.Now;
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "Sent heartbeat", dt);

                    var hbd = new VoiceDispatch
                    {
                        OpCode = 3,
                        Payload = UnixTimestamp(dt)
                    };
                    var hbj = JsonConvert.SerializeObject(hbd);
                    this.VoiceWs.SendMessage(hbj);

                    this.LastHeartbeat = dt;
                    await Task.Delay(this.HeartbeatInterval);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task Stage1()
        {
            // Begin heartbeating
            this.HeartbeatTask = Task.Run(this.Heartbeat);

#if !NETSTANDARD1_1
            // IP Discovery
            this.UdpClient.Setup(this.ConnectionEndpoint);
            var pck = new byte[70];
            Array.Copy(BitConverter.GetBytes(this.SSRC), 0, pck, pck.Length - 4, 4);
            await this.UdpClient.SendAsync(pck, pck.Length);
            var ipd = await this.UdpClient.ReceiveAsync();
            var ipe = Array.IndexOf<byte>(ipd, 0, 4);
            var ip = new UTF8Encoding(false).GetString(ipd, 4, ipe - 4);
            var port = BitConverter.ToUInt16(ipd, ipd.Length - 2);
            this.DiscoveredEndpoint = new IpEndpoint { Address = System.Net.IPAddress.Parse(ip), Port = port };
#endif

            // Ready
            var vsp = new VoiceDispatch
            {
                OpCode = 1,
                Payload = new VoiceSelectProtocolPayload
                {
                    Protocol = "udp",
                    Data = new VoiceSelectProtocolPayloadData
                    {
#if !NETSTANDARD1_1
                        Address = this.DiscoveredEndpoint.Address.ToString(),
                        Port = (ushort)this.DiscoveredEndpoint.Port,
#else
                        Address = "0.0.0.0",
                        Port = 0,
#endif
                        Mode = VOICE_MODE
                    }
                }
            };
            var vsj = JsonConvert.SerializeObject(vsp, Formatting.None);
            this.VoiceWs.SendMessage(vsj);

#if !NETSTANDARD1_1
            if (this.Configuration.EnableIncoming)
                this.ReceiverTask = Task.Run(this.VoiceReceiverTask, this.Token);
#endif
        }

        private Task Stage2()
        {
            this.IsInitialized = true;
            this.ReadyWait.SetResult(true);
            return Task.Delay(0);
        }

        private async Task HandleDispatch(JObject jo)
        {
            var opc = (int)jo["op"];
            var opp = jo["d"] as JObject;

            switch (opc)
            {
                case 2:
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP2 received", DateTime.Now);
                    var vrp = opp.ToObject<VoiceReadyPayload>();
                    this.SSRC = vrp.SSRC;
                    this.ConnectionEndpoint = new ConnectionEndpoint { Hostname = this.ConnectionEndpoint.Hostname, Port = vrp.Port };
                    this.HeartbeatInterval = vrp.HeartbeatInterval;
                    await this.Stage1();
                    break;

                case 4:
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP4 received", DateTime.Now);
                    var vsd = opp.ToObject<VoiceSessionDescriptionPayload>();
                    this.Key = vsd.SecretKey;
                    await this.Stage2();
                    break;

                case 5:
                    // Don't spam OP5
                    //this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP5 received", DateTime.Now);
                    var spd = opp.ToObject<VoiceSpeakingPayload>();
                    var spk = new UserSpeakingEventArgs(this.Discord)
                    {
                        Speaking = spd.Speaking,
                        SSRC = spd.SSRC.Value,
                        User = this.Discord.InternalGetCachedUser(spd.UserId.Value)
                    };
                    if (!this.SSRCMap.ContainsKey(spk.SSRC))
                        this.SSRCMap.AddOrUpdate(spk.SSRC, spk.User.Id, (k, v) => spk.User.Id);
                    await this._user_speaking.InvokeAsync(spk);
                    break;

                case 3:
                case 6:
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP3 or OP6 received", DateTime.Now);
                    var dt = DateTime.Now;
                    var ping = (int)(dt - this.LastHeartbeat).TotalMilliseconds;
                    Volatile.Write(ref this._ping, ping);
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", $"Received voice heartbeat ACK, ping {ping.ToString("#,###")}ms", dt);
                    this.LastHeartbeat = dt;
                    break;

                case 8:
                    // this sends a heartbeat interval that appears to be consistent with regular GW hello
                    // however opcodes don't match (8 != 10)
                    // so we suppress it so that users are not alerted
                    // HELLO
                    break;

                case 9:
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP9 received, starting new session", DateTime.Now);
                    this.Resume = false;
                    await this.StartAsync();
                    break;

                default:
                    this.Discord.DebugLogger.LogMessage(LogLevel.Warning, "VoiceNext", $"Unknown opcode received: {opc}", DateTime.Now);
                    break;
            }
        }

        private async Task VoiceWS_SocketClosed(SocketDisconnectEventArgs e)
        {
            this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", $"Voice socket closed ({e.CloseCode}, '{e.CloseMessage}')", DateTime.Now);
            this.Dispose();

            if (!this.IsDisposed)
            {
                this.VoiceWs = BaseWebSocketClient.Create();
                this.VoiceWs.OnDisconnect += this.VoiceWS_SocketClosed;
                this.VoiceWs.OnMessage += this.VoiceWS_SocketMessage;
                this.VoiceWs.OnConnect += this.VoiceWS_SocketOpened;
                await this.StartAsync();
            }
        }

        private async Task VoiceWS_SocketMessage(SocketMessageEventArgs e)
        {
            await this.HandleDispatch(JObject.Parse(e.Message));
        }

        private async Task VoiceWS_SocketOpened()
        {
            await this.StartAsync();
        }

        private Task VoiceWs_SocketErrored(SocketErrorEventArgs e) =>
            this._voice_socket_error.InvokeAsync(new SocketErrorEventArgs(this.Discord) { Exception = e.Exception });

        private static uint UnixTimestamp(DateTime dt)
        {
            var ts = dt - UnixEpoch;
            var sd = ts.TotalSeconds;
            var si = (uint)sd;
            return si;
        }
    }
}
