﻿/* This Source Code Form is subject to the terms of the Mozilla internal
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using NetMQ.Sockets;

namespace NetMQ.Zyre
{
    public class ZrePeer : IDisposable
    {
        private const int PeerEvasive = 10000; // 10 seconds' silence is evasive
        private const int PeerExpired = 30000; // 30 seconds' silence is expired
        private const ushort UshortMax = ushort.MaxValue;
        private const byte UbyteMax = byte.MaxValue;

        #region Private Variables

        /// <summary>
        /// Socket through to peer.
        /// DealerSocket connected to the peer's RouterSocket.
        /// ZRE messages are sent from this _mailbox for each peer
        ///    and they are received by the ZreNode's RouterSocket _inbox.
        /// </summary>
        private DealerSocket _mailbox;

        /// <summary>
        /// Identity guid of the peer, 16 bytes
        /// </summary>
        private readonly Guid _uuid;

        /// <summary>
        /// Endpoint of the peer connected to
        /// </summary>
        private string _endpoint;

        /// <summary>
        /// Peer's internal name
        /// </summary>
        private string _name;

        /// <summary>
        /// Origin node's internal name
        /// </summary>
        private string _origin;

        /// <summary>
        /// Peer is being evasive
        /// </summary>
        private long _evasiveAt;

        /// <summary>
        /// Peer has expired by now
        /// </summary>
        private long _expiredAt;

        /// <summary>
        /// Peer will send messages
        /// </summary>
        private bool _connected;

        /// <summary>
        /// Peer has said Hello to us
        /// </summary>
        private bool _ready;

        /// <summary>
        /// Our status counter
        /// </summary>
        private byte _status;

        /// <summary>
        /// Outgoing message sequence counter used for detecting lost messages
        /// </summary>
        private ushort _sentSequence;

        /// <summary>
        /// Incoming message sequence counter used for detecting lost messages
        /// </summary>
        private ushort _wantSequence;

        /// <summary>
        /// Peer headers
        /// </summary>
        private Dictionary<string, string> _headers;

        /// <summary>
        /// Optional logger action passed into ctor
        /// </summary>
        private readonly Action<string> _loggerDelegate;

        #endregion Private Variables


        private ZrePeer(Guid uuid, Action<string> loggerDelegate = null)
        {
            _uuid = uuid;
            _loggerDelegate = loggerDelegate;
            _ready = false;
            _connected = false;
            _sentSequence = 0;
            _wantSequence = 0;
            _headers = new Dictionary<string, string>();
        }

        /// <summary>
        /// Construct new ZrePeer object
        /// </summary>
        /// <param name="container">The dictionary of peers</param>
        /// <param name="guid">The identity for this peer</param>
        /// <param name="verboseAction">An action to take for logging when _verbose is true. Default is null.</param>
        /// <returns></returns>
        internal static ZrePeer NewPeer(Dictionary<Guid, ZrePeer> container, Guid guid, Action<string> verboseAction = null)
        {
            var peer = new ZrePeer(guid, verboseAction);
            container[guid] = peer; // overwrite any existing entry for same uuid
            return peer;
        }

        /// <summary>
        /// Disconnect this peer and Dispose this class.
        /// </summary>
        internal void Destroy()
        {
            Debug.Assert(_mailbox != null, "Mailbox must not be null");
            Disconnect();
            Dispose();
        }

        /// <summary>
        /// Connect peer mailbox
        /// Configures a DealerSocket mailbox connected to peer's router endpoint
        /// </summary>
        /// <param name="replyTo"></param>
        /// <param name="endpoint"></param>
        internal void Connect(Guid replyTo, string endpoint)
        {
            Debug.Assert(!_connected);

            //  Create new outgoing socket (drop any messages in transit)
            _mailbox = new DealerSocket(endpoint) // default action is to connect to the peer node
            {
                Options =
                {
                    //  Set our own identity on the socket so that receiving node
                    //  knows who each message came from. Note that we cannot use
                    //  the UUID directly as the identity since it may contain a
                    //  zero byte at the start, which libzmq does not like for
                    //  historical and arguably bogus reasons that it nonetheless
                    //  enforces.
                    Identity = GetIdentity(replyTo), 

                    //  Set a high-water mark that allows for reasonable activity
                    SendHighWatermark = PeerExpired * 100,

                    // SendTimeout = TimeSpan.Zero Instead of this, ZreMsg.Send() uses TrySend() with TimeSpan.Zero
                }
            };
            _loggerDelegate?.Invoke($"({_origin}) (identity={replyTo.ToShortString6()}) DealerSocket mailbox is connecting to peer endPoint={_endpoint}, ");
            _mailbox.Connect(endpoint);
            _endpoint = endpoint;
            _connected = true;
            _ready = false;
        }

        internal static byte[] GetIdentity(Guid replyTo)
        {
            var result = new byte[17];
            result[0] = 1;
            var uuidBytes = replyTo.ToByteArray();
            Buffer.BlockCopy(uuidBytes, 0, result, 1, 16);
            return result;
        }

        /// <summary>
        /// Disconnect peer mailbox 
        /// No more messages will be sent to peer until connected again
        /// </summary>
        internal void Disconnect()
        {
            _mailbox.Dispose();
            _mailbox = null;
            _endpoint = null;
            _connected = false;
            _ready = false;
        }

        /// <summary>
        /// Send message to peer
        /// </summary>
        /// <param name="msg">the message</param>
        /// <returns>always true</returns>
        internal bool Send(ZreMsg msg)
        {
            if (_connected)
            {
                msg.Sequence = ++_sentSequence;
                _loggerDelegate?.Invoke($"({_origin}) ZrePeer.Send() sending message={msg} from peer={this}");
                msg.Send(_mailbox);
            }
            return true;
        }

        /// <summary>
        /// Return peer connected status. True when connected and peer will send messages.
        /// </summary>
        internal bool Connected
        {
            get { return _connected; }
        }

        /// <summary>
        /// Return peer identity string
        /// </summary>
        internal Guid Uuid
        {
            get { return _uuid; }
        }

        /// <summary>
        /// Return peer connection endpoint
        /// </summary>
        internal string Endpoint
        {
            get { return _endpoint; }
        }

        /// <summary>
        /// Register activity at peer
        /// </summary>
        internal void Refresh()
        {
            _evasiveAt = CurrentTimeMilliseconds() + PeerEvasive;
            _expiredAt = CurrentTimeMilliseconds() + PeerExpired;
        }

        /// <summary>
        /// Milliseconds since January 1, 1970 UTC
        /// </summary>
        /// <returns></returns>
        internal static long CurrentTimeMilliseconds()
        {
            return (DateTime.UtcNow.Ticks - 621355968000000000) / 10000;
        }

        /// <summary>
        /// Return peer future expired time
        /// </summary>
        internal long EvasiveAt
        {
            get { return _evasiveAt; }
        }

        /// <summary>
        /// Return peer future evasive time
        /// </summary>
        internal long ExpiredAt
        {
            get { return _expiredAt; }
        }

        /// <summary>
        /// Return peer name
        /// </summary>
        internal string Name
        {
            get { return _name ?? ""; }
        }

        /// <summary>
        /// Return peer cycle
        /// This gives us a state change count for the peer, which we can
        /// check against its claimed status, to detect message loss.
        /// </summary>
        internal byte Status
        {
            get { return _status; }
        }

        /// <summary>
        /// Increment status
        /// </summary>
        internal void IncrementStatus()
        {
            _status = _status == UbyteMax ? (byte) 0 : ++_status;
        }

        /// <summary>
        /// Set peer name
        /// </summary>
        /// <param name="name"></param>
        internal void SetName(string name)
        {
            _name = name;
        }

        /// <summary>
        /// Set current node name, for logging
        /// </summary>
        /// <param name="originNodeName"></param>
        internal void SetOrigin(string originNodeName)
        {
            _origin = originNodeName;
        }

        /// <summary>
        /// Set peer status
        /// </summary>
        /// <param name="status"></param>
        internal void SetStatus(byte status)
        {
            _status = status;
        }

        /// <summary>
        /// Return peer ready state. True when peer has said Hello to us
        /// </summary>
        internal bool Ready
        {
            get { return _ready; }
        }

        /// <summary>
        /// Set peer ready
        /// </summary>
        /// <param name="ready"></param>
        internal void SetReady(bool ready)
        {
            _ready = ready;
        }

        /// <summary>
        /// Get peer header value
        /// </summary>
        /// <param name="key">The he</param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        internal string Header(string key, string defaultValue)
        {
            string value;
            if (!_headers.TryGetValue(key, out value))
            {
                return defaultValue;
            }
            return value;
        }

        /// <summary>
        /// Get peer headers table
        /// </summary>
        /// <returns></returns>
        internal Dictionary<string, string> GetHeaders()
        {
            return _headers;
        }

        /// <summary>
        /// Set peer headers from provided dictionary
        /// </summary>
        /// <returns></returns>
        internal void SetHeaders(Dictionary<string, string> headers)
        {
            _headers = headers;
        }

        /// <summary>
        /// Check if messages were lost from peer, returns true if they were
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>true if we have lost one or more message</returns>
        internal bool MessagesLost(ZreMsg msg)
        {
            Debug.Assert(msg != null);

            //  The sequence number set by the peer and our own calculated sequence number should be the same.
            if (msg.Id == ZreMsg.MessageId.Hello)
            {
                //  HELLO always MUST have sequence = 1
                _wantSequence = 1;
            }
            else
            {
                _wantSequence = _wantSequence == UshortMax ? (ushort) 0 : ++_wantSequence;
            }
            if (_wantSequence != msg.Sequence)
            {
                if (_loggerDelegate != null)
                {
                    _loggerDelegate($"({_origin}) seq error from peer={_name} expect={_wantSequence}, got={msg.Sequence}");
                }
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            var name = string.IsNullOrEmpty(_name) ? "NotSet" : _name;
            var origin = string.IsNullOrEmpty(_origin) ? "NotSet" : _origin;
            return
                $"from [origin:{origin} to name:{name} endpoint:{_endpoint} connected:{_connected} ready:{_ready} status:{_status} _sentSeq:{_sentSequence} _wantSeq:{_wantSequence} _ guidShort:{_uuid.ToShortString6()}]";
        }

        /// <summary>
        /// Release any contained resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Release any contained resources.
        /// </summary>
        /// <param name="disposing">true if managed resources are to be released</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            if (_mailbox != null)
            {
                _mailbox.Dispose();
                _mailbox = null;
            }
        }
    }
}
