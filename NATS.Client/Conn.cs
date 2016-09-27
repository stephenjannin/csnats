﻿// Copyright 2015 Apcera Inc. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;

// disable XML comment warnings
#pragma warning disable 1591

namespace NATS.Client
{
    /// <summary>
    /// State of the connection.
    /// </summary>
    public enum ConnState
    {
        DISCONNECTED = 0,
        CONNECTED,
        CLOSED,
        RECONNECTING,
        CONNECTING
    }

    internal enum ClientProtcolVersion
    {
        // clientProtoZero is the original client protocol from 2009.
 	    // http://nats.io/documentation/internals/nats-protocol/
        ClientProtoZero = 0,

        // ClientProtoInfo signals a client can receive more then the original INFO block.
        // This can be used to update clients on other cluster members, etc.
        ClientProtoInfo
    }

    // TODO - for a pure object model, we can create
    // an abstract subclass containing shared code between conn and 
    // encoded conn rather than using this class as
    // a base class.  This can happen anytime as we are using
    // interfaces.
    public class Connection : IConnection, IDisposable
    {
        Statistics stats = new Statistics();

        // NOTE: We aren't using Mutex here to support enterprises using
        // .NET 4.0.
        readonly internal object mu = new Object(); 

        private Random r = null;

        Options opts = new Options();

        // returns the options used to create this connection.
        public Options Opts
        {
            get { return opts; }
        }

        List<Thread> wg = new List<Thread>(2);

        private Uri             url     = null;
        private ServerPool srvPool = new ServerPool();

        private Dictionary<string, Uri> urls = new Dictionary<string, Uri>();

        // we have a buffered reader for writing, and reading.
        // This is for both performance, and having to work around
        // interlinked read/writes (supported by the underlying network
        // stream, but not the BufferedStream).
        private Stream  bw      = null;
        private Stream  br      = null;
        private MemoryStream    pending = null;

        Object flusherLock     = new Object();
        bool   flusherKicked = false;
        bool   flusherDone     = false;

        private ServerInfo     info = null;
        private Int64          ssid = 0;

        private ConcurrentDictionary<Int64, Subscription> subs = 
            new ConcurrentDictionary<Int64, Subscription>();
        
        private Queue<Channel<bool>> pongs = new Queue<Channel<bool>>();

        internal MsgArg   msgArgs = new MsgArg();

        internal ConnState status = ConnState.CLOSED;

        internal Exception lastEx;

        Timer               ptmr = null;

        int                 pout = 0;

        // Prepare protocol messages for efficiency
        private byte[] PING_P_BYTES = null;
        private int    PING_P_BYTES_LEN;

        private byte[] PONG_P_BYTES = null;
        private int    PONG_P_BYTES_LEN;

        private byte[] PUB_P_BYTES = null;
        private int    PUB_P_BYTES_LEN = 0;

        private byte[] CRLF_BYTES = null;
        private int    CRLF_BYTES_LEN = 0;

        byte[] pubProtoBuf = null;

        static readonly int REQ_CANCEL_IVL = 100;

        TCPConnection conn = new TCPConnection();

        // One could use a task scheduler, but this is simpler and will
        // likely be easier to port to .NET core.
        private class CallbackScheduler
        {
            Channel<Task> tasks            = new Channel<Task>();
            Task executorTask = null;
            Object        runningLock      = new Object();
            bool          schedulerRunning = false;

            private bool Running
            {
                get
                {
                    lock (runningLock)
                    {
                        return schedulerRunning;
                    }
                }

                set
                {
                    lock  (runningLock)
                    {
                        schedulerRunning = value;
                    }
                }
            }

            private void process()
            {
                while (this.Running)
                {
                    Task t = tasks.get(-1);
                    try
                    {
                        t.RunSynchronously();
                    }
                    catch (Exception) { }
                }
            }

            internal void Start()
            {
                lock (runningLock)
                {
                    schedulerRunning = true;
                    executorTask = new Task(() => { process(); });
                    executorTask.Start();
                }
            }

            internal void Add(Task t)
            {
                lock (runningLock)
                {
                    if (schedulerRunning)
                        tasks.add(t);
                }
            }

            internal void ScheduleStop()
            {
                Add(new Task(() =>
                {
                    this.Running = false;
                    tasks.close();
                }));
            }

            internal void WaitForCompletion()
            {
                try
                {
                    executorTask.Wait(5000);
                }
                catch (Exception) { }
            }
        }

        CallbackScheduler callbackScheduler = new CallbackScheduler();

        internal class Control
        {
            // for efficiency, assign these once in the contructor;
            internal string op;
            internal string args;

            static readonly internal char[] separator = { ' ' };

            // ensure this object is always created with a string.
            private Control() { }

            internal Control(string s)
            {
                string[] parts = s.Split(separator, 2);

                if (parts.Length == 1)
                {
                    op = parts[0].Trim();
                    args = IC._EMPTY_;
                }
                if (parts.Length == 2)
                {
                    op = parts[0].Trim();
                    args = parts[1].Trim();
                }
                else
                {
                    op = IC._EMPTY_;
                    args = IC._EMPTY_;
                }
            }
        }

        /// <summary>
        /// Convenience class representing the TCP connection to prevent 
        /// managing two variables throughout the NATs client code.
        /// </summary>
        private sealed class TCPConnection
        {
            /// A note on the use of streams.  .NET provides a BufferedStream
            /// that can sit on top of an IO stream, in this case the network
            /// stream. It increases performance by providing an additional
            /// buffer.
            /// 
            /// So, here's what we have for writing:
            ///     Client code
            ///          ->BufferedStream (bw)
            ///              ->NetworkStream/SslStream (srvStream)
            ///                  ->TCPClient (srvClient);
            ///                  
            ///  For reading:
            ///     Client code
            ///          ->NetworkStream/SslStream (srvStream)
            ///              ->TCPClient (srvClient);
            /// 
            Object        mu        = new Object();
            TcpClient     client    = null;
            NetworkStream stream    = null;
            SslStream     sslStream = null;

            string        hostName  = null;

            internal void open(Srv s, int timeoutMillis)
            {
                lock (mu)
                {
                    // If a connection was lost during a reconnect we 
                    // we could have a defunct SSL stream remaining and 
                    // need to clean up.
                    if (sslStream != null)
                    {
                        try
                        {
                            sslStream.Dispose();
                        }
                        catch (Exception) { }
                        sslStream = null;
                    }

#if NET45
                    client = new TcpClient(s.url.Host, s.url.Port);
#else
                    client = new TcpClient();
                    if (!client.ConnectAsync(s.url.Host, s.url.Port).Wait(TimeSpan.FromMilliseconds(timeoutMillis)))
                    {
                        throw new NATSConnectionException("timeout");
                    }
#endif

                    client.NoDelay = false;

                    client.ReceiveBufferSize = Defaults.defaultBufSize*2;
                    client.SendBufferSize    = Defaults.defaultBufSize;

                    stream = client.GetStream();

                    // save off the hostname
                    hostName = s.url.Host;
                }
            }

            private static bool remoteCertificateValidation(
                  object sender,
                  X509Certificate certificate,
                  X509Chain chain,
                  SslPolicyErrors sslPolicyErrors)
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                    return true;

                return false;
            }

            internal void closeClient(TcpClient c)
            {
                if (c != null)
                {
#if NET45
                    c.Close();
#else
                    c.Dispose();
#endif
                }
            }

            internal void makeTLS(Options options)
            {
                RemoteCertificateValidationCallback cb = null;

                if (stream == null)
                    throw new NATSException("Internal error:  Cannot create SslStream from null stream.");

                cb = options.TLSRemoteCertificationValidationCallback;
                if (cb == null)
                    cb = remoteCertificateValidation;

                sslStream = new SslStream(stream, false, cb, null,
                    EncryptionPolicy.RequireEncryption);

                try
                {
                    SslProtocols protocol = (SslProtocols)Enum.Parse(typeof(SslProtocols), "Tls12");
                    sslStream.AuthenticateAsClientAsync(hostName, options.certificates, protocol, true).Wait();
                }
                catch (Exception ex)
                {
                    closeClient(client);
                    throw new NATSConnectionException("TLS Authentication error", ex);
                }
            }

            internal int ConnectTimeout
            {
                set
                {
                    ConnectTimeout = value;
                }
            }

            internal int SendTimeout
            {
                set
                {
                    if (client != null)
                        client.SendTimeout = value;
                }
            }

            internal bool isSetup()
            {
                return (client != null);
            }

            internal void teardown()
            {
                TcpClient c;
                Stream s;

                lock (mu)
                {
                    c = client;
                    s = getReadBufferedStream();

                    client = null;
                    stream = null;
                    sslStream = null;
                }

                try
                {
                    if (s != null)
                        s.Dispose();

                    if (c != null)
                        closeClient(c);
                }
                catch (Exception) { }
            }

            internal Stream getReadBufferedStream()
            {
                if (sslStream != null)
                    return sslStream;

                return stream;
            }

            internal Stream getWriteBufferedStream(int size)
            {
                BufferedStream bs = null;

                if (sslStream != null)
                    bs = new BufferedStream(sslStream, size);
                else
                    bs = new BufferedStream(stream, size);

                return bs;
            }

            internal bool Connected
            {
                get
                {
                    if (client == null)
                        return false;

                    return client.Connected;
                }
            }

            internal bool DataAvailable
            {
                get
                {
                    if (stream == null)
                        return false;

                    return stream.DataAvailable;
                }
            }
        }

        // Ensure we cannot instanciate a connection this way.
        private Connection() { }

        internal Connection(Options opts)
        {
            this.opts = new Options(opts);
            this.pongs = createPongs();

            PING_P_BYTES = System.Text.Encoding.UTF8.GetBytes(IC.pingProto);
            PING_P_BYTES_LEN = PING_P_BYTES.Length;

            PONG_P_BYTES = System.Text.Encoding.UTF8.GetBytes(IC.pongProto);
            PONG_P_BYTES_LEN = PONG_P_BYTES.Length;

            PUB_P_BYTES = Encoding.UTF8.GetBytes(IC._PUB_P_);
            PUB_P_BYTES_LEN = PUB_P_BYTES.Length;

            CRLF_BYTES = Encoding.UTF8.GetBytes(IC._CRLF_);
            CRLF_BYTES_LEN = CRLF_BYTES.Length;

            // predefine the start of the publish protocol message.
            buildPublishProtocolBuffer(Defaults.scratchSize);

            callbackScheduler.Start();
        }

        private void buildPublishProtocolBuffer(int size)
        {
            pubProtoBuf = new byte[size];
            Buffer.BlockCopy(PUB_P_BYTES, 0, pubProtoBuf, 0, PUB_P_BYTES_LEN);
        }

        // Will assign the correct server to the Conn.Url
        private void pickServer()
        {
            url = null;

            Srv s = srvPool.First();
            if (s == null)
                throw new NATSNoServersException("Unable to choose server; no servers available.");

            url = s.url;
        }


        // Create the server pool using the options given.
        // We will place a Url option first, followed by any
        // Server Options. We will randomize the server pool unlesss
        // the NoRandomize flag is set.
        private void setupServerPool()
        {
            srvPool.Setup(Opts);
            pickServer();
        }

        // createConn will connect to the server and wrap the appropriate
        // bufio structures. It will do the right thing when an existing
        // connection is in place.
        private bool createConn(Srv s)
        {
            try
            {
                conn.open(s, opts.Timeout);

                if (pending != null && bw != null)
                {
                    // flush to the pending buffer;
                    try
                    {
                        // Make a best effort, but this shouldn't stop
                        // conn creation.
                        bw.Flush();
                    }
                    catch (Exception) { }
                }

                bw = conn.getWriteBufferedStream(Defaults.defaultBufSize);
                br = conn.getReadBufferedStream();
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        // makeSecureConn will wrap an existing Conn using TLS
        private void makeTLSConn()
        {
            conn.makeTLS(this.opts);
            bw = conn.getWriteBufferedStream(Defaults.defaultBufSize);
            br = conn.getReadBufferedStream();
        }

        // waitForExits will wait for all socket watcher Go routines to
        // be shutdown before proceeding.
        private void waitForExits()
        {
            // Kick old flusher forcefully.
            setFlusherDone(true);
            kickFlusher();

            if (wg.Count > 0)
            {
                try
                {
                    foreach (Thread t in wg)
                    {
                        t.Join();
                    }
                }
                catch (Exception) { }
            }
        }

        private void pingTimerCallback(object state)
        {
            lock (mu)
            {
                if (status != ConnState.CONNECTED)
                {
                    return;
                }

                pout++;

                if (pout > Opts.MaxPingsOut)
                {
                    processOpError(new NATSStaleConnectionException());
                    return;
                }

                try
                {
                    sendPing(null);
                }
                catch (Exception) { }
            }
        }

        // caller must lock
        private void stopPingTimer()
        {
            if (ptmr != null)
            {
                ptmr.Dispose();
                ptmr = null;
            }
        }

        // caller must lock
        private void startPingTimer()
        {
            pout = 0;

            stopPingTimer();

            if (Opts.PingInterval > 0)
            {
                ptmr = new Timer(pingTimerCallback, null,
                    opts.PingInterval,
                    opts.PingInterval);
            }
        }

        private string generateThreadName(string prefix)
        {
            string name = "unknown";
            if (opts.Name != null)
            {
                name = opts.Name;
            }

            return prefix + "-" + name;
        }

        private void spinUpSocketWatchers()
        {
            Thread t = null;

            waitForExits();

            // Ensure threads are started before we continue with
            // ManualResetEvents.
            ManualResetEvent readLoopStartEvent = new ManualResetEvent(false);
            t = new Thread(() => {
                readLoopStartEvent.Set();
                readLoop();
            });
            t.Start();
            t.Name = generateThreadName("Reader");
            wg.Add(t);

            ManualResetEvent flusherStartEvent = new ManualResetEvent(false);
            t = new Thread(() => {
                flusherStartEvent.Set();
                flusher();
            });
            t.Start();
            t.Name = generateThreadName("Flusher");
            wg.Add(t);

            // wait for both threads to start before continuing.
            flusherStartEvent.WaitOne(60000);
            readLoopStartEvent.WaitOne(60000);
        }

        public string ConnectedUrl
        {
            get
            {
                lock (mu)
                {
                    if (status != ConnState.CONNECTED)
                        return null;

                    return url.OriginalString;
                }
            }
        }

        public string ConnectedId
        {
            get
            {
                lock (mu)
                {
                    if (status != ConnState.CONNECTED)
                        return IC._EMPTY_;

                    return this.info.serverId;
                }
            }
        }

        public string[] Servers
        {
            get
            {
                lock (mu)
                {
                    return srvPool.GetServerList();
                }
            }
        }

        private Queue<Channel<bool>> createPongs()
        {
            Queue<Channel<bool>> rv = new Queue<Channel<bool>>();
            return rv;
        }

        // Process a connected connection and initialize properly.
        // Caller must lock.
        private void processConnectInit()
        {
            this.status = ConnState.CONNECTING;

            processExpectedInfo();
            sendConnect();

            // .NET vs go design difference here:
            // Starting the ping timer earlier allows us
            // to assign, and thus, dispose of it if the connection 
            // is disposed before the socket watchers are running.
            // Otherwise, the connection is referenced by an orphaned 
            // ping timer which can create a memory leak.
            startPingTimer();

            new Task(() => { spinUpSocketWatchers(); }).Start();
        }

        internal bool connect(Srv s, out Exception exToThrow)
        {
            url = s.url;
            try
            {
                exToThrow = null;
                lock (mu)
                {
                    if (createConn(s))
                    {
                        processConnectInit();
                        exToThrow = null;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                exToThrow = e;
                close(ConnState.DISCONNECTED, false);
                lock (mu)
                {
                    url = null;
                }
            }

            return false;
        }

        internal void connect()
        {
            Exception exToThrow = null;

            setupServerPool();

            srvPool.ConnectToAServer((s) => {
                return connect(s, out exToThrow);
            });

            lock (mu)
            {
                if (status != ConnState.CONNECTED)
                {
                    if (exToThrow == null)
                        exToThrow = new NATSNoServersException("Unable to connect to a server.");

                    throw exToThrow;
                }
            }
        }

        // This will check to see if the connection should be
        // secure. This can be dictated from either end and should
        // only be called after the INIT protocol has been received.
        private void checkForSecure()
        {
            // Check to see if we need to engage TLS
            // Check for mismatch in setups
            if (Opts.Secure && !info.tlsRequired)
            {
                throw new NATSSecureConnWantedException();
            }
            else if (info.tlsRequired && !Opts.Secure)
            {
                throw new NATSSecureConnRequiredException();
            }

            // Need to rewrap with bufio
            if (Opts.Secure)
            {
                makeTLSConn();
            }
        }

        // processExpectedInfo will look for the expected first INFO message
        // sent when a connection is established. The lock should be held entering.
        private void processExpectedInfo()
        {
            Control c;

            try
            {
                conn.SendTimeout = 2;
                c = readOp();
            }
            catch (Exception e)
            {
                processOpError(e);
                throw;
            }
            finally
            {
                conn.SendTimeout = 0;
            }

            if (!IC._INFO_OP_.Equals(c.op))
            {
                throw new NATSConnectionException("Protocol exception, INFO not received");
            }

            processInfo(c.args);
            checkForSecure();
        }

        private void writeString(string format, object a, object b)
        {
            writeString(String.Format(format, a, b));
        }

        private void writeString(string format, object a, object b, object c)
        {
            writeString(String.Format(format, a, b, c));
        }

        private void writeString(string value)
        {
            byte[] sendBytes = System.Text.Encoding.UTF8.GetBytes(value);
            bw.Write(sendBytes, 0, sendBytes.Length);
        }

        private void sendProto(byte[] value, int length)
        {
            lock (mu)
            {
                bw.Write(value, 0, length);
            }
        }

        // Generate a connect protocol message, issuing user/password if
        // applicable. The lock is assumed to be held upon entering.
        private string connectProto()
        {
            string u = url.UserInfo;
            string user = null;
            string pass = null;
            string token = null;

            if (!string.IsNullOrEmpty(u))
            {
                if (u.Contains(":"))
                {
                    string[] userpass = u.Split(':');
                    if (userpass.Length > 0)
                    {
                        user = userpass[0];
                    }
                    if (userpass.Length > 1)
                    {
                        pass = userpass[1];
                    }
                }
                else
                {
                    token = u;
                }
            }
            else
            {
                user = opts.user;
                pass = opts.password;
                token = opts.token;
            }

            ConnectInfo info = new ConnectInfo(opts.Verbose, opts.Pedantic, user,
                pass, token, opts.Secure, opts.Name);

            StringBuilder sb = new StringBuilder();

            sb.AppendFormat(IC.conProto, info.ToJson());
            return sb.ToString();
        }


        // caller must lock.
        private void sendConnect()
        {
            try
            {
                writeString(connectProto());
                bw.Write(PING_P_BYTES, 0, PING_P_BYTES_LEN);
                bw.Flush();
            }
            catch (Exception ex)
            {
                if (lastEx == null)
                    throw new NATSException("Error sending connect protocol message", ex);
            }

            string result = null;
            try
            {
                StreamReader sr = new StreamReader(br);
                result = sr.ReadLine();


                // If opts.verbose is set, handle +OK.
                if (opts.Verbose == true && IC.okProtoNoCRLF.Equals(result))
                {
                    result = sr.ReadLine();
                }

                // Do not close or dispose the stream reader; 
                // we need the underlying BufferedStream.
            }
            catch (Exception ex)
            {
                throw new NATSConnectionException("Connect read error", ex);
            }

            if (IC.pongProtoNoCRLF.Equals(result))
            {
                status = ConnState.CONNECTED;
                return;
            }
            else
            {
                if (result == null)
                {
                    throw new NATSConnectionException("Connect read protocol error");
                }
                else if (result.StartsWith(IC._ERR_OP_))
                {
                    throw new NATSConnectionException(
                        result.Substring(IC._ERR_OP_.Length));
                }
                else
                {
                    throw new NATSException("Error from sendConnect(): " + result);
                }
            }
        }

        private Control readOp()
        {
            // This is only used when creating a connection, so simplify
            // life and just create a stream reader to read the incoming
            // info string.  If this becomes part of the fastpath, read
            // the string directly using the buffered reader.
            //
            // Do not close or dispose the stream reader - we need the underlying
            // BufferedStream.
            StreamReader sr = new StreamReader(br);
            return new Control(sr.ReadLine());
        }

        private void processDisconnect()
        {
            status = ConnState.DISCONNECTED;
            if (lastEx == null)
                return;
        }

        // This will process a disconnect when reconnect is allowed.
        // The lock should not be held on entering this function.
        private void processReconnect()
        {
            lock (mu)
            {
                // If we are already in the proper state, just return.
                if (isReconnecting())
                    return;

                status = ConnState.RECONNECTING;

                stopPingTimer();

                if (conn.isSetup())
                {
                    conn.teardown();
                }

                Thread t = new Thread(() =>
                {
                    doReconnect();
                });
                t.Name = generateThreadName("Reconnect");
                t.Start();
            }
        }

        // flushReconnectPending will push the pending items that were
        // gathered while we were in a RECONNECTING state to the socket.
        private void flushReconnectPendingItems()
        {
            if (pending == null)
                return;

            if (pending.Length > 0)
            {
#if NET45
                bw.Write(pending.GetBuffer(), 0, (int)pending.Length);
                bw.Flush();
#else
                ArraySegment<byte> buffer;
                if (pending.TryGetBuffer(out buffer))
                {
                    // TODO:  Buffer.length
                    bw.Write(buffer.Array, buffer.Offset, (int)buffer.Count);
                }
#endif
            }

            pending = null;
        }


        // Sleep guarantees no exceptions will be thrown out of it.
        private static void sleep(int millis)
        {
            try
            {
                Thread.Sleep(millis);
            }
            catch (Exception) { }
        }

        // Schedules a connection event (connected/disconnected/reconnected)
        // if non-null.
        // Caller must lock.
        private void scheduleConnEvent(EventHandler<ConnEventArgs> connEvent)
        {
            if (connEvent == null)
                return;

            // Schedule a reference to the event handler.
            EventHandler<ConnEventArgs> eh = connEvent;
            callbackScheduler.Add(
                new Task(() => { eh(this, new ConnEventArgs(this)); })
            );

        }

        // Try to reconnect using the option parameters.
        // This function assumes we are allowed to reconnect.
        //
        // NOTE: locks are manually acquired/released to parallel the NATS
        // go client
        private void doReconnect()
        {
            // We want to make sure we have the other watchers shutdown properly
            // here before we proceed past this point
            waitForExits();

            // FIXME(dlc) - We have an issue here if we have
            // outstanding flush points (pongs) and they were not
            // sent out, but are still in the pipe.

            // Hold manually release where needed below.
            Monitor.Enter(mu);

            // clear any queued pongs, e..g. pending flush calls.
            clearPendingFlushCalls();

            pending = new MemoryStream();
            bw = new BufferedStream(pending);

            // Clear any errors.
            lastEx = null;

            scheduleConnEvent(Opts.DisconnectedEventHandler);

            // TODO:  Look at using a predicate delegate in the server pool to
            // pass a method to, but locking is complex and would need to be
            // reworked.
            Srv cur;
            while ((cur = srvPool.SelectNextServer(Opts.MaxReconnect)) != null)
            {
                url = cur.url;

                lastEx = null;

                // Sleep appropriate amount of time before the
                // connection attempt if connecting to same server
                // we just got disconnected from.
                double elapsedMillis = cur.TimeSinceLastAttempt.TotalMilliseconds;
                double sleepTime = 0;

                if (elapsedMillis < Opts.ReconnectWait)
                {
                    sleepTime = Opts.ReconnectWait - elapsedMillis;
                }

                if (sleepTime <= 0)
                {
                    // Release to allow parallel processes to close,
                    // unsub, etc.  Note:  Use the sleep API - yield is
                    // heavy handed here.
                    sleepTime = 50;
                }

                Monitor.Exit(mu);
                sleep((int)sleepTime);
                Monitor.Enter(mu);

                if (isClosed())
                    break;

                cur.reconnects++;

                try
                {
                    // try to create a new connection
                    createConn(cur);
                }
                catch (Exception)
                {
                    // not yet connected, retry and hold
                    // the lock.
                    lastEx = null;
                    continue;
                }

                // process our connect logic
                try
                {
                    processConnectInit();
                }
                catch (Exception e)
                {
                    lastEx = e;
                    status = ConnState.RECONNECTING;
                    continue;
                }

                try
                {
                    // Send existing subscription state
                    resendSubscriptions();

                    // Now send off and clear pending buffer
                    flushReconnectPendingItems();
                }
                catch (Exception)
                {
                    status = ConnState.RECONNECTING;
                    continue;
                }

                // We are reconnected.
                stats.reconnects++;
                cur.didConnect = true;
                cur.reconnects = 0;
                srvPool.CurrentServer = cur;
                status = ConnState.CONNECTED;

                scheduleConnEvent(Opts.ReconnectedEventHandler);

                // Release lock here, we will return below
                Monitor.Exit(mu);

                // Make sure to flush everything
                // We have a corner case where the server we just
                // connected to has failed as well - let the reader
                // thread detect this and spawn another reconnect 
                // thread to simplify locking.
                try
                {
                    Flush();
                }
                catch (Exception) { }

                return;
            }

            // Call into close.. we have no more servers left..
            if (lastEx == null)
                lastEx = new NATSNoServersException("Unable to reconnect");

            Monitor.Exit(mu);
            Close();
        }

        private bool isConnecting()
        {
            return (status == ConnState.CONNECTING);
        }

        private void processOpError(Exception e)
        {
            bool disconnected = false;

            lock (mu)
            {
                if (isConnecting() || isClosed() || isReconnecting())
                {
                    return;
                }

                if (Opts.AllowReconnect && status == ConnState.CONNECTED)
                {
                    processReconnect();
                }
                else
                {
                    processDisconnect();
                    disconnected = true;
                    lastEx = e;
                }
            }

            if (disconnected)
            {
                Close();
            }
        }

        private void readLoop()
        {
            // Stack based buffer.
            byte[] buffer = new byte[Defaults.defaultReadLength];
            Parser parser = new Parser(this);
            int    len;

            while (true)
            {
                try
                {
                    len = br.Read(buffer, 0, Defaults.defaultReadLength);
                    parser.parse(buffer, len);
                }
                catch (Exception e)
                {
                    if (State != ConnState.CLOSED)
                    {
                        processOpError(e);
                    }
                    break;
                }
            }
        }

        // deliverMsgs waits on the delivery channel shared with readLoop and processMsg.
        // It is used to deliver messages to asynchronous subscribers.
        internal void deliverMsgs(Channel<Msg> ch)
        {
            Msg m;

            while (true)
            {
                lock (mu)
                {
                    if (isClosed())
                        return;
                }
 
                m = ch.get(-1);
                if (m == null)
                {
                    // the channel has been closed, exit silently.
                    return;
                }

                // Note, this seems odd message having the sub process itself, 
                // but this is good for performance.
                if (!m.sub.processMsg(m))
                {
                    lock (mu)
                    {
                        removeSub(m.sub);
                    }
                }
            }
        }

        // Roll our own fast conversion - we know it's the right
        // encoding. 
        char[] convertToStrBuf = new char[Defaults.scratchSize];

        // Caller must ensure thread safety.
        private string convertToString(byte[] buffer, long length)
        {
            // expand if necessary
            if (length > convertToStrBuf.Length)
            {
                convertToStrBuf = new char[length];
            }

            for (int i = 0; i < length; i++)
            {
                convertToStrBuf[i] = (char)buffer[i];
            }

            // This is the copy operation for msg arg strings.
            return new String(convertToStrBuf, 0, (int)length);
        }

        // Since we know we don't need to decode the protocol string,
        // just copy the chars into bytes.  This increased
        // publish peformance by 30% as compared to Encoding.
        private int writeStringToBuffer(byte[] buffer, int offset, string value)
        {
            int length = value.Length;
            int end = offset + length;

            for (int i = 0; i < length; i++)
            {
                buffer[i+offset] = (byte)value[i];
            }

            return end;
        }

        // Here we go ahead and convert the message args into
        // strings, numbers, etc.  The msgArg object is a temporary
        // place to hold them, until we create the message.
        //
        // These strings, once created, are never copied.
        //
        internal void processMsgArgs(byte[] buffer, long length)
        {
            string s = convertToString(buffer, length);
            string[] args = s.Split(' ');

            switch (args.Length)
            {
                case 3:
                    msgArgs.subject = args[0];
                    msgArgs.sid     = Convert.ToInt64(args[1]);
                    msgArgs.reply   = null;
                    msgArgs.size    = Convert.ToInt32(args[2]);
                    break;
                case 4:
                    msgArgs.subject = args[0];
                    msgArgs.sid     = Convert.ToInt64(args[1]);
                    msgArgs.reply   = args[2];
                    msgArgs.size    = Convert.ToInt32(args[3]);
                    break;
                default:
                    throw new NATSException("Unable to parse message arguments: " + s);
            }

            if (msgArgs.size < 0)
            {
                throw new NATSException("Invalid Message - Bad or Missing Size: " + s);
            }
            if (msgArgs.sid < 0)
            {
                throw new NATSException("Invalid Message - Bad or Missing Sid: " + s);
            }
        }


        // processMsg is called by parse and will place the msg on the
        // appropriate channel for processing. All subscribers have their
        // their own channel. If the channel is full, the connection is
        // considered a slow subscriber.
        internal void processMsg(byte[] msg, long length)
        {
            bool maxReached = false;
            Subscription s;

            lock (mu)
            {
                stats.inMsgs++;
                stats.inBytes += length;

                // In regular message processing, the key should be present,
                // so optimize by using an an exception to handle a missing key.
                // (as opposed to checking with Contains or TryGetValue)
                try
                {
                    s = subs[msgArgs.sid];
                }
                catch (Exception)
                {
                    // this can happen when a subscriber is unsubscribing.
                    return;
                }

                lock (s.mu)
                {
                    maxReached = s.tallyMessage(length);
                    if (maxReached == false)
                    {
                        s.addMessage(new Msg(msgArgs, s, msg, length), opts.subChanLen);
                    } // maxreached == false

                } // lock s.mu

            } // lock conn.mu

            if (maxReached)
                removeSub(s);
        }

        // processSlowConsumer will set SlowConsumer state and fire the
        // async error handler if registered.
        internal void processSlowConsumer(Subscription s)
        {
            lastEx = new NATSSlowConsumerException();
            if (opts.AsyncErrorEventHandler != null && !s.sc)
            {
                EventHandler<ErrEventArgs> aseh = opts.AsyncErrorEventHandler;
                callbackScheduler.Add(
                    new Task(() => { aseh(this, new ErrEventArgs(this, s, "Slow Consumer")); })
                );
            }
            s.sc = true;
        }

        private void kickFlusher()
        {
            lock (flusherLock)
            {
                if (!flusherKicked)
                    Monitor.Pulse(flusherLock);

                flusherKicked = true;
            }
        }

        private bool waitForFlusherKick()
        {
            lock (flusherLock)
            {
                if (flusherDone == true)
                    return false;

                // if kicked before we get here meantime, skip
                // waiting.
                if (!flusherKicked)
                {
                    // A spurious wakeup here is OK - we'll just do
                    // an earlier flush.
                    Monitor.Wait(flusherLock);
                }

                flusherKicked = false;
            }

            return true;
        }

        private void setFlusherDone(bool value)
        {
            lock (flusherLock)
            {
                flusherDone = value;

                if (flusherDone)
                    kickFlusher();
            }
        }

        private bool isFlusherDone()
        {
            lock (flusherLock)
            {
                return flusherDone;
            }
        }

        // flusher is a separate task that will process flush requests for the write
        // buffer. This allows coalescing of writes to the underlying socket.
        private void flusher()
        {
            setFlusherDone(false);

            if (conn.Connected == false)
            {
                return;
            }

            while (!isFlusherDone())
            {
                bool val = waitForFlusherKick();
                if (val == false)
                    return;

                // Yield for a millisecond.  This reduces resource contention,
                // increasing throughput by about 50%.
                Thread.Sleep(1);

                lock (mu)
                {
                    if (!isConnected())
                        return;

                    if (bw.CanWrite)
                    {
                        try
                        {
                            bw.Flush();
                        }
                        catch (Exception) {  /* ignore */ }
                    }
                }
            }
        }

        // processPing will send an immediate pong protocol response to the
        // server. The server uses this mechanism to detect dead clients.
        internal void processPing()
        {
            sendProto(PONG_P_BYTES, PONG_P_BYTES_LEN);
        }


        // processPong is used to process responses to the client's ping
        // messages. We use pings for the flush mechanism as well.
        internal void processPong()
        {
            Channel<bool> ch = null;
            lock (mu)
            {
                if (pongs.Count > 0)
                    ch = pongs.Dequeue();

                pout = 0;

                if (ch != null)
                {
                    ch.add(true);
                }
            }
        }

        // processOK is a placeholder for processing OK messages.
        internal void processOK()
        {
            // NOOP;
            return;
        }

        // processInfo is used to parse the info messages sent
        // from the server.
        internal void processInfo(string json)
        {
            if (json == null || IC._EMPTY_.Equals(json))
            {
                return;
            }

            info = ServerInfo.CreateFromJson(json);
            if (srvPool.Add(info.connectURLs))
            {
                if (opts.NoRandomize == false)
                    srvPool.Shuffle();
            }
        }

        internal void processAsyncInfo(byte[] jsonBytes, int length)
        {
            lock (mu)
            {
                processInfo(Encoding.UTF8.GetString(jsonBytes, 0, length));
            }
        }

        // LastError reports the last error encountered via the Connection.
        public Exception LastError
        {
            get
            {
                return this.lastEx;
            }
        }

        // processErr processes any error messages from the server and
        // sets the connection's lastError.
        internal void processErr(MemoryStream errorStream)
        {
            bool invokeDelegates = false;
            Exception ex = null;

            string s = getNormalizedError(errorStream);

            if (IC.STALE_CONNECTION.Equals(s))
            {
                processOpError(new NATSStaleConnectionException());
            }
            else if (IC.AUTH_TIMEOUT.Equals(s))
            {
                // Protect against a timing issue where an authoriztion error
                // is handled before the connection close from the server.
                // This can happen in reconnect scenarios.
                processOpError(new NATSConnectionException(IC.AUTH_TIMEOUT));
            }
            else
            {
                ex = new NATSException("Error from processErr(): " + s);
                lock (mu)
                {
                    lastEx = ex;

                    if (status != ConnState.CONNECTING)
                    {
                        invokeDelegates = true;
                    }
                }

                close(ConnState.CLOSED, invokeDelegates);
            }
        }

        // getNormalizedError extracts a string from a MemoryStream, then normalizes it
        // by removing leading and trailing spaces and quotes, and converting to lowercase
        private string getNormalizedError(MemoryStream errorStream)
        {
            string s = Encoding.UTF8.GetString(errorStream.ToArray(), 0, (int)errorStream.Position);
            return s.Trim('\'', '"', ' ', '\t', '\r', '\n').ToLower();
        }

        // Use low level primitives to build the protocol for the publish
        // message.
        private int writePublishProto(byte[] dst, string subject, string reply, int msgSize)
        {
            // skip past the predefined "PUB "
            int index = PUB_P_BYTES_LEN;

            // Subject
            index = writeStringToBuffer(dst, index, subject);

            if (reply != null)
            {
                // " REPLY"
                dst[index] = (byte)' ';
                index++;

                index = writeStringToBuffer(dst, index, reply);
            }

            // " "
            dst[index] = (byte)' ';
            index++;

            // " SIZE"
            index = writeStringToBuffer(dst, index, msgSize.ToString());

            // "\r\n"
            Buffer.BlockCopy(CRLF_BYTES, 0, dst, index, CRLF_BYTES_LEN);
            index += CRLF_BYTES_LEN;

            return index;
        }

        // publish is the internal function to publish messages to a nats-server.
        // Sends a protocol data message by queueing into the bufio writer
        // and kicking the flush go routine. These writes should be protected.
        internal void publish(string subject, string reply, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                throw new NATSBadSubscriptionException();
            }

            int msgSize = data != null ? data.Length : 0;

            lock (mu)
            {
                // Proactively reject payloads over the threshold set by server.
                if (msgSize > info.maxPayload)
                    throw new NATSMaxPayloadException();

                if (isClosed())
                    throw new NATSConnectionClosedException();

                if (lastEx != null)
                    throw lastEx;

                int pubProtoLen;
                // write our pubProtoBuf buffer to the buffered writer.
                try
                {
                    pubProtoLen = writePublishProto(pubProtoBuf, subject,
                        reply, msgSize);
                }
                catch (IndexOutOfRangeException)
                {
                    // We can get here if we have very large subjects.
                    // Expand with some room to spare.
                    int resizeAmount = Defaults.scratchSize + subject.Length
                        + (reply != null ? reply.Length : 0);

                    buildPublishProtocolBuffer(resizeAmount);

                    pubProtoLen = writePublishProto(pubProtoBuf, subject,
                        reply, msgSize);
                }

                bw.Write(pubProtoBuf, 0, pubProtoLen);

                if (msgSize > 0)
                {
                    bw.Write(data, 0, msgSize);
                }

                bw.Write(CRLF_BYTES, 0, CRLF_BYTES_LEN);

                stats.outMsgs++;
                stats.outBytes += msgSize;

                kickFlusher();
            }

        } // publish

        public void Publish(string subject, byte[] data)
        {
            publish(subject, null, data);
        }

        public void Publish(Msg msg)
        {
            publish(msg.Subject, msg.Reply, msg.Data);
        }

        public void Publish(string subject, string reply, byte[] data)
        {
            publish(subject, reply, data);
        }

        internal virtual Msg request(string subject, byte[] data, int timeout)
        {
            Msg    m     = null;
            string inbox = NewInbox();

            SyncSubscription s = subscribeSync(inbox, null);
            s.AutoUnsubscribe(1);

            publish(subject, inbox, data);
            Flush();
            m = s.NextMessage(timeout);
            s.unsubscribe(false);

            return m;
        }

        public Msg Request(string subject, byte[] data, int timeout)
        {
            // a timeout of 0 will never succeed - do not allow it.
            if (timeout <= 0)
            {
                throw new ArgumentException(
                    "Timeout must be greater that 0.",
                    "timeout");
            }

            return request(subject, data, timeout);
        }

        public Msg Request(string subject, byte[] data)
        {
            return request(subject, data, -1);
        }

        internal virtual Task<Msg> requestAsync(string subject, byte[] data, int timeout)
        {
            // Simple case without a cancellation token.
            return Task.Factory.StartNew<Msg>(() => { return request(subject, data, timeout); });
        }

        internal virtual Task<Msg> requestAsync(string subject, byte[] data, int timeout, CancellationToken ct)
        {
            // Simple case without a cancellation token.
            if (ct == null)
            {
                return Task.Factory.StartNew<Msg>(() => { return request(subject, data, timeout); });
            }

            // More complex case, supporting cancellation.
            return Task.Factory.StartNew<Msg>(() =>
            {
                // check if we are already cancelled.
                ct.ThrowIfCancellationRequested();

                Msg m = null;
                string inbox = NewInbox();

                // Use synchronous subscriber to minimize overhead.
                // An async subscriber would be easier, but creates 
                // yet another task internally.  The cost is it could
                // take up to CANCEL_IVL ms to cancel.
                SyncSubscription s = subscribeSync(inbox, null);
                s.AutoUnsubscribe(1);

                publish(subject, inbox, data);
                Flush();

                int timeRemaining = timeout;

                while (m == null && ct.IsCancellationRequested == false)
                {
                    if (timeout == -1)
                    {
                        // continue in a loop until cancellation
                        // is requested.
                        try
                        {
                            m = s.NextMessage(REQ_CANCEL_IVL);
                        }
                        catch (NATSTimeoutException) { /* ignore */ };
                    }
                    else
                    {
                        int waitTime = timeRemaining < REQ_CANCEL_IVL ?
                            timeRemaining : REQ_CANCEL_IVL;

                        try
                        {
                            m = s.NextMessage(waitTime);
                        }
                        catch (NATSTimeoutException e)
                        {
                            timeRemaining -= waitTime;
                            if (timeRemaining <= 0)
                            {
                                s.unsubscribe(false);
                                throw e;
                            }
                        }
                    }
                }

                // we either have a message or have been cancelled.
                s.unsubscribe(false);

                // Throw if cancelled.  Note, the cancellation occured
                // after we've received a message, go ahead and honor 
                // the cancellation.
                ct.ThrowIfCancellationRequested();

                return m;

            }, ct);
        }

        public Task<Msg> RequestAsync(string subject, byte[] data, int timeout)
        {
            // a timeout of 0 will never succeed - do not allow it.
            if (timeout <= 0)
            {
                throw new ArgumentException(
                    "Timeout must be greater that 0.",
                    "timeout");
            }

            return requestAsync(subject, data, timeout);
        }

        public Task<Msg> RequestAsync(string subject, byte[] data)
        {
            return requestAsync(subject, data, -1);
        }

        public Task<Msg> RequestAsync(string subject, byte[] data, int timeout, CancellationToken token)
        {
            // a timeout of 0 will never succeed - do not allow it.
            if (timeout <= 0)
            {
                throw new ArgumentException(
                    "Timeout must be greater that 0.",
                    "timeout");
            }

            return requestAsync(subject, data, timeout, token);
        }

        public Task<Msg> RequestAsync(string subject, byte[] data, CancellationToken token)
        {
            return requestAsync(subject, data, -1, token);
        }

        public string NewInbox()
        {
            if (r == null)
                r = new Random();

            byte[] buf = new byte[13];

            r.NextBytes(buf);

            return IC.inboxPrefix + BitConverter.ToString(buf).Replace("-","");
        }

        internal void sendSubscriptionMessage(AsyncSubscription s)
        {
            lock (mu)
            {
                // We will send these for all subs when we reconnect
                // so that we can suppress here.
                if (!isReconnecting())
                {
                    writeString(IC.subProto, s.Subject, s.Queue, s.sid);
                    kickFlusher();
                }
            }
        }

        private void addSubscription(Subscription s)
        {
            s.sid = Interlocked.Increment(ref ssid);
            subs[s.sid] = s;
        }

        internal AsyncSubscription subscribeAsync(string subject, string queue,
            EventHandler<MsgHandlerEventArgs> handler)
        {
            AsyncSubscription s = null;

            lock (mu)
            {
                if (isClosed())
                    throw new NATSConnectionClosedException();

                s = new AsyncSubscription(this, subject, queue);

                addSubscription(s);

                if (handler != null)
                {
                    s.MessageHandler += handler;
                    s.Start();
                }
            }

            return s;
        }

        // subscribe is the internal subscribe 
        // function that indicates interest in a subject.
        private SyncSubscription subscribeSync(string subject, string queue)
        {
            SyncSubscription s = null;

            lock (mu)
            {
                if (isClosed())
                    throw new NATSConnectionClosedException();

                s = new SyncSubscription(this, subject, queue);

                addSubscription(s);

                // We will send these for all subs when we reconnect
                // so that we can suppress here.
                if (!isReconnecting())
                {
                    writeString(IC.subProto, subject, queue, s.sid);
                }
            }

            kickFlusher();

            return s;
        }

        public ISyncSubscription SubscribeSync(string subject)
        {
            return subscribeSync(subject, null);
        }

        public IAsyncSubscription SubscribeAsync(string subject)
        {
            return subscribeAsync(subject, null, null);
        }

        public IAsyncSubscription SubscribeAsync(string subject, EventHandler<MsgHandlerEventArgs> handler)
        {
            return subscribeAsync(subject, null, handler);
        }

        public ISyncSubscription SubscribeSync(string subject, string queue)
        {
            return subscribeSync(subject, queue);
        }

        public IAsyncSubscription SubscribeAsync(string subject, string queue)
        {
            return subscribeAsync(subject, queue, null);
        }

        public IAsyncSubscription SubscribeAsync(string subject, string queue, EventHandler<MsgHandlerEventArgs> handler)
        {
            return subscribeAsync(subject, queue, handler);
        }
        // unsubscribe performs the low level unsubscribe to the server.
        // Use Subscription.Unsubscribe()
        internal void unsubscribe(Subscription sub, int max)
        {
            lock (mu)
            {
                if (isClosed())
                    throw new NATSConnectionClosedException();

                Subscription s = subs[sub.sid];
                if (s == null)
                {
                    // already unsubscribed
                    return;
                }

                if (max > 0)
                {
                    s.max = max;
                }
                else
                {
                    removeSub(s);
                }

                // We will send all subscriptions when reconnecting
                // so that we can supress here.
                if (!isReconnecting())
                    writeString(IC.unsubProto, s.sid, max);

            }

            kickFlusher();
        }

        internal virtual void removeSub(Subscription s)
        {
            Subscription o;

            subs.TryRemove(s.sid, out o);
            if (s.mch != null)
            {
                s.mch.close();
                s.mch = null;
            }

            s.conn = null;
            s.closed = true;
        }

        // FIXME: This is a hack
        // removeFlushEntry is needed when we need to discard queued up responses
        // for our pings as part of a flush call. This happens when we have a flush
        // call outstanding and we call close.
        private bool removeFlushEntry(Channel<bool> chan)
        {
            if (pongs == null)
                return false;

            if (pongs.Count == 0)
                return false;
            
            Channel<bool> start = pongs.Dequeue();
            Channel<bool> c = start;

            while (true)
            {
                if (c == chan)
                {
                    return true;
                }
                else
                {
                    pongs.Enqueue(c);
                }

                c = pongs.Dequeue();

                if (c == start)
                    break;
            }

            return false;
        }

        // The caller must lock this method.
        private void sendPing(Channel<bool> ch)
        {
            if (ch != null)
                pongs.Enqueue(ch);

            bw.Write(PING_P_BYTES, 0, PING_P_BYTES_LEN);
            bw.Flush();
        }

        public void Flush(int timeout)
        {
            Exception ex = null;

            if (timeout <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "Timeout must be greater than 0",
                    "timeout");
            }

            Channel<bool> ch = new Channel<bool>(1);
            lock (mu)
            {
                if (isClosed())
                    throw new NATSConnectionClosedException();

                sendPing(ch);
            }

            try
            {
                bool rv = ch.get(timeout);
                if (!rv)
                {
                    ex = new NATSConnectionClosedException();
                }
            }
            catch (NATSTimeoutException te)
            {
                ex = te;
            }
            catch (Exception e)
            {
                ex = new NATSException("Flush channel error.", e);
            }

            if (ex != null)
            {
                if (lastEx != null && !(lastEx is NATSSlowConsumerException))
                    lastEx = ex;

                removeFlushEntry(ch);
                throw ex;
            }
        }

        /// <summary>
        /// Flush will perform a round trip to the server and return when it
        /// receives the internal reply.
        /// </summary>
        public void Flush()
        {
            // 60 second default.
            Flush(60000);
        }

        // resendSubscriptions will send our subscription state back to the
        // server. Used in reconnects
        private void resendSubscriptions()
        {
            foreach (Subscription s in subs.Values)
            {
                if (s is IAsyncSubscription)
                    ((AsyncSubscription)s).enableAsyncProcessing();
                

                writeString(IC.subProto, s.Subject, s.Queue, s.sid);
            }

            bw.Flush();
        }

        // This will clear any pending flush calls and release pending calls.
        // Lock must be held by the caller.
        private void clearPendingFlushCalls()
        {

            // Clear any queued pongs, e.g. pending flush calls.
            foreach (Channel<bool> ch in pongs)
            {
                if (ch != null)
                    ch.add(true);
            }
            pongs.Clear();
        }


        // Low level close call that will do correct cleanup and set
        // desired status. Also controls whether user defined callbacks
        // will be triggered. The lock should not be held entering this
        // function. This function will handle the locking manually.
        private void close(ConnState closeState, bool invokeDelegates)
        {
            lock (mu)
            {
                if (isClosed())
                {
                    status = closeState;
                    return;
                }

                status = ConnState.CLOSED;

                // Kick the routines so they fall out.
                // fch will be closed on finalizer
                kickFlusher();
            }

            lock (mu)
            {
                // Clear any queued pongs, e.g. pending flush calls.
                clearPendingFlushCalls();

                stopPingTimer();

                // Close sync subscriber channels and release any
                // pending NextMsg() calls.
                foreach (Subscription s in subs.Values)
                {
                    s.close();
                }

                subs.Clear();

                // perform appropriate callback is needed for a
                // disconnect;
                if (invokeDelegates && conn.isSetup())
                {
                    scheduleConnEvent(Opts.DisconnectedEventHandler);
                }

                // Go ahead and make sure we have flushed the outbound buffer.
                status = ConnState.CLOSED;
                if (conn.isSetup())
                {
                    if (bw != null)
                    {
                        try
                        {
                            bw.Flush();
                        }
                        catch (Exception) { /* ignore */ }
                    }

                    conn.teardown();
                }

                if (invokeDelegates)
                {
                    scheduleConnEvent(opts.ClosedEventHandler);
                }

                status = closeState;
            }
        }

        public void Close()
        {
            close(ConnState.CLOSED, true);
            callbackScheduler.ScheduleStop();
        }

        // assume the lock is head.
        private bool isClosed()
        {
            return (status == ConnState.CLOSED);
        }

        public bool IsClosed()
        {
            lock (mu)
            {
                return isClosed();
            }
        }

        public bool IsReconnecting()
        {
            lock (mu)
            {
                return isReconnecting();
            }
        }

        public ConnState State
        {
            get
            {
                lock (mu)
                {
                    return status;
                }
            }
        }

        private bool isReconnecting()
        {
            lock (mu)
            {
                return (status == ConnState.RECONNECTING);
            }
        }

        // Test if Conn is connected or connecting.
        private bool isConnected()
        {
            return (status == ConnState.CONNECTING || status == ConnState.CONNECTED);
        }

        public IStatistics Stats
        {
            get
            {
                lock (mu)
                {
                    return new Statistics(this.stats);
                }
            }
        }

        public void ResetStats()
        {
            lock (mu)
            {
                this.stats.clear();
            }
        }

        public long MaxPayload
        {
            get
            {
                lock (mu)
                {
                    return info.maxPayload;
                }
            }
        }

        /// <summary>
        /// Returns a string representation of the
        /// value of this Connection instance.
        /// </summary>
        /// <returns>String value of this instance.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.AppendFormat("url={0};", url);
            sb.AppendFormat("info={0};", info);
            sb.AppendFormat("status={0};", status);
            sb.Append("Subscriptions={");
            foreach (Subscription s in subs.Values)
            {
                sb.Append("Subscription {" + s.ToString() + "};");
            }
            sb.Append("};");

            string[] servers = Servers;
            if (servers != null)
            {
                bool printedFirst = false;
                sb.Append("Servers {");
                foreach (string s in servers)
                {
                    if (printedFirst)
                        sb.Append(",");

                    sb.AppendFormat("[{0}]", s);
                    printedFirst = true;
                }
            }
            sb.Append("}");
            return sb.ToString();
        }

        void IDisposable.Dispose()
        {
            try
            {
                Close();
                callbackScheduler.WaitForCompletion();
            }
            catch (Exception)
            {
                // No need to throw an exception here
            }
        }
    } // class Conn


}
