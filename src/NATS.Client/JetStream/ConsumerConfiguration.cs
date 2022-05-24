// Copyright 2021 The NATS Authors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at:
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using NATS.Client.Internals;
using NATS.Client.Internals.SimpleJSON;
using static NATS.Client.Internals.JsonUtils;
using static NATS.Client.Internals.Validator;

namespace NATS.Client.JetStream
{
    public sealed class ConsumerConfiguration : JsonSerializable
    {
        [Obsolete("This property is obsolete, it is not used.", false)]
        public static readonly Duration MinAckWait = Duration.One;
        [Obsolete("This property is obsolete, it is not used as a default", false)]
        public static readonly Duration MinDefaultIdleHeartbeat = Duration.OfMillis(100);

        public static readonly Duration DurationUnset = Duration.Zero;
        public static readonly Duration MinIdleHeartbeat = Duration.OfMillis(100);

        public const int IntUnset = -1;
        public const long LongUnset = -1;
        public const ulong UlongUnset = 0;
        public const long DurationUnsetLong = 0;
        public const long DurationMinLong = 1;
        public const int StandardMin = 0;
        public const int MaxDeliverMin = 1;
        public static readonly long MinIdleHeartbeatNanos = MinIdleHeartbeat.Nanos;
        public static readonly long MinIdleHeartbeatMillis = MinIdleHeartbeat.Millis;

        private DeliverPolicy? _deliverPolicy;
        private AckPolicy? _ackPolicy;
        private ReplayPolicy? _replayPolicy;
        private ulong? _startSeq;
        private ulong? _rateLimitBps;
        private int? _maxDeliver;
        private int? _maxAckPending;
        private int? _maxPullWaiting;
        private int? _maxBatch;
        private int? _maxBytes;
        private bool? _flowControl;
        private bool? _headersOnly;

        public DeliverPolicy DeliverPolicy => _deliverPolicy ?? DeliverPolicy.All;
        public AckPolicy AckPolicy => _ackPolicy ?? AckPolicy.Explicit;
        public ReplayPolicy ReplayPolicy => _replayPolicy ?? ReplayPolicy.Instant;
        public string Description { get; }
        public string Durable { get; }
        public string DeliverSubject { get; }
        public string DeliverGroup { get; }
        public string FilterSubject { get; }
        public string SampleFrequency { get; }
        public DateTime StartTime { get; }
        public Duration AckWait { get; }
        public Duration IdleHeartbeat { get; }
        public Duration MaxExpires { get; }
        public Duration InactiveThreshold { get; }
        public ulong StartSeq => GetOrUnset(_startSeq);
        public int MaxDeliver => GetOrUnset(_maxDeliver);
        
        [Obsolete("This property is obsolete. Use RateLimitBps.", false)]
        public long RateLimit => (long)GetOrUnset(_rateLimitBps);
        public ulong RateLimitBps => GetOrUnset(_rateLimitBps);
        public int MaxAckPending => GetOrUnset(_maxAckPending);
        public int MaxPullWaiting => GetOrUnset(_maxPullWaiting);
        public int MaxBatch => GetOrUnset(_maxBatch);
        public int MaxBytes => GetOrUnset(_maxBytes);
        public bool FlowControl => _flowControl ?? false;
        public bool HeadersOnly => _headersOnly ?? false;
        public IList<Duration> Backoff { get; }
       
        internal ConsumerConfiguration(string json) : this(JSON.Parse(json)) {}

        internal ConsumerConfiguration(JSONNode ccNode)
        {
            _deliverPolicy = ApiEnums.GetDeliverPolicy(ccNode[ApiConstants.DeliverPolicy].Value);
            _ackPolicy = ApiEnums.GetAckPolicy(ccNode[ApiConstants.AckPolicy].Value);
            _replayPolicy = ApiEnums.GetReplayPolicy(ccNode[ApiConstants.ReplayPolicy]);

            Description = ccNode[ApiConstants.Description].Value;
            Durable = ccNode[ApiConstants.DurableName].Value;
            DeliverSubject = ccNode[ApiConstants.DeliverSubject].Value;
            DeliverGroup = ccNode[ApiConstants.DeliverGroup].Value;
            FilterSubject = ccNode[ApiConstants.FilterSubject].Value;
            SampleFrequency = ccNode[ApiConstants.SampleFreq].Value;
            
            StartTime = AsDate(ccNode[ApiConstants.OptStartTime]);
            AckWait = AsDuration(ccNode, ApiConstants.AckWait, null);
            IdleHeartbeat = AsDuration(ccNode, ApiConstants.IdleHeartbeat, null);
            MaxExpires = AsDuration(ccNode, ApiConstants.MaxExpires, null);
            InactiveThreshold = AsDuration(ccNode, ApiConstants.InactiveThreshold, null);

            _startSeq = ccNode[ApiConstants.OptStartSeq].AsUlongOr(UlongUnset);
            _maxDeliver = ccNode[ApiConstants.MaxDeliver].AsIntOr(IntUnset);
            _rateLimitBps = ccNode[ApiConstants.RateLimitBps].AsUlongOr(UlongUnset);
            _maxAckPending = ccNode[ApiConstants.MaxAckPending].AsIntOr(IntUnset);
            _maxPullWaiting = ccNode[ApiConstants.MaxWaiting].AsIntOr(IntUnset);
            _maxBatch = ccNode[ApiConstants.MaxBatch].AsIntOr(IntUnset);
            _maxBytes = ccNode[ApiConstants.MaxBytes].AsIntOr(IntUnset);
            _flowControl = ccNode[ApiConstants.FlowControl].AsBool;
            _headersOnly = ccNode[ApiConstants.HeadersOnly].AsBool;

            Backoff = DurationList(ccNode, ApiConstants.Backoff);
        }

        private ConsumerConfiguration(ConsumerConfigurationBuilder builder)
        {
            _deliverPolicy = builder._deliverPolicy;
            _ackPolicy = builder._ackPolicy;
            _replayPolicy = builder._replayPolicy;

            Description = builder._description;
            Durable = builder._durable;
            DeliverSubject = builder._deliverSubject;
            DeliverGroup = builder._deliverGroup;
            FilterSubject = builder._filterSubject;
            SampleFrequency = builder._sampleFrequency;

            StartTime = builder._startTime;
            AckWait = builder._ackWait;
            IdleHeartbeat = builder._idleHeartbeat;
            MaxExpires = builder._maxExpires;
            InactiveThreshold = builder._inactiveThreshold;
            _flowControl = builder._flowControl;
            _headersOnly = builder._headersOnly;

            _startSeq = builder._startSeq;
            _maxDeliver = builder._maxDeliver;
            _rateLimitBps = builder._rateLimitBps;
            _maxAckPending = builder._maxAckPending;
            _maxPullWaiting = builder._maxPullWaiting;
            _maxBatch = builder._maxBatch;
            _maxBytes = builder._maxBytes;

            Backoff = builder._backoff;
        }

        internal override JSONNode ToJsonNode()
        {
            JSONObject o = new JSONObject();

            AddField(o, ApiConstants.Description, Description);
            AddField(o, ApiConstants.DurableName, Durable);
            AddField(o, ApiConstants.DeliverPolicy, DeliverPolicy.GetString());
            AddField(o, ApiConstants.DeliverSubject, DeliverSubject);
            AddField(o, ApiConstants.DeliverGroup, DeliverGroup);
            AddField(o, ApiConstants.OptStartSeq, StartSeq);
            AddField(o, ApiConstants.OptStartTime, JsonUtils.ToString(StartTime));
            AddField(o, ApiConstants.AckPolicy, AckPolicy.GetString());
            AddField(o, ApiConstants.AckWait, AckWait);
            AddField(o, ApiConstants.MaxDeliver, MaxDeliver);
            AddField(o, ApiConstants.FilterSubject, FilterSubject);
            AddField(o, ApiConstants.ReplayPolicy, ReplayPolicy.GetString());
            AddField(o, ApiConstants.SampleFreq, SampleFrequency);
            AddField(o, ApiConstants.RateLimitBps, RateLimitBps);
            AddField(o, ApiConstants.MaxAckPending, MaxAckPending);
            AddField(o, ApiConstants.IdleHeartbeat, IdleHeartbeat);
            AddField(o, ApiConstants.FlowControl, FlowControl);
            AddField(o, ApiConstants.MaxWaiting, MaxPullWaiting);
            AddField(o, ApiConstants.HeadersOnly, HeadersOnly);
            AddField(o, ApiConstants.MaxBatch, MaxBatch);
            AddField(o, ApiConstants.MaxBytes, MaxBytes);
            AddField(o, ApiConstants.MaxExpires, MaxExpires);
            AddField(o, ApiConstants.InactiveThreshold, InactiveThreshold);
            AddField(o, ApiConstants.Backoff, Backoff);

            return o;
        }

        internal IList<string> GetChanges(ConsumerConfiguration server)
        {
            IList<string> changes = new List<string>();
            if (_deliverPolicy != null && _deliverPolicy != server.DeliverPolicy) { changes.Add("DeliverPolicy"); }
            if (_ackPolicy != null && _ackPolicy != server.AckPolicy) { changes.Add("AckPolicy"); }
            if (_replayPolicy != null && _replayPolicy != server.ReplayPolicy) { changes.Add("ReplayPolicy"); }

            if (_flowControl != null && _flowControl != server.FlowControl) { changes.Add("FlowControl"); }
            if (_headersOnly != null && _headersOnly != server.HeadersOnly) { changes.Add("HeadersOnly"); }

            if (_startSeq != null && !_startSeq.Equals(server.StartSeq)) { changes.Add("StartSequence"); }
            if (_rateLimitBps != null && !_rateLimitBps.Equals(server.RateLimitBps)) { changes.Add("RateLimitBps"); }

            if (_maxDeliver != null && !_maxDeliver.Equals(server.MaxDeliver)) { changes.Add("MaxDeliver"); }
            if (_maxAckPending != null && !_maxAckPending.Equals(server.MaxAckPending)) { changes.Add("MaxAckPending"); }
            if (_maxPullWaiting != null && !_maxPullWaiting.Equals(server.MaxPullWaiting)) { changes.Add("MaxPullWaiting"); }
            if (_maxBatch != null && !_maxBatch.Equals(server.MaxBatch)) { changes.Add("MaxBatch"); }
            if (_maxBytes != null && !_maxBytes.Equals(server.MaxBytes)) { changes.Add("MaxBytes"); }

            if (AckWait != null && !AckWait.Equals(server.AckWait)) { changes.Add("AckWait"); }
            if (IdleHeartbeat != null && !IdleHeartbeat.Equals(server.IdleHeartbeat)) { changes.Add("IdleHeartbeat"); }
            if (MaxExpires != null && !MaxExpires.Equals(server.MaxExpires)) { changes.Add("MaxExpires"); }
            if (InactiveThreshold != null && !InactiveThreshold.Equals(server.InactiveThreshold)) { changes.Add("InactiveThreshold"); }

            RecordWouldBeChange(StartTime, server.StartTime, "StartTime", changes);
                   
            RecordWouldBeChange(FilterSubject, server.FilterSubject, "FilterSubject", changes);
            RecordWouldBeChange(Description, server.Description, "Description", changes);
            RecordWouldBeChange(SampleFrequency, server.SampleFrequency, "SampleFrequency", changes);
            RecordWouldBeChange(DeliverSubject, server.DeliverSubject, "DeliverSubject", changes);
            RecordWouldBeChange(DeliverGroup, server.DeliverGroup, "DeliverGroup", changes);
                   
            if (!Backoff.SequenceEqual(server.Backoff)) { changes.Add("Backoff"); }
            return changes;
        }

        private void RecordWouldBeChange(string request, string server, string field, IList<string> changes)
        {
            string r = EmptyAsNull(request);
            if (r != null && !r.Equals(EmptyAsNull(server))) { changes.Add(field); }
        }

        private void RecordWouldBeChange(DateTime request, DateTime server, string field, IList<string> changes)
        {
            if (request != DateTime.MinValue && !request.Equals(server)) { changes.Add(field); }
        }
        
        private static int GetOrUnset(int? val)
        {
            return val ?? IntUnset;
        }

        private static ulong GetOrUnset(ulong? val)
        {
            return val ?? UlongUnset;
        }

        internal static int? NormalizeToInt(long? l, long min) {
            if (l == null) {
                return null;
            }

            if (l < min) {
                return IntUnset;
            }

            if (l > int.MaxValue) {
                return int.MaxValue;
            }

            return (int)l;
        }

        internal static ulong? Normalize(ulong? u)
        {
            return u == null ? null : u <= UlongUnset ? UlongUnset : u;
        }

        internal static Duration Normalize(Duration d)
        {
            return d == null ? null : d.Nanos <= DurationUnsetLong ? DurationUnset : d;
        }

        internal static Duration NormalizeDuration(long millis)
        {
            return millis <= DurationUnsetLong ? DurationUnset : Duration.OfMillis(millis);
        }

        public static ConsumerConfigurationBuilder Builder()
        {
            return new ConsumerConfigurationBuilder();
        }
        
        public static ConsumerConfigurationBuilder Builder(ConsumerConfiguration cc)
        {
            return new ConsumerConfigurationBuilder(cc);
        }

        public sealed class ConsumerConfigurationBuilder
        {
            internal DeliverPolicy? _deliverPolicy;
            internal AckPolicy? _ackPolicy;
            internal ReplayPolicy? _replayPolicy;
            
            internal string _description;
            internal string _durable;
            internal string _deliverSubject;
            internal string _deliverGroup;
            internal string _filterSubject;
            internal string _sampleFrequency;
            
            internal DateTime _startTime; 
            internal Duration _ackWait;
            internal Duration _idleHeartbeat;
            internal Duration _maxExpires;
            internal Duration _inactiveThreshold;

            internal ulong? _startSeq;
            internal ulong? _rateLimitBps;
            internal int? _maxDeliver;
            internal int? _maxAckPending;
            internal int? _maxPullWaiting;
            internal int? _maxBatch;
            internal int? _maxBytes;
            internal bool? _flowControl;
            internal bool? _headersOnly;
            internal IList<Duration> _backoff = new List<Duration>();

            public ConsumerConfigurationBuilder() {}

            public ConsumerConfigurationBuilder(ConsumerConfiguration cc)
            {
                if (cc == null) return;
                
                _deliverPolicy = cc._deliverPolicy;
                _ackPolicy = cc._ackPolicy;
                _replayPolicy = cc._replayPolicy;

                _description = cc.Description;
                _durable = cc.Durable;
                _deliverSubject = cc.DeliverSubject;
                _deliverGroup = cc.DeliverGroup;
                _filterSubject = cc.FilterSubject;
                _sampleFrequency = cc.SampleFrequency;

                _startTime = cc.StartTime;
                _ackWait = cc.AckWait;
                _idleHeartbeat = cc.IdleHeartbeat;
                _maxExpires = cc.MaxExpires;
                _inactiveThreshold = cc.InactiveThreshold;

                _startSeq = cc._startSeq;
                _maxDeliver = cc._maxDeliver;
                _rateLimitBps = cc._rateLimitBps;
                _maxAckPending = cc._maxAckPending;
                _maxPullWaiting = cc._maxPullWaiting;
                _maxBatch = cc._maxBatch;
                _maxBytes = cc._maxBytes;
                _flowControl = cc._flowControl;
                _headersOnly = cc._headersOnly;
                _backoff = new List<Duration>(cc.Backoff);
            }

            /// <summary>
            /// Sets the description.
            /// </summary>
            /// <param name="description">the description</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithDescription(string description)
            {
                _description = EmptyAsNull(description);
                return this;
            }

            /// <summary>
            /// Sets the name of the durable subscription.
            /// </summary>
            /// <param name="durable">name of the durable subscription.</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithDurable(string durable)
            {
                _durable = EmptyAsNull(durable);
                return this;
            }

            /// <summary>
            /// Sets the delivery policy of the ConsumerConfiguration.
            /// </summary>
            /// <param name="policy">the delivery policy.</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithDeliverPolicy(DeliverPolicy? policy)
            {
                _deliverPolicy = policy;
                return this;
            }

            /// <summary>
            /// Sets the subject to deliver messages to.
            /// </summary>
            /// <param name="deliverSubject">the delivery subject.</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithDeliverSubject(string deliverSubject)
            {
                _deliverSubject = EmptyAsNull(deliverSubject);
                return this;
            }

            /// <summary>
            /// Sets the group to deliver messages to.
            /// </summary>
            /// <param name="group">the delivery group.</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithDeliverGroup(string group)
            {
                _deliverGroup = EmptyAsNull(group);
                return this;
            }

            /// <summary>
            /// Sets the start sequence of the ConsumerConfiguration.
            /// </summary>
            /// <param name="sequence">the start sequence</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithStartSequence(ulong? sequence)
            {
                _startSeq = Normalize(sequence);
                return this;
            }

            /// <summary>
            /// Sets the start time of the ConsumerConfiguration.
            /// </summary>
            /// <param name="startTime">the start time</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithStartTime(DateTime startTime)
            {
                _startTime = startTime;
                return this;
            }

            /// <summary>
            /// Sets the acknowledgement policy of the ConsumerConfiguration.
            /// </summary>
            /// <param name="policy">the acknowledgement policy.</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithAckPolicy(AckPolicy? policy)
            {
                _ackPolicy = policy;
                return this;
            }

            /// <summary>
            /// Sets the acknowledgement wait duration of the ConsumerConfiguration.
            /// </summary>
            /// <param name="timeout">the wait timeout as a duration</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithAckWait(Duration timeout)
            {
                _ackWait = Normalize(timeout); 
                return this;
            }

            /// <summary>
            /// Sets the acknowledgement wait duration of the ConsumerConfiguration.
            /// </summary>
            /// <param name="timeoutMillis">the wait timeout as millis</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithAckWait(long timeoutMillis)
            {
                _ackWait = NormalizeDuration(timeoutMillis); 
                return this;
            }

            /// <summary>
            /// Sets the maximum delivery amount of the ConsumerConfiguration.
            /// </summary>
            /// <param name="maxDeliver">the maximum delivery amount</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithMaxDeliver(long? maxDeliver)
            {
                _maxDeliver = NormalizeToInt(maxDeliver, MaxDeliverMin);
                return this;
            }

            /// <summary>
            /// Sets the filter subject of the ConsumerConfiguration.
            /// </summary>
            /// <param name="filterSubject">the filter subject</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithFilterSubject(string filterSubject)
            {
                _filterSubject = EmptyAsNull(filterSubject);
                return this;
            }

            /// <summary>
            /// Sets the replay policy of the ConsumerConfiguration.
            /// </summary>
            /// <param name="policy">the replay policy.</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithReplayPolicy(ReplayPolicy? policy)
            {
                _replayPolicy = policy;
                return this;
            }

            /// <summary>
            /// Sets the sample frequency of the ConsumerConfiguration.
            /// </summary>
            /// <param name="frequency">the frequency</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithSampleFrequency(string frequency)
            {
                _sampleFrequency = EmptyAsNull(frequency);
                return this;
            }

            /// <summary>
            /// Set the rate limit of the ConsumerConfiguration.
            /// </summary>
            /// <param name="bitsPerSecond">bits per second to deliver</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            [Obsolete("This method is obsolete. Use WithRateLimit with ulong parameter.", false)]
            public ConsumerConfigurationBuilder WithRateLimit(long? bitsPerSecond)
            {
                if (bitsPerSecond == null)
                {
                    _rateLimitBps = null;
                }
                else if (bitsPerSecond < 0)
                {
                    _rateLimitBps = UlongUnset;
                }
                else
                {
                    _rateLimitBps = Normalize((ulong)bitsPerSecond);
                }
                return this;
            }

            /// <summary>
            /// Set the rate limit of the ConsumerConfiguration.
            /// </summary>
            /// <param name="bitsPerSecond">bits per second to deliver</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithRateLimitBps(ulong? bitsPerSecond)
            {
                _rateLimitBps = Normalize(bitsPerSecond);
                return this;
            }

            /// <summary>
            /// Sets the maximum ack pending.
            /// </summary>
            /// <param name="maxAckPending">maximum pending acknowledgements.</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithMaxAckPending(long? maxAckPending)
            {
                _maxAckPending = NormalizeToInt(maxAckPending, StandardMin);
                return this;
            }

            /// <summary>
            /// Sets the idle heart beat wait time.
            /// </summary>
            /// <param name="idleHeartbeat">the wait timeout as a Duration</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithIdleHeartbeat(Duration idleHeartbeat)
            {
                if (idleHeartbeat == null) {
                    _idleHeartbeat = null;
                }
                else
                {
                    long nanos = idleHeartbeat.Nanos;
                    if (nanos <= DurationUnsetLong) {
                        _idleHeartbeat = DurationUnset;
                    }
                    else if (nanos < MinIdleHeartbeatNanos) {
                        throw new ArgumentException($"Duration must be greater than or equal to {MinIdleHeartbeatNanos} nanos.");
                    }
                    else {
                        _idleHeartbeat = idleHeartbeat;
                    }
                }

                return this;
            }

            /// <summary>
            /// Sets the idle heart beat wait time.
            /// </summary>
            /// <param name="idleHeartbeatMillis">the wait timeout as milliseconds</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithIdleHeartbeat(long idleHeartbeatMillis)
            {
                if (idleHeartbeatMillis <= DurationUnsetLong) {
                    _idleHeartbeat = DurationUnset;
                }
                else if (idleHeartbeatMillis < MinIdleHeartbeatMillis) {
                    throw new ArgumentException($"Duration must be greater than or equal to {MinIdleHeartbeatMillis} milliseconds.");
                }
                else {
                    _idleHeartbeat = Duration.OfMillis(idleHeartbeatMillis);
                }
                return this;
            }

            /// <summary>
            /// Set the flow control on and set the idle heartbeat
            /// </summary>
            /// <param name="idleHeartbeat">the idle heart beat as a Duration</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithFlowControl(Duration idleHeartbeat) {
                _flowControl = true;
                return WithIdleHeartbeat(idleHeartbeat);
            }

            /// <summary>
            /// Set the flow control on and set the idle heartbeat
            /// </summary>
            /// <param name="idleHeartbeatMillis">the idle heart beat as milliseconds</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithFlowControl(long idleHeartbeatMillis) {
                _flowControl = true;
                return WithIdleHeartbeat(idleHeartbeatMillis);
            }

            /// <summary>
            /// Set the max amount of expire time for the server to allow on pull requests.
            /// </summary>
            /// <param name="maxExpires">the max amount of expire as a Duration</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithMaxExpires(Duration maxExpires) {
                _maxExpires = Normalize(maxExpires);
                return this;
            }

            /// <summary>
            /// Set the max amount of expire time for the server to allow on pull requests.
            /// </summary>
            /// <param name="maxExpiresMillis">the max amount of expire as milliseconds</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithMaxExpires(long maxExpiresMillis) {
                _maxExpires = NormalizeDuration(maxExpiresMillis);
                return this;
            }

            /// <summary>
            /// Set the amount of time before the ephemeral consumer is deemed inactive.
            /// </summary>
            /// <param name="inactiveThreshold">the max amount of expire as a Duration</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithInactiveThreshold(Duration inactiveThreshold) {
                _inactiveThreshold = Normalize(inactiveThreshold);
                return this;
            }

            /// <summary>
            /// Set the amount of time before the ephemeral consumer is deemed inactive.
            /// </summary>
            /// <param name="inactiveThresholdMillis">the max amount of expire as milliseconds</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithInactiveThreshold(long inactiveThresholdMillis) {
                _inactiveThreshold = NormalizeDuration(inactiveThresholdMillis);
                return this;
            }

            /// <summary>
            /// Sets the maximum pull waiting.
            /// </summary>
            /// <param name="maxPullWaiting">the maximum delivery amount</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithMaxPullWaiting(long? maxPullWaiting)
            {
                _maxPullWaiting = NormalizeToInt(maxPullWaiting, StandardMin);
                return this;
            }

            /// <summary>
            /// Sets the max batch size for the server to allow on pull requests.
            /// </summary>
            /// <param name="maxBatch">the maximum batch size</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithMaxBatch(long? maxBatch)
            {
                _maxBatch = NormalizeToInt(maxBatch, StandardMin);
                return this;
            }

            /// <summary>
            /// Sets the max bytes size for the server to allow on pull requests.
            /// </summary>
            /// <param name="maxBytes">the maximum bytes size</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithMaxBytes(long? maxBytes)
            {
                _maxBytes = NormalizeToInt(maxBytes, StandardMin);
                return this;
            }

            /// <summary>
            /// Sets the headers only flag
            /// </summary>
            /// <param name="headersOnly">true to enable flow control.</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithHeadersOnly(bool? headersOnly) {
                _headersOnly = headersOnly;
                return this;
            }

            /// <summary>
            /// Sets the list of backoff
            /// </summary>
            /// <param name="backoffs">zero or more backoff durations or an array of backoffs</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithBackoff(params Duration[] backoffs) {
                _backoff.Clear();
                if (backoffs != null) {
                    foreach (Duration d in backoffs)
                    {
                        if (d != null) {
                            if (d.Nanos < DurationMinLong)
                            {
                                throw new ArgumentException($"Backoff cannot be less than {DurationMinLong}");
                            }
                            _backoff.Add(d);
                        }
                    }
                }
                return this;
            }

            /// <summary>
            /// Sets the list of backoff
            /// </summary>
            /// <param name="backoffsMillis">zero or more backoff in millis or an array of backoffsMillis</param>
            /// <returns>The ConsumerConfigurationBuilder</returns>
            public ConsumerConfigurationBuilder WithBackoff(params long[] backoffsMillis) {
                _backoff.Clear();
                if (backoffsMillis != null) {
                    foreach (long ms in backoffsMillis) {
                        if (ms < DurationMinLong)
                        {
                            throw new ArgumentException($"Backoff cannot be less than {DurationMinLong}");
                        }
                        _backoff.Add(Duration.OfMillis(ms));
                    }
                }
                return this;
            }

            /// <summary>
            /// Builds the ConsumerConfiguration
            /// </summary>
            /// <returns>The ConsumerConfiguration</returns>
            public ConsumerConfiguration Build()
            {
                return new ConsumerConfiguration(this);
            }

            /// <summary>
            /// Builds the PushSubscribeOptions with this configuration
            /// </summary>
            /// <returns>The PushSubscribeOptions</returns>
            public PushSubscribeOptions BuildPushSubscribeOptions()
            {
                return PushSubscribeOptions.Builder().WithConfiguration(Build()).Build();
            }

            /// <summary>
            /// Builds the PullSubscribeOptions with this configuration
            /// </summary>
            /// <returns>The PullSubscribeOptions</returns>
            public PullSubscribeOptions BuildPullSubscribeOptions()
            {
                return PullSubscribeOptions.Builder().WithConfiguration(Build()).Build();
            }
        }
    }
}
