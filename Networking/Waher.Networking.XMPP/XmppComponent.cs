﻿using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
#if WINDOWS_UWP
using Windows.Foundation;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Security.Cryptography.Certificates;
using Windows.Storage.Streams;
#else
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
#endif
using Waher.Content;
using Waher.Events;
using Waher.Networking;
using Waher.Networking.Sniffers;
using Waher.Networking.XMPP.Authentication;
using Waher.Networking.XMPP.AuthenticationErrors;
using Waher.Networking.XMPP.StanzaErrors;
using Waher.Networking.XMPP.StreamErrors;
using Waher.Networking.XMPP.DataForms;
using Waher.Networking.XMPP.ServiceDiscovery;
using Waher.Networking.XMPP.SoftwareVersion;
using Waher.Networking.XMPP.Search;

namespace Waher.Networking.XMPP
{
	/// <summary>
	/// Delegate for event raised to get roster items for the component.
	/// </summary>
	/// <param name="BareJid">Bare JID</param>
	/// <returns>Corresponding roster item, if found, or null, if not found.</returns>
	public delegate RosterItem GetRosterItemEventHandler(string BareJid);

	/// <summary>
	/// Manages an XMPP component connection, as defined in XEP-0114:
	/// http://xmpp.org/extensions/xep-0114.html
	/// </summary>
	public class XmppComponent : Sniffable, IDisposable
	{
		private const int BufferSize = 16384;
		private const int KeepAliveTimeSeconds = 30;

		private LinkedList<KeyValuePair<byte[], EventHandler>> outputQueue = new LinkedList<KeyValuePair<byte[], EventHandler>>();
		private Dictionary<uint, PendingRequest> pendingRequestsBySeqNr = new Dictionary<uint, PendingRequest>();
		private SortedDictionary<DateTime, PendingRequest> pendingRequestsByTimeout = new SortedDictionary<DateTime, PendingRequest>();
		private Dictionary<string, IqEventHandler> iqGetHandlers = new Dictionary<string, IqEventHandler>();
		private Dictionary<string, IqEventHandler> iqSetHandlers = new Dictionary<string, IqEventHandler>();
		private Dictionary<string, MessageEventHandler> messageHandlers = new Dictionary<string, MessageEventHandler>();
		private Dictionary<string, MessageEventArgs> receivedMessages = new Dictionary<string, MessageEventArgs>();
		private Dictionary<string, bool> clientFeatures = new Dictionary<string, bool>();
		private Dictionary<string, int> pendingAssuredMessagesPerSource = new Dictionary<string, int>();
		private object rosterSyncObject = new object();
#if WINDOWS_UWP
		private StreamSocket client = null;
		private DataWriter dataWriter = null;
		private DataReader dataReader = null;
		private MemoryBuffer memoryBuffer = new MemoryBuffer(BufferSize);
		private IBuffer buffer = null;
#else
		private TcpClient client = null;
		private Stream stream = null;
		private byte[] buffer = new byte[BufferSize];
#endif
		private Timer secondTimer = null;
		private DateTime nextPing = DateTime.MinValue;
		private UTF8Encoding encoding = new UTF8Encoding(false, false);
		private StringBuilder fragment = new StringBuilder();
		private XmppState state;
		private Random gen = new Random();
		private object synchObject = new object();
		private string identityCategory;
		private string identityType;
		private string identityName;
		private string host;
		private string componentSubDomain;
		private string sharedSecret;
		private string streamId;
		private string streamHeader;
		private string streamFooter;
		private uint seqnr = 0;
		private int port;
		private int keepAliveSeconds = 30;
		private int inputState = 0;
		private int inputDepth = 0;
		private int defaultRetryTimeout = 2000;
		private int defaultNrRetries = 5;
		private int defaultMaxRetryTimeout = int.MaxValue;
		private int maxAssuredMessagesPendingFromSource = 5;
		private int maxAssuredMessagesPendingTotal = 100;
		private int nrAssuredMessagesPending = 0;
		private bool defaultDropOff = true;
		private bool isWriting = false;
		private bool supportsPing = true;

		/// <summary>
		/// Manages an XMPP component connection, as defined in XEP-0114:
		/// http://xmpp.org/extensions/xep-0114.html
		/// </summary>
		/// <param name="Host">Host name or IP address of XMPP server.</param>
		/// <param name="Port">Port to connect to.</param>
		/// <param name="Tls">If TLS is used to encrypt communication.</param>
		/// <param name="ComponentSubDomain">Component sub-domain.</param>
		/// <param name="SharedSecret">Shared secret for the component.</param>
		/// <param name="IdentityCategory">Identity category, as defined in XEP-0030.</param>
		/// <param name="IdentityType">Identity type, as defined in XEP-0030.</param>
		/// <param name="IdentityName">Identity name, as defined in XEP-0030.</param>
		public XmppComponent(string Host, int Port, string ComponentSubDomain, string SharedSecret,
			string IdentityCategory, string IdentityType, string IdentityName)
		{
			this.identityCategory = IdentityCategory;
			this.identityType = IdentityType;
			this.identityName = IdentityName;
			this.host = Host;
			this.port = Port;
			this.componentSubDomain = ComponentSubDomain;
			this.sharedSecret = SharedSecret;
			this.state = XmppState.Connecting;

			this.RegisterDefaultHandlers();
			this.Connect();
		}

#if WINDOWS_UWP
		private async void Connect()
#else
		private void Connect()
#endif
		{
			this.State = XmppState.Connecting;

#if WINDOWS_UWP
			this.client = new StreamSocket();
			try
			{
				await this.client.ConnectAsync(new HostName(Host), Port.ToString(), SocketProtectionLevel.PlainSocket);     // Allow use of service name "xmpp-client"

				this.State = XmppState.StreamNegotiation;

				this.dataReader = new DataReader(this.client.InputStream);
				this.dataWriter = new DataWriter(this.client.OutputStream);

				this.BeginWrite("<?xml version='1.0'?><stream:stream to='" + XML.Encode(this.componentSubDomain) + 
					"' xmlns='jabber:component:accept' xmlns:stream='http://etherx.jabber.org/streams'>", null);

				this.ResetState(false);
				this.BeginRead();
			}
			catch (Exception ex)
			{
				this.ConnectionError(ex);
			}
#else
			this.client = new TcpClient();
			this.client.BeginConnect(Host, Port, this.ConnectCallback, null);
#endif
		}

		private void RegisterDefaultHandlers()
		{
			this.RegisterIqGetHandler("query", XmppClient.NamespaceServiceDiscoveryInfo, this.ServiceDiscoveryRequestHandler, true);
			this.RegisterIqSetHandler("acknowledged", XmppClient.NamespaceQualityOfService, this.AcknowledgedQoSMessageHandler, true);
			this.RegisterIqSetHandler("assured", XmppClient.NamespaceQualityOfService, this.AssuredQoSMessageHandler, false);
			this.RegisterIqSetHandler("deliver", XmppClient.NamespaceQualityOfService, this.DeliverQoSMessageHandler, false);
			this.RegisterIqGetHandler("ping", XmppClient.NamespacePing, this.PingRequestHandler, true);
		}

#if !WINDOWS_UWP
		private void ConnectCallback(IAsyncResult ar)
		{
			try
			{
				this.client.EndConnect(ar);
			}
			catch (Exception ex)
			{
				this.ConnectionError(ex);
				return;
			}

			this.stream = new NetworkStream(this.client.Client, false);

			this.State = XmppState.StreamNegotiation;

			this.BeginWrite("<?xml version='1.0'?><stream:stream to='" + XML.Encode(this.componentSubDomain) +
				"' xmlns='jabber:component:accept' xmlns:stream='http://etherx.jabber.org/streams'>", null);

			this.ResetState(false);
			this.BeginRead();
		}
#endif

		private void ResetState(bool Authenticated)
		{
			this.inputState = 0;
			this.inputDepth = 0;

			this.pendingRequestsBySeqNr.Clear();
			this.pendingRequestsByTimeout.Clear();
		}

		private void ConnectionError(Exception ex)
		{
			XmppExceptionEventHandler h = this.OnConnectionError;
			if (h != null)
			{
				try
				{
					h(this, ex);
				}
				catch (Exception ex2)
				{
					Exception(ex2);
				}
			}

			this.Error(ex);

			this.inputState = -1;
			this.DisposeClient();
			this.State = XmppState.Error;
		}

		private void Error(Exception ex)
		{
			this.Error(ex.Message);

			XmppExceptionEventHandler h = this.OnError;
			if (h != null)
			{
				try
				{
					h(this, ex);
				}
				catch (Exception ex2)
				{
					Exception(ex2);
				}
			}
		}

		/// <summary>
		/// Event raised when a connection to a broker could not be made.
		/// </summary>
		public event XmppExceptionEventHandler OnConnectionError = null;

		/// <summary>
		/// Event raised when an error was encountered.
		/// </summary>
		public event XmppExceptionEventHandler OnError = null;

		/// <summary>
		/// Host or IP address of XMPP server.
		/// </summary>
		public string Host
		{
			get { return this.host; }
		}

		/// <summary>
		/// Port number to connect to.
		/// </summary>
		public int Port
		{
			get { return this.port; }
		}

		/// <summary>
		/// Current state of connection.
		/// </summary>
		public XmppState State
		{
			get { return this.state; }
			internal set
			{
				if (this.state != value)
				{
					this.state = value;

					this.Information("State changed to " + value.ToString());

					StateChangedEventHandler h = this.OnStateChanged;
					if (h != null)
					{
						try
						{
							h(this, value);
						}
						catch (Exception ex)
						{
							Exception(ex);
						}
					}
				}
			}
		}

		/// <summary>
		/// Event raised whenever the internal state of the connection changes.
		/// </summary>
		public event StateChangedEventHandler OnStateChanged = null;

		/// <summary>
		/// Closes the connection and disposes of all resources.
		/// </summary>
		public void Dispose()
		{
			if (this.state == XmppState.Connected || this.state == XmppState.FetchingRoster || this.state == XmppState.SettingPresence)
				this.BeginWrite(this.streamFooter, this.CleanUp);
			else
				this.CleanUp(this, new EventArgs());
		}

		/// <summary>
		/// Closes the connection the hard way. This might disrupt stream processing, but can simulate a lost connection. To close the connection
		/// softly, call the <see cref="Dispose"/> method.
		/// 
		/// Note: After turning the connection hard-offline, you can reconnect to the server calling the <see cref="Reconnect"/> method.
		/// </summary>
		public void HardOffline()
		{
			this.CleanUp(this, new EventArgs());
		}

		private void CleanUp(object Sender, EventArgs e)
		{
			this.State = XmppState.Offline;

			if (this.outputQueue != null)
			{
				lock (this.outputQueue)
				{
					this.outputQueue.Clear();
				}
			}

			if (this.pendingRequestsBySeqNr != null)
			{
				lock (this.synchObject)
				{
					this.pendingRequestsBySeqNr.Clear();
					this.pendingRequestsByTimeout.Clear();
				}
			}

			if (this.secondTimer != null)
			{
				this.secondTimer.Dispose();
				this.secondTimer = null;
			}

			this.DisposeClient();

#if WINDOWS_UWP
			if (this.memoryBuffer != null)
			{
				this.memoryBuffer.Dispose();
				this.memoryBuffer = null;
			}
#endif
		}

		private void DisposeClient()
		{
#if WINDOWS_UWP
			if (this.dataReader != null)
			{
				this.dataReader.Dispose();
				this.dataReader = null;
			}

			if (this.dataWriter != null)
			{
				this.dataWriter.Dispose();
				this.dataWriter = null;
			}

			if (this.client != null)
			{
				this.client.Dispose();
				this.client = null;
			}
#else
			if (this.stream != null)
			{
				this.stream.Dispose();
				this.stream = null;
			}

			if (this.client != null)
			{
				this.client.Close();
				this.client = null;
			}
#endif
		}

		/// <summary>
		/// Reconnects a client after an error or if it's offline. Reconnecting, instead of creating a completely new connection,
		/// saves time. It binds to the same resource provided earlier, and avoids fetching the roster.
		/// </summary>
		public void Reconnect()
		{
			this.DisposeClient();
			this.Connect();
		}

		private void BeginWrite(string Xml, EventHandler Callback)
		{
			TransmitText(Xml);

			byte[] Packet = this.encoding.GetBytes(Xml);

			lock (this.outputQueue)
			{
				if (this.isWriting)
					this.outputQueue.AddLast(new KeyValuePair<byte[], EventHandler>(Packet, Callback));
				else
				{
					this.isWriting = true;
					this.DoBeginWriteLocked(Packet, Callback);
				}
			}
		}

#if WINDOWS_UWP
		private async void DoBeginWriteLocked(byte[] Packet, EventHandler Callback)
		{
			try
			{
				this.dataWriter.WriteBytes(Packet);
				await this.dataWriter.StoreAsync();

				this.EndWriteOk(Callback);
			}
			catch (Exception ex)
			{
				lock (this.outputQueue)
				{
					this.outputQueue.Clear();
					this.isWriting = false;
				}

				this.ConnectionError(ex);
			}
		}
#else
		private void DoBeginWriteLocked(byte[] Packet, EventHandler Callback)
		{
			this.stream.BeginWrite(Packet, 0, Packet.Length, this.EndWrite, Callback);
		}

		private void EndWrite(IAsyncResult ar)
		{
			if (this.stream == null)
				return;

			try
			{
				this.stream.EndWrite(ar);
				this.EndWriteOk((EventHandler)ar.AsyncState);
			}
			catch (Exception ex)
			{
				lock (this.outputQueue)
				{
					this.outputQueue.Clear();
					this.isWriting = false;
				}

				this.ConnectionError(ex);
			}
		}
#endif
		private void EndWriteOk(EventHandler h)
		{
			this.nextPing = DateTime.Now.AddMilliseconds(this.keepAliveSeconds * 500);

			if (h != null)
			{
				try
				{
					h(this, new EventArgs());
				}
				catch (Exception ex)
				{
					Exception(ex);
				}
			}

			lock (this.outputQueue)
			{
				LinkedListNode<KeyValuePair<byte[], EventHandler>> Next = this.outputQueue.First;

				if (Next == null)
					this.isWriting = false;
				else
				{
					this.outputQueue.RemoveFirst();
					this.isWriting = true;
					this.DoBeginWriteLocked(Next.Value.Key, Next.Value.Value);
				}
			}
		}

#if WINDOWS_UWP
		private async void BeginRead()
		{
			try
			{
				while (true)
				{
					IBuffer DataRead = await this.client.InputStream.ReadAsync(this.buffer, BufferSize, InputStreamOptions.Partial);
					byte[] Data;
					CryptographicBuffer.CopyToByteArray(DataRead, out Data);
					int NrRead = Data.Length;

					if (NrRead > 0)
					{
						string s = this.encoding.GetString(Data, 0, NrRead);
						this.ReceiveText(s);

						if (!this.ParseIncoming(s))
							break;
					}
					else
						break;
				}
			}
			catch (NullReferenceException)
			{
				// Client closed.
			}
			catch (Exception ex)
			{
				this.ConnectionError(ex);
			}
		}
#else
		private void BeginRead()
		{
			this.stream.BeginRead(this.buffer, 0, BufferSize, this.EndRead, null);
		}

		private void EndRead(IAsyncResult ar)
		{
			string s;
			int NrRead;

			if (this.stream == null)
				return;

			try
			{
				NrRead = this.stream.EndRead(ar);
				if (NrRead > 0)
				{
					s = this.encoding.GetString(this.buffer, 0, NrRead);
					this.ReceiveText(s);

					if (this.ParseIncoming(s))
						this.stream.BeginRead(this.buffer, 0, BufferSize, this.EndRead, null);
				}
			}
			catch (NullReferenceException)
			{
				// Client closed.
			}
			catch (Exception ex)
			{
				this.ConnectionError(ex);
			}
		}
#endif

		private bool ParseIncoming(string s)
		{
			bool Result = true;

			foreach (char ch in s)
			{
				switch (this.inputState)
				{
					case 0:     // Waiting for <?
						if (ch == '<')
						{
							this.fragment.Append(ch);
							this.inputState++;
						}
						else if (ch > ' ')
						{
							this.ToError();
							return false;
						}
						break;

					case 1:     // Waiting for ? or >
						this.fragment.Append(ch);
						if (ch == '?')
							this.inputState++;
						else if (ch == '>')
						{
							this.inputState = 5;
							this.inputDepth = 1;
							this.ProcessStream(this.fragment.ToString());
							this.fragment.Clear();
						}
						break;

					case 2:     // Waiting for ?>
						this.fragment.Append(ch);
						if (ch == '>')
							this.inputState++;
						break;

					case 3:     // Waiting for <stream
						this.fragment.Append(ch);
						if (ch == '<')
							this.inputState++;
						else if (ch > ' ')
						{
							this.ToError();
							return false;
						}
						break;

					case 4:     // Waiting for >
						this.fragment.Append(ch);
						if (ch == '>')
						{
							this.inputState++;
							this.inputDepth = 1;
							this.ProcessStream(this.fragment.ToString());
							this.fragment.Clear();
						}
						break;

					case 5: // Waiting for <
						if (ch == '<')
						{
							this.fragment.Append(ch);
							this.inputState++;
						}

						else if (this.inputDepth > 1)
							this.fragment.Append(ch);
						else if (ch > ' ')
						{
							this.ToError();
							return false;
						}
						break;

					case 6: // Second character in tag
						this.fragment.Append(ch);
						if (ch == '/')
							this.inputState++;
						else
							this.inputState += 2;
						break;

					case 7: // Waiting for end of closing tag
						this.fragment.Append(ch);
						if (ch == '>')
						{
							this.inputDepth--;
							if (this.inputDepth < 1)
							{
								this.ToError();
								return false;
							}
							else
							{
								if (this.inputDepth == 1)
								{
									if (!this.ProcessFragment(this.fragment.ToString()))
										Result = false;

									this.fragment.Clear();
								}

								if (this.inputState > 0)
									this.inputState = 5;
							}
						}
						break;

					case 8: // Wait for end of start tag
						this.fragment.Append(ch);
						if (ch == '>')
						{
							this.inputDepth++;
							this.inputState = 5;
						}
						else if (ch == '/')
							this.inputState++;
						break;

					case 9: // Check for end of childless tag.
						this.fragment.Append(ch);
						if (ch == '>')
						{
							if (this.inputDepth == 1)
							{
								if (!this.ProcessFragment(this.fragment.ToString()))
									Result = false;

								this.fragment.Clear();
							}

							if (this.inputState != 0)
								this.inputState = 5;
						}
						else
							this.inputState--;
						break;

					default:
						break;
				}
			}

			return Result;
		}

		private void ToError()
		{
			this.inputState = -1;
#if WINDOWS_UWP
			if (this.dataWriter != null)
			{
				this.dataWriter.Dispose();
				this.dataWriter = null;

				this.dataReader.Dispose();
				this.dataReader = null;

				this.client.Dispose();
				this.client = null;
			}
#else
			if (this.stream != null)
			{
				this.stream.Dispose();
				this.stream = null;

				this.client.Close();
				this.client = null;
			}
#endif
			this.State = XmppState.Error;
		}

		private void ProcessStream(string Xml)
		{
			try
			{
				int i = Xml.IndexOf("?>");
				if (i >= 0)
					Xml = Xml.Substring(i + 2).TrimStart();

				this.streamHeader = Xml;

				i = Xml.IndexOf(":stream");
				if (i < 0)
					this.streamFooter = "</stream>";
				else
					this.streamFooter = "</" + Xml.Substring(1, i - 1) + ":stream>";

				XmlDocument Doc = new XmlDocument();
				Doc.LoadXml(Xml + this.streamFooter);

				if (Doc.DocumentElement.LocalName != "stream")
					throw new XmppException("Invalid stream.", Doc.DocumentElement);

				XmlElement Stream = Doc.DocumentElement;

				this.streamId = XML.Attribute(Stream, "id");
				string From = XML.Attribute(Stream, "from");

				if (From != this.componentSubDomain)
					this.ConnectionError(new System.Exception("Invalid component address."));
				else
				{
					string s = this.streamId + this.sharedSecret;
					byte[] Data = System.Text.Encoding.UTF8.GetBytes(s);
					byte[] Result;

#if WINDOWS_UWP
					HashAlgorithmProvider Provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha1);
					CryptographicHash Hash = Provider.CreateHash();

					Hash.Append(CryptographicBuffer.CreateFromByteArray(Data));

					CryptographicBuffer.CopyToByteArray(Hash.GetValueAndReset(), out Result);
#else
					using (SHA1 SHA1 = SHA1.Create())
					{
						Result = SHA1.ComputeHash(Data);
					}
#endif
					StringBuilder Response = new StringBuilder();

					Response.Append("<handshake>");

					foreach (byte b in Result)
						Response.Append(b.ToString("x2"));

					Response.Append("</handshake>");

					this.BeginWrite(Response.ToString(), null);
				}
			}
			catch (Exception ex)
			{
				this.ConnectionError(ex);
			}
		}

		private bool ProcessFragment(string Xml)
		{
			XmlDocument Doc;
			XmlElement E;

			try
			{
				Doc = new XmlDocument();
				Doc.LoadXml(this.streamHeader + Xml + this.streamFooter);

				foreach (XmlNode N in Doc.DocumentElement.ChildNodes)
				{
					E = N as XmlElement;
					if (E == null)
						continue;

					switch (E.LocalName)
					{
						case "iq":
							string Type = XML.Attribute(E, "type");
							string Id = XML.Attribute(E, "id");
							string To = XML.Attribute(E, "to");
							string From = XML.Attribute(E, "from");
							switch (Type)
							{
								case "get":
									this.ProcessIq(this.iqGetHandlers, new IqEventArgs(this, E, Id, To, From));
									break;

								case "set":
									this.ProcessIq(this.iqSetHandlers, new IqEventArgs(this, E, Id, To, From));
									break;

								case "result":
								case "error":
									uint SeqNr;
									IqResultEventHandler Callback;
									object State;
									PendingRequest Rec;
									bool Ok = (Type == "result");

									if (uint.TryParse(Id, out SeqNr))
									{
										lock (this.synchObject)
										{
											if (this.pendingRequestsBySeqNr.TryGetValue(SeqNr, out Rec))
											{
												Callback = Rec.Callback;
												State = Rec.State;

												this.pendingRequestsBySeqNr.Remove(SeqNr);
												this.pendingRequestsByTimeout.Remove(Rec.Timeout);
											}
											else
											{
												Callback = null;
												State = null;
											}
										}

										if (Callback != null)
										{
											try
											{
												Callback(this, new IqResultEventArgs(E, Id, To, From, Ok, State));
											}
											catch (Exception ex)
											{
												Exception(ex);
											}
										}
									}
									break;
							}
							break;

						case "message":
							this.ProcessMessage(new MessageEventArgs(this, E));
							break;

						case "presence":
							this.ProcessPresence(new PresenceEventArgs(this, E));
							break;

						case "error":
							XmppException StreamException = XmppClient.GetStreamExceptionObject(E);
							if (StreamException is SeeOtherHostException)
							{
								this.host = ((SeeOtherHostException)StreamException).NewHost;
								this.inputState = -1;

								this.Information("Reconnecting to " + this.host);

								this.DisposeClient();
								this.Connect();
								return false;
							}
							else
								throw StreamException;

						case "handshake":
							this.State = XmppState.Connected;
							break;

						default:
							break;
					}
				}
			}
			catch (Exception ex)
			{
				this.ConnectionError(ex);
				return false;
			}

			return true;
		}

		private void ProcessMessage(MessageEventArgs e)
		{
			MessageEventHandler h = null;
			string Key;

			lock (this.synchObject)
			{
				foreach (XmlElement E in e.Message.ChildNodes)
				{
					Key = E.LocalName + " " + E.NamespaceURI;
					if (this.messageHandlers.TryGetValue(Key, out h))
					{
						e.Content = E;
						break;
					}
					else
						h = null;
				}
			}

			if (h != null)
#if WINDOWS_UWP
				this.Information(h.GetMethodInfo().Name);
#else
				this.Information(h.Method.Name);
#endif
			else
			{
				switch (e.Type)
				{
					case MessageType.Chat:
						this.Information("OnChatMessage()");
						h = this.OnChatMessage;
						break;

					case MessageType.Error:
						this.Information("OnErrorMessage()");
						h = this.OnErrorMessage;
						break;

					case MessageType.GroupChat:
						this.Information("OnGroupChatMessage()");
						h = this.OnGroupChatMessage;
						break;

					case MessageType.Headline:
						this.Information("OnHeadlineMessage()");
						h = this.OnHeadlineMessage;
						break;

					case MessageType.Normal:
					default:
						this.Information("OnNormalMessage()");
						h = this.OnNormalMessage;
						break;
				}
			}

			if (h != null)
			{
				try
				{
					h(this, e);
				}
				catch (Exception ex)
				{
					this.Exception(ex);
				}
			}
		}

		private void ProcessPresence(PresenceEventArgs e)
		{
			PresenceEventHandler h;

			switch (e.Type)
			{
				case PresenceType.Available:
					this.Information("OnPresence()");
					h = this.OnPresence;
					break;

				case PresenceType.Unavailable:
					this.Information("OnPresence()");
					h = this.OnPresence;
					break;

				case PresenceType.Error:
				case PresenceType.Probe:
				default:
					this.Information("OnPresence()");
					h = this.OnPresence;
					break;

				case PresenceType.Subscribe:
					this.Information("OnPresenceSubscribe()");
					h = this.OnPresenceSubscribe;
					break;

				case PresenceType.Subscribed:
					this.Information("OnPresenceSubscribed()");
					h = this.OnPresenceSubscribed;
					break;

				case PresenceType.Unsubscribe:
					this.Information("OnPresenceUnsubscribe()");
					h = this.OnPresenceUnsubscribe;
					break;

				case PresenceType.Unsubscribed:
					this.Information("OnPresenceUnsubscribed()");
					h = this.OnPresenceUnsubscribed;
					break;
			}

			if (h != null)
			{
				try
				{
					h(this, e);
				}
				catch (Exception ex)
				{
					this.Exception(ex);
				}
			}
		}

		private void ProcessIq(Dictionary<string, IqEventHandler> Handlers, IqEventArgs e)
		{
			IqEventHandler h = null;
			string Key;

			lock (this.synchObject)
			{
				foreach (XmlElement E in e.IQ.ChildNodes)
				{
					Key = E.LocalName + " " + E.NamespaceURI;
					if (Handlers.TryGetValue(Key, out h))
					{
						e.Query = E;
						break;
					}
					else
						h = null;
				}
			}

			if (h == null)
				this.SendIqError(e.Id, e.To, e.From, "<error type='cancel'><feature-not-implemented xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error>");
			else
			{
				try
				{
					h(this, e);
				}
				catch (StanzaExceptionException ex)
				{
					StringBuilder Xml = new StringBuilder();

					this.Error(ex.Message);

					Xml.Append("<error type='");
					Xml.Append(ex.ErrorType);
					Xml.Append("'><");
					Xml.Append(ex.ErrorStanzaName);
					Xml.Append(" xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>");
					Xml.Append("<text>");
					Xml.Append(XML.Encode(ex.Message));
					Xml.Append("</text>");
					Xml.Append("</error>");

					this.SendIqError(e.Id, e.To, e.From, Xml.ToString());
				}
				catch (Exception ex)
				{
					StringBuilder Xml = new StringBuilder();

					this.Exception(ex);

					Xml.Append("<error type='cancel'><internal-server-error xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>");
					Xml.Append("<text>");
					Xml.Append(XML.Encode(ex.Message));
					Xml.Append("</text>");
					Xml.Append("</error>");

					this.SendIqError(e.Id, e.To, e.From, Xml.ToString());
				}
			}
		}

		/// <summary>
		/// Registers an IQ-Get handler.
		/// </summary>
		/// <param name="LocalName">Local Name</param>
		/// <param name="Namespace">Namespace</param>
		/// <param name="Handler">Handler to process request.</param>
		/// <param name="PublishNamespaceAsClientFeature">If the namespace should be published as a client feature.</param>
		public void RegisterIqGetHandler(string LocalName, string Namespace, IqEventHandler Handler, bool PublishNamespaceAsClientFeature)
		{
			this.RegisterIqHandler(this.iqGetHandlers, LocalName, Namespace, Handler, PublishNamespaceAsClientFeature);
		}

		/// <summary>
		/// Registers an IQ-Set handler.
		/// </summary>
		/// <param name="LocalName">Local Name</param>
		/// <param name="Namespace">Namespace</param>
		/// <param name="Handler">Handler to process request.</param>
		/// <param name="PublishNamespaceAsClientFeature">If the namespace should be published as a client feature.</param>
		public void RegisterIqSetHandler(string LocalName, string Namespace, IqEventHandler Handler, bool PublishNamespaceAsClientFeature)
		{
			this.RegisterIqHandler(this.iqSetHandlers, LocalName, Namespace, Handler, PublishNamespaceAsClientFeature);
		}

		private void RegisterIqHandler(Dictionary<string, IqEventHandler> Handlers, string LocalName, string Namespace, IqEventHandler Handler,
			bool PublishNamespaceAsClientFeature)
		{
			string Key = LocalName + " " + Namespace;

			lock (this.synchObject)
			{
				if (Handlers.ContainsKey(Key))
					throw new ArgumentException("Handler already registered.", "LocalName");

				Handlers[Key] = Handler;

				if (PublishNamespaceAsClientFeature)
					this.clientFeatures[Namespace] = true;
			}
		}

		/// <summary>
		/// Unregisters an IQ-Get handler.
		/// </summary>
		/// <param name="LocalName">Local Name</param>
		/// <param name="Namespace">Namespace</param>
		/// <param name="Handler">Handler to process request.</param>
		/// <param name="RemoveNamespaceAsClientFeature">If the namespace should be removed from the lit of client features.</param>
		/// <returns>If the handler was found and removed.</returns>
		public bool UnregisterIqGetHandler(string LocalName, string Namespace, IqEventHandler Handler, bool RemoveNamespaceAsClientFeature)
		{
			return this.UnregisterIqHandler(this.iqGetHandlers, LocalName, Namespace, Handler, RemoveNamespaceAsClientFeature);
		}

		/// <summary>
		/// Unregisters an IQ-Set handler.
		/// </summary>
		/// <param name="LocalName">Local Name</param>
		/// <param name="Namespace">Namespace</param>
		/// <param name="Handler">Handler to process request.</param>
		/// <param name="RemoveNamespaceAsClientFeature">If the namespace should be removed from the lit of client features.</param>
		/// <returns>If the handler was found and removed.</returns>
		public bool UnregisterIqSetHandler(string LocalName, string Namespace, IqEventHandler Handler, bool RemoveNamespaceAsClientFeature)
		{
			return this.UnregisterIqHandler(this.iqSetHandlers, LocalName, Namespace, Handler, RemoveNamespaceAsClientFeature);
		}

		private bool UnregisterIqHandler(Dictionary<string, IqEventHandler> Handlers, string LocalName, string Namespace, IqEventHandler Handler,
			bool RemoveNamespaceAsClientFeature)
		{
			IqEventHandler h;
			string Key = LocalName + " " + Namespace;

			lock (this.synchObject)
			{
				if (!Handlers.TryGetValue(Key, out h))
					return false;

				if (h != Handler)
					return false;

				Handlers.Remove(Key);

				if (RemoveNamespaceAsClientFeature)
					this.clientFeatures.Remove(Namespace);
			}

			return true;
		}

		/// <summary>
		/// Registers a Message handler.
		/// </summary>
		/// <param name="LocalName">Local Name</param>
		/// <param name="Namespace">Namespace</param>
		/// <param name="Handler">Handler to process message.</param>
		/// <param name="PublishNamespaceAsClientFeature">If the namespace should be published as a client feature.</param>
		public void RegisterMessageHandler(string LocalName, string Namespace, MessageEventHandler Handler, bool PublishNamespaceAsClientFeature)
		{
			string Key = LocalName + " " + Namespace;

			lock (this.synchObject)
			{
				if (this.messageHandlers.ContainsKey(Key))
					throw new ArgumentException("Handler already registered.", "LocalName");

				this.messageHandlers[Key] = Handler;

				if (PublishNamespaceAsClientFeature)
					this.clientFeatures[Namespace] = true;
			}
		}

		/// <summary>
		/// Unregisters a Message handler.
		/// </summary>
		/// <param name="LocalName">Local Name</param>
		/// <param name="Namespace">Namespace</param>
		/// <param name="Handler">Handler to remove.</param>
		/// <param name="RemoveNamespaceAsClientFeature">If the namespace should be removed from the lit of client features.</param>
		/// <returns>If the handler was found and removed.</returns>
		public bool UnregisterMessageHandler(string LocalName, string Namespace, MessageEventHandler Handler, bool RemoveNamespaceAsClientFeature)
		{
			MessageEventHandler h;
			string Key = LocalName + " " + Namespace;

			lock (this.synchObject)
			{
				if (!this.messageHandlers.TryGetValue(Key, out h))
					return false;

				if (h != Handler)
					return false;

				this.messageHandlers.Remove(Key);

				if (RemoveNamespaceAsClientFeature)
					this.clientFeatures.Remove(Namespace);
			}

			return true;
		}

		/// <summary>
		/// Registers a feature on the client.
		/// </summary>
		/// <param name="Feature">Feature to register.</param>
		public void RegisterFeature(string Feature)
		{
			lock (this.synchObject)
			{
				this.clientFeatures[Feature] = true;
			}
		}

		/// <summary>
		/// Unregisters a feature from the client.
		/// </summary>
		/// <param name="Feature">Feature to remove.</param>
		/// <returns>If the feature was found and removed.</returns>
		public bool UnregisterFeature(string Feature)
		{
			lock (this.synchObject)
			{
				return this.clientFeatures.Remove(Feature);
			}
		}

		/// <summary>
		/// Event raised when a presence message has been received from a resource.
		/// </summary>
		public event PresenceEventHandler OnPresence = null;

		/// <summary>
		/// Event raised when a resource is requesting to be informed of the current client's presence
		/// </summary>
		public event PresenceEventHandler OnPresenceSubscribe = null;

		/// <summary>
		/// Event raised when your presence subscription has been accepted.
		/// </summary>
		public event PresenceEventHandler OnPresenceSubscribed = null;

		/// <summary>
		/// Event raised when a resource is requesting to be removed from the current client's presence
		/// </summary>
		public event PresenceEventHandler OnPresenceUnsubscribe = null;

		/// <summary>
		/// Event raised when your presence unsubscription has been accepted.
		/// </summary>
		public event PresenceEventHandler OnPresenceUnsubscribed = null;

		/// <summary>
		/// Raised when a chat message has been received, that is not handled by a specific message handler.
		/// </summary>
		public event MessageEventHandler OnChatMessage = null;

		/// <summary>
		/// Raised when an error message has been received, that is not handled by a specific message handler.
		/// </summary>
		public event MessageEventHandler OnErrorMessage = null;

		/// <summary>
		/// Raised when a group chat message has been received, that is not handled by a specific message handler.
		/// </summary>
		public event MessageEventHandler OnGroupChatMessage = null;

		/// <summary>
		/// Raised when a headline message has been received, that is not handled by a specific message handler.
		/// </summary>
		public event MessageEventHandler OnHeadlineMessage = null;

		/// <summary>
		/// Raised when a normal message has been received, that is not handled by a specific message handler.
		/// </summary>
		public event MessageEventHandler OnNormalMessage = null;

		/// <summary>
		/// Component sub-domain on server.
		/// </summary>
		public string ComponentSubDomain
		{
			get { return this.componentSubDomain; }
		}

		internal string SharedSecret
		{
			get { return this.sharedSecret; }
		}

		/// <summary>
		/// Sends an IQ Get request.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the request.</param>
		/// <param name="Callback">Callback method to call when response is returned.</param>
		/// <param name="State">State object to pass on to the callback method.</param>
		/// <returns>ID of IQ stanza</returns>
		public uint SendIqGet(string From, string To, string Xml, IqResultEventHandler Callback, object State)
		{
			return this.SendIq(null, From, To, Xml, "get", Callback, State, this.defaultRetryTimeout, this.defaultNrRetries, this.defaultDropOff,
				this.defaultMaxRetryTimeout);
		}

		/// <summary>
		/// Sends an IQ Get request.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the request.</param>
		/// <param name="Callback">Callback method to call when response is returned.</param>
		/// <param name="State">State object to pass on to the callback method.</param>
		/// <param name="RetryTimeout">Retry Timeout, in milliseconds.</param>
		/// <param name="NrRetries">Number of retries.</param>
		/// <returns>ID of IQ stanza</returns>
		public uint SendIqGet(string From, string To, string Xml, IqResultEventHandler Callback, object State, int RetryTimeout, int NrRetries)
		{
			return this.SendIq(null, From, To, Xml, "get", Callback, State, RetryTimeout, NrRetries, false, RetryTimeout);
		}

		/// <summary>
		/// Sends an IQ Get request.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the request.</param>
		/// <param name="Callback">Callback method to call when response is returned.</param>
		/// <param name="State">State object to pass on to the callback method.</param>
		/// <param name="RetryTimeout">Retry Timeout, in milliseconds.</param>
		/// <param name="NrRetries">Number of retries.</param>
		/// <param name="DropOff">If the retry timeout should be doubled between retries (true), or if the same retry timeout 
		/// should be used for all retries. The retry timeout will never exceed <paramref name="MaxRetryTieout"/>.</param>
		/// <param name="MaxRetryTimeout">Maximum retry timeout. Used if <see cref="DropOff"/> is true.</param>
		/// <returns>ID of IQ stanza</returns>
		public uint SendIqGet(string From, string To, string Xml, IqResultEventHandler Callback, object State,
			int RetryTimeout, int NrRetries, bool DropOff, int MaxRetryTimeout)
		{
			return this.SendIq(null, From, To, Xml, "get", Callback, State, RetryTimeout, NrRetries, DropOff, MaxRetryTimeout);
		}

		/// <summary>
		/// Sends an IQ Set request.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the request.</param>
		/// <param name="Callback">Callback method to call when response is returned.</param>
		/// <param name="State">State object to pass on to the callback method.</param>
		/// <returns>ID of IQ stanza</returns>
		public uint SendIqSet(string From, string To, string Xml, IqResultEventHandler Callback, object State)
		{
			return this.SendIq(null, From, To, Xml, "set", Callback, State, this.defaultRetryTimeout, this.defaultNrRetries, this.defaultDropOff,
				this.defaultMaxRetryTimeout);
		}

		/// <summary>
		/// Sends an IQ Set request.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the request.</param>
		/// <param name="Callback">Callback method to call when response is returned.</param>
		/// <param name="State">State object to pass on to the callback method.</param>
		/// <param name="RetryTimeout">Retry Timeout, in milliseconds.</param>
		/// <param name="NrRetries">Number of retries.</param>
		/// <returns>ID of IQ stanza</returns>
		public uint SendIqSet(string From, string To, string Xml, IqResultEventHandler Callback, object State, int RetryTimeout, int NrRetries)
		{
			return this.SendIq(null, From, To, Xml, "set", Callback, State, RetryTimeout, NrRetries, false, RetryTimeout);
		}

		/// <summary>
		/// Sends an IQ Set request.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the request.</param>
		/// <param name="Callback">Callback method to call when response is returned.</param>
		/// <param name="State">State object to pass on to the callback method.</param>
		/// <param name="RetryTimeout">Retry Timeout, in milliseconds.</param>
		/// <param name="NrRetries">Number of retries.</param>
		/// <param name="DropOff">If the retry timeout should be doubled between retries (true), or if the same retry timeout 
		/// should be used for all retries. The retry timeout will never exceed <paramref name="MaxRetryTieout"/>.</param>
		/// <param name="MaxRetryTimeout">Maximum retry timeout. Used if <see cref="DropOff"/> is true.</param>
		/// <returns>ID of IQ stanza</returns>
		public uint SendIqSet(string From, string To, string Xml, IqResultEventHandler Callback, object State,
			int RetryTimeout, int NrRetries, bool DropOff, int MaxRetryTimeout)
		{
			return this.SendIq(null, From, To, Xml, "set", Callback, State, RetryTimeout, NrRetries, DropOff, MaxRetryTimeout);
		}

		/// <summary>
		/// Returns a response to an IQ Get/Set request.
		/// </summary>
		/// <param name="Id">ID attribute of original IQ request.</param>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the response.</param>
		public void SendIqResult(string Id, string From, string To, string Xml)
		{
			this.SendIq(Id, From, To, Xml, "result", null, null, 0, 0, false, 0);
		}

		/// <summary>
		/// Returns an error response to an IQ Get/Set request.
		/// </summary>
		/// <param name="Id">ID attribute of original IQ request.</param>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the response.</param>
		public void SendIqError(string Id, string From, string To, string Xml)
		{
			this.SendIq(Id, From, To, Xml, "error", null, null, 0, 0, false, 0);
		}

		private uint SendIq(string Id, string From, string To, string Xml, string Type, IqResultEventHandler Callback, object State,
			int RetryTimeout, int NrRetries, bool DropOff, int MaxRetryTimeout)
		{
			PendingRequest PendingRequest = null;
			DateTime TP;
			uint SeqNr;

			if (string.IsNullOrEmpty(Id))
			{
				lock (this.synchObject)
				{
					SeqNr = this.seqnr++;
					PendingRequest = new PendingRequest(SeqNr, Callback, State, RetryTimeout, NrRetries, DropOff, MaxRetryTimeout, To);
					TP = PendingRequest.Timeout;

					while (this.pendingRequestsByTimeout.ContainsKey(TP))
						TP = TP.AddTicks(this.gen.Next(1, 10));

					PendingRequest.Timeout = TP;

					this.pendingRequestsBySeqNr[SeqNr] = PendingRequest;
					this.pendingRequestsByTimeout[TP] = PendingRequest;

					Id = SeqNr.ToString();
				}
			}
			else
				SeqNr = 0;

			StringBuilder XmlOutput = new StringBuilder();

			XmlOutput.Append("<iq type='");
			XmlOutput.Append(Type);
			XmlOutput.Append("' id='");
			XmlOutput.Append(Id);

			if (!string.IsNullOrEmpty(From))
			{
				XmlOutput.Append("' from='");
				XmlOutput.Append(XML.Encode(From));
			}

			if (!string.IsNullOrEmpty(To))
			{
				XmlOutput.Append("' to='");
				XmlOutput.Append(XML.Encode(To));
			}

			XmlOutput.Append("'>");
			XmlOutput.Append(Xml);
			XmlOutput.Append("</iq>");

			string IqXml = XmlOutput.ToString();
			if (PendingRequest != null)
				PendingRequest.Xml = IqXml;

			this.BeginWrite(IqXml, null);

			return SeqNr;
		}

		/// <summary>
		/// Performs a synchronous IQ Get request/response operation.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the request.</param>
		/// <param name="Timeout">Timeout in milliseconds.</param>
		/// <returns>Response XML element.</returns>
		/// <exception cref="TimeoutException">If a timeout occurred.</exception>
		/// <exception cref="XmppException">If an IQ error is returned.</exception>
		public XmlElement IqGet(string From, string To, string Xml, int Timeout)
		{
			ManualResetEvent Done = new ManualResetEvent(false);
			IqResultEventArgs e = null;

			try
			{
				this.SendIqGet(From, To, Xml, (sender, e2) =>
				{
					e = e2;
					Done.Set();
				}, null);

				if (!Done.WaitOne(Timeout))
					throw new TimeoutException();
			}
			finally
			{
				Done.Dispose();
			}

			if (!e.Ok)
				throw e.StanzaError;

			return e.Response;
		}

		/// <summary>
		/// Performs a synchronous IQ Set request/response operation.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Xml">XML to embed into the request.</param>
		/// <param name="Timeout">Timeout in milliseconds.</param>
		/// <returns>Response XML element.</returns>
		/// <exception cref="TimeoutException">If a timeout occurred.</exception>
		/// <exception cref="XmppException">If an IQ error is returned.</exception>
		public XmlElement IqSet(string From, string To, string Xml, int Timeout)
		{
			ManualResetEvent Done = new ManualResetEvent(false);
			IqResultEventArgs e = null;

			try
			{
				this.SendIqSet(From, To, Xml, (sender, e2) =>
				{
					e = e2;
					Done.Set();
				}, null);

				if (!Done.WaitOne(Timeout))
					throw new TimeoutException();
			}
			finally
			{
				Done.Dispose();
			}

			if (!e.Ok)
				throw e.StanzaError;

			return e.Response;
		}

		/// <summary>
		/// Sets the presence of the connection.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		public void SetPresence(string From)
		{
			this.SetPresence(From, Availability.Online, string.Empty, null);
		}

		/// <summary>
		/// Sets the presence of the connection.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="Availability">Client availability.</param>
		public void SetPresence(string From, Availability Availability)
		{
			this.SetPresence(From, Availability, string.Empty, null);
		}

		/// <summary>
		/// Sets the presence of the connection.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="Availability">Client availability.</param>
		/// <param name="CustomXml">Custom XML.</param>
		public void SetPresence(string From, Availability Availability, string CustomXml)
		{
			this.SetPresence(From, Availability, CustomXml, null);
		}

		/// <summary>
		/// Sets the presence of the connection.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="Availability">Client availability.</param>
		/// <param name="CustomXml">Custom XML.</param>
		/// <param name="Status">Custom Status message, defined as a set of (language,text) pairs.</param>
		public void SetPresence(string From, Availability Availability, string CustomXml, params KeyValuePair<string, string>[] Status)
		{
			if (this.state == XmppState.Connected || this.state == XmppState.SettingPresence)
			{
				StringBuilder Xml = new StringBuilder("<presence");

				if (!string.IsNullOrEmpty(From))
				{
					Xml.Append(" from='");
					Xml.Append(XML.Encode(From));
					Xml.Append("'");
				}

				switch (Availability)
				{
					case XMPP.Availability.Online:
					default:
						Xml.Append(">");
						break;

					case XMPP.Availability.Away:
						Xml.Append("><show>away</show>");
						break;

					case XMPP.Availability.Chat:
						Xml.Append("><show>chat</show>");
						break;

					case XMPP.Availability.DoNotDisturb:
						Xml.Append("><show>dnd</show>");
						break;

					case XMPP.Availability.ExtendedAway:
						Xml.Append("><show>xa</show>");
						break;

					case XMPP.Availability.Offline:
						Xml.Append(" type='unavailable'>");
						break;
				}

				if (Status != null)
				{
					foreach (KeyValuePair<string, string> P in Status)
					{
						Xml.Append("<status");

						if (!string.IsNullOrEmpty(P.Key))
						{
							Xml.Append(" xml:lang='");
							Xml.Append(XML.Encode(P.Key));
							Xml.Append("'>");
						}
						else
							Xml.Append('>');

						Xml.Append(XML.Encode(P.Value));
						Xml.Append("</status>");
					}
				}

				if (!string.IsNullOrEmpty(CustomXml))
					Xml.Append(CustomXml);

				Xml.Append("</presence>");

				this.BeginWrite(Xml.ToString(), null);
			}
		}

		/// <summary>
		/// Requests subscription of presence information from a contact.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="BareJid">Bare JID of contact.</param>
		public void RequestPresenceSubscription(string From, string BareJid)
		{
			StringBuilder Xml = new StringBuilder();
			uint SeqNr;

			lock (this.synchObject)
			{
				SeqNr = this.seqnr++;
			}

			Xml.Append("<presence id='");
			Xml.Append(SeqNr.ToString());
			Xml.Append("' from='");
			Xml.Append(XML.Encode(From));
			Xml.Append("' to='");
			Xml.Append(XML.Encode(BareJid));
			Xml.Append("' type='subscribe'/>");

			this.BeginWrite(Xml.ToString(), null);
		}

		/// <summary>
		/// Requests unssubscription of presence information from a contact.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="BareJid">Bare JID of contact.</param>
		public void RequestPresenceUnsubscription(string From, string BareJid)
		{
			StringBuilder Xml = new StringBuilder();
			uint SeqNr;

			lock (this.synchObject)
			{
				SeqNr = this.seqnr++;
			}

			Xml.Append("<presence id='");
			Xml.Append(SeqNr.ToString());
			Xml.Append("' from='");
			Xml.Append(XML.Encode(From));
			Xml.Append("' to='");
			Xml.Append(XML.Encode(BareJid));
			Xml.Append("' type='unsubscribe'/>");

			this.BeginWrite(Xml.ToString(), null);
		}

		internal void PresenceSubscriptionAccepted(string Id, string From, string BareJid)
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<presence id='");
			Xml.Append(XML.Encode(Id));
			Xml.Append("' from='");
			Xml.Append(XML.Encode(From));
			Xml.Append("' to='");
			Xml.Append(XML.Encode(BareJid));
			Xml.Append("' type='subscribed'/>");

			this.BeginWrite(Xml.ToString(), null);
		}

		internal void PresenceSubscriptionDeclined(string Id, string From, string BareJid)
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<presence id='");
			Xml.Append(XML.Encode(Id));
			Xml.Append("' from='");
			Xml.Append(XML.Encode(From));
			Xml.Append("' to='");
			Xml.Append(XML.Encode(BareJid));
			Xml.Append("' type='unsubscribed'/>");

			this.BeginWrite(Xml.ToString(), null);
		}

		internal void PresenceUnsubscriptionAccepted(string Id, string From, string BareJid)
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<presence id='");
			Xml.Append(XML.Encode(Id));
			Xml.Append("' from='");
			Xml.Append(XML.Encode(From));
			Xml.Append("' to='");
			Xml.Append(XML.Encode(BareJid));
			Xml.Append("' type='unsubscribed'/>");

			this.BeginWrite(Xml.ToString(), null);
		}

		internal void PresenceUnsubscriptionDeclined(string Id, string From, string BareJid)
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<presence id='");
			Xml.Append(XML.Encode(Id));
			Xml.Append("' from='");
			Xml.Append(XML.Encode(From));
			Xml.Append("' to='");
			Xml.Append(XML.Encode(BareJid));
			Xml.Append("' type='subscribed'/>");

			this.BeginWrite(Xml.ToString(), null);
		}

		/// <summary>
		/// Sends a simple chat message
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Body">Body text of chat message.</param>
		public void SendChatMessage(string From, string To, string Body)
		{
			this.SendMessage(QoSLevel.Unacknowledged, MessageType.Chat, From, To, string.Empty, Body, string.Empty, string.Empty, string.Empty, string.Empty,
				null, null);
		}

		/// <summary>
		/// Sends a simple chat message
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Body">Body text of chat message.</param>
		/// <param name="Subject">Subject</param>
		public void SendChatMessage(string From, string To, string Body, string Subject)
		{
			this.SendMessage(QoSLevel.Unacknowledged, MessageType.Chat, From, To, string.Empty, Body, Subject, string.Empty, string.Empty, string.Empty, null, null);
		}

		/// <summary>
		/// Sends a simple chat message
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Body">Body text of chat message.</param>
		/// <param name="Subject">Subject</param>
		/// <param name="Language">Language used.</param>
		public void SendChatMessage(string From, string To, string Body, string Subject, string Language)
		{
			this.SendMessage(QoSLevel.Unacknowledged, MessageType.Chat, From, To, string.Empty, Body, Subject, Language, string.Empty, string.Empty, null, null);
		}

		/// <summary>
		/// Sends a simple chat message
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Body">Body text of chat message.</param>
		/// <param name="Subject">Subject</param>
		/// <param name="Language">Language used.</param>
		/// <param name="ThreadId">Thread ID</param>
		public void SendChatMessage(string From, string To, string Body, string Subject, string Language, string ThreadId)
		{
			this.SendMessage(QoSLevel.Unacknowledged, MessageType.Chat, From, To, string.Empty, Body, Subject, Language, ThreadId, string.Empty, null, null);
		}

		/// <summary>
		/// Sends a simple chat message
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="Body">Body text of chat message.</param>
		/// <param name="Subject">Subject</param>
		/// <param name="Language">Language used.</param>
		/// <param name="ThreadId">Thread ID</param>
		/// <param name="ParentThreadId">Parent Thread ID</param>
		public void SendChatMessage(string From, string To, string Body, string Subject, string Language, string ThreadId, string ParentThreadId)
		{
			this.SendMessage(QoSLevel.Unacknowledged, MessageType.Chat, From, To, string.Empty, Body, Subject, Language, ThreadId, ParentThreadId, null, null);
		}

		/// <summary>
		/// Sends a simple chat message
		/// </summary>
		/// <param name="Type">Type of message to send.</param>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="CustomXml">Custom XML</param>
		/// <param name="Body">Body text of chat message.</param>
		/// <param name="Subject">Subject</param>
		/// <param name="Language">Language used.</param>
		/// <param name="ThreadId">Thread ID</param>
		/// <param name="ParentThreadId">Parent Thread ID</param>
		public void SendMessage(MessageType Type, string From, string To, string CustomXml, string Body, string Subject, string Language, string ThreadId,
			string ParentThreadId)
		{
			this.SendMessage(QoSLevel.Unacknowledged, Type, From, To, CustomXml, Body, Subject, Language, ThreadId, ParentThreadId, null, null);
		}

		/// <summary>
		/// Sends a simple chat message
		/// </summary>
		/// <param name="QoS">Quality of Service level of message.</param>
		/// <param name="Type">Type of message to send.</param>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address</param>
		/// <param name="CustomXml">Custom XML</param>
		/// <param name="Body">Body text of chat message.</param>
		/// <param name="Subject">Subject</param>
		/// <param name="Language">Language used.</param>
		/// <param name="ThreadId">Thread ID</param>
		/// <param name="ParentThreadId">Parent Thread ID</param>
		/// <param name="DeliveryCallback">Callback to call when message has been sent, or failed to be sent.</param>
		/// <param name="State">State object to pass on to the callback method.</param>
		public void SendMessage(QoSLevel QoS, MessageType Type, string From, string To, string CustomXml, string Body, string Subject, string Language, string ThreadId,
			string ParentThreadId, DeliveryEventHandler DeliveryCallback, object State)
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<message");

			switch (Type)
			{
				case MessageType.Chat:
					Xml.Append(" type='chat'");
					break;

				case MessageType.Error:
					Xml.Append(" type='error'");
					break;

				case MessageType.GroupChat:
					Xml.Append(" type='groupchat'");
					break;

				case MessageType.Headline:
					Xml.Append(" type='headline'");
					break;
			}

			if (QoS == QoSLevel.Unacknowledged)
			{
				Xml.Append(" from='");
				Xml.Append(XML.Encode(From));
				Xml.Append("' to='");
				Xml.Append(XML.Encode(To));
				Xml.Append('\'');
			}

			if (!string.IsNullOrEmpty(Language))
			{
				Xml.Append(" xml:lang='");
				Xml.Append(XML.Encode(Language));
				Xml.Append('\'');
			}

			Xml.Append('>');

			if (!string.IsNullOrEmpty(Subject))
			{
				Xml.Append("<subject>");
				Xml.Append(XML.Encode(Subject));
				Xml.Append("</subject>");
			}

			Xml.Append("<body>");
			Xml.Append(XML.Encode(Body));
			Xml.Append("</body>");

			if (!string.IsNullOrEmpty(ThreadId))
			{
				Xml.Append("<thread");

				if (!string.IsNullOrEmpty(ParentThreadId))
				{
					Xml.Append(" parent='");
					Xml.Append(XML.Encode(ParentThreadId));
					Xml.Append("'");
				}

				Xml.Append(">");
				Xml.Append(XML.Encode(ThreadId));
				Xml.Append("</thread>");
			}

			if (!string.IsNullOrEmpty(CustomXml))
				Xml.Append(CustomXml);

			Xml.Append("</message>");

			string MessageXml = Xml.ToString();

			switch (QoS)
			{
				case QoSLevel.Unacknowledged:
					this.BeginWrite(MessageXml, (sender, e) => this.DeliveryCallback(DeliveryCallback, State, true));
					break;

				case QoSLevel.Acknowledged:
					Xml.Clear();
					Xml.Append("<qos:acknowledged xmlns:qos='urn:xmpp:qos'>");
					Xml.Append(MessageXml);
					Xml.Append("</qos:acknowledged>");

					this.SendIqSet(From, To, Xml.ToString(), (sender, e) => this.DeliveryCallback(DeliveryCallback, State, e.Ok), null,
						2000, int.MaxValue, true, 3600000);
					break;

				case QoSLevel.Assured:
					string MsgId = Guid.NewGuid().ToString().Replace("-", string.Empty);

					Xml.Clear();
					Xml.Append("<qos:assured xmlns:qos='urn:xmpp:qos' msgId='");
					Xml.Append(MsgId);
					Xml.Append("'>");
					Xml.Append(MessageXml);
					Xml.Append("</qos:assured>");

					this.SendIqSet(From, To, Xml.ToString(), this.AssuredDeliveryStep, new object[] { DeliveryCallback, State, MsgId },
						2000, int.MaxValue, true, 3600000);
					break;
			}
		}

		private void AssuredDeliveryStep(object Sender, IqResultEventArgs e)
		{
			object[] P = (object[])e.State;
			DeliveryEventHandler DeliveryCallback = (DeliveryEventHandler)P[0];
			object State = P[1];
			string MsgId = (string)P[2];

			if (e.Ok)
			{
				foreach (XmlNode N in e.Response)
				{
					if (N.LocalName == "received")
					{
						if (MsgId == XML.Attribute((XmlElement)N, "msgId"))
						{
							StringBuilder Xml = new StringBuilder();

							Xml.Append("<qos:deliver xmlns:qos='urn:xmpp:qos' msgId='");
							Xml.Append(MsgId);
							Xml.Append("'/>");

							this.SendIqSet(e.To, e.From, Xml.ToString(), (sender, e2) => this.DeliveryCallback(DeliveryCallback, State, e2.Ok), null,
								2000, int.MaxValue, true, 3600000);
							return;
						}
					}
				}
			}

			this.DeliveryCallback(DeliveryCallback, State, false);
		}

		private void DeliveryCallback(DeliveryEventHandler Callback, object State, bool Ok)
		{
			if (Callback != null)
			{
				try
				{
					Callback(this, new DeliveryEventArgs(State, Ok));
				}
				catch (Exception ex)
				{
					this.Exception(ex);
				}
			}
		}

		/// <summary>
		/// Number of seconds before a network connection risks being closed by the network, if no communication is done over it.
		/// To avoid this, ping messages are sent over the network with an interval of half this value (in seconds).
		/// </summary>
		public int KeepAliveSeconds
		{
			get { return this.keepAliveSeconds; }
			set
			{
				if (value <= 0)
					throw new ArgumentException("Value must be positive.", "KeepAliveSeconds");

				this.keepAliveSeconds = value;
			}
		}

		private void AcknowledgedQoSMessageHandler(object Sender, IqEventArgs e)
		{
			foreach (XmlNode N in e.Query.ChildNodes)
			{
				if (N.LocalName == "message")
				{
					MessageEventArgs e2 = new MessageEventArgs(this, (XmlElement)N);

					e2.From = e.From;
					e2.To = e.To;

					this.SendIqResult(e.Id, e.To, e.From, string.Empty);
					this.ProcessMessage(e2);

					return;
				}
			}

			throw new BadRequestException(string.Empty, e.Query);
		}

		/// <summary>
		/// Maximum number of pending incoming assured messages received from a single source.
		/// </summary>
		public int MaxAssuredMessagesPendingFromSource
		{
			get { return this.maxAssuredMessagesPendingFromSource; }
			set
			{
				if (value <= 0)
					throw new ArgumentException("Value must be positive.", "MaxAssuredMessagesPendingFromSource");

				this.maxAssuredMessagesPendingFromSource = value;
			}
		}

		/// <summary>
		/// Maximum total number of pending incoming assured messages received.
		/// </summary>
		public int MaxAssuredMessagesPendingTotal
		{
			get { return this.maxAssuredMessagesPendingTotal; }
			set
			{
				if (value <= 0)
					throw new ArgumentException("Value must be positive.", "MaxAssuredMessagesPendingTotal");

				this.maxAssuredMessagesPendingTotal = value;
			}
		}

		/// <summary>
		/// Default retry timeout, in milliseconds.
		/// This value is used when sending IQ requests wihtout specifying request-specific retry parameter values.
		/// </summary>
		public int DefaultRetryTimeout
		{
			get { return this.defaultRetryTimeout; }
			set
			{
				if (value <= 0)
					throw new ArgumentException("Value must be positive.", "DefaultRetryTimeout");

				this.defaultRetryTimeout = value;
			}
		}

		/// <summary>
		/// Default number of retries if results or errors are not returned.
		/// This value is used when sending IQ requests wihtout specifying request-specific retry parameter values.
		/// </summary>
		public int DefaultNrRetries
		{
			get { return this.defaultNrRetries; }
			set
			{
				if (value <= 0)
					throw new ArgumentException("Value must be positive.", "DefaultNrRetries");

				this.defaultNrRetries = value;
			}
		}

		/// <summary>
		/// Default maximum retry timeout, in milliseconds.
		/// This value is used when sending IQ requests wihtout specifying request-specific retry parameter values.
		/// </summary>
		public int DefaultMaxRetryTimeout
		{
			get { return this.defaultMaxRetryTimeout; }
			set
			{
				if (value <= 0)
					throw new ArgumentException("Value must be positive.", "DefaultMaxRetryTimeout");

				this.defaultMaxRetryTimeout = value;
			}
		}

		/// <summary>
		/// Default Drop-off value. If drop-off is used, the retry timeout is doubled for each retry, up till the maximum retry timeout time.
		/// This value is used when sending IQ requests wihtout specifying request-specific retry parameter values.
		/// </summary>
		public bool DefaultDropOff
		{
			get { return this.defaultDropOff; }
			set { this.defaultDropOff = value; }
		}

		/// <summary>
		/// Tries to get a roster item for a given Bare JID.
		/// </summary>
		/// <param name="BareJid">Bare JID</param>
		/// <returns>Roster item, if found, or null, if not found.</returns>
		public bool TryGetRosterItem(string BareJid, out RosterItem Item)
		{
			GetRosterItemEventHandler h = this.OnGetRosterItem;
			if (h == null)
			{
				Item = null;
				return false;
			}
			else
			{
				Item = h(BareJid);
				return Item != null;
			}
		}

		/// <summary>
		/// Event raised when the component needs a roster item. Results are not cached. For performance reasons, it might be wise
		/// to cache results by the event handler.
		/// </summary>
		public event GetRosterItemEventHandler OnGetRosterItem = null;

		private void AssuredQoSMessageHandler(object Sender, IqEventArgs e)
		{
			string FromBareJid = XmppClient.GetBareJID(e.From);
			string MsgId = XML.Attribute(e.Query, "msgId");

			foreach (XmlNode N in e.Query.ChildNodes)
			{
				if (N.LocalName == "message")
				{
					MessageEventArgs e2 = new MessageEventArgs(this, (XmlElement)N);
					RosterItem Item;
					int i;

					e2.From = e.From;
					e2.To = e.To;

					lock (this.rosterSyncObject)
					{
						if (this.nrAssuredMessagesPending >= this.maxAssuredMessagesPendingTotal)
						{
							Log.Warning("Rejected incoming assured message. Unable to manage more than " + this.maxAssuredMessagesPendingTotal.ToString() +
								" pending assured messages.", XmppClient.GetBareJID(e.To), XmppClient.GetBareJID(e.From), "ResourceConstraint",
								new KeyValuePair<string, object>("Variable", "NrAssuredMessagesPending"),
								new KeyValuePair<string, object>("Limit", (double)this.maxAssuredMessagesPendingTotal),
								new KeyValuePair<string, object>("Unit", string.Empty));

							throw new StanzaErrors.ResourceConstraintException(string.Empty, e.Query);
						}

						if (!this.TryGetRosterItem(FromBareJid, out Item))
						{
							Log.Notice("Rejected incoming assured message. Sender not in roster.", XmppClient.GetBareJID(e.To), XmppClient.GetBareJID(e.From), "NotAllowed",
								new KeyValuePair<string, object>("Variable", "NrAssuredMessagesPending"));

							throw new NotAllowedException(string.Empty, e.Query);
						}

						if (this.pendingAssuredMessagesPerSource.TryGetValue(FromBareJid, out i))
						{
							if (i >= this.maxAssuredMessagesPendingFromSource)
							{
								Log.Warning("Rejected incoming assured message. Unable to manage more than " + this.maxAssuredMessagesPendingFromSource.ToString() +
									" pending assured messages from each sender.", XmppClient.GetBareJID(e.To), XmppClient.GetBareJID(e.From), "ResourceConstraint",
									new KeyValuePair<string, object>("Variable", "NrPendingAssuredMessagesPerSource"),
									new KeyValuePair<string, object>("Limit", (double)this.maxAssuredMessagesPendingFromSource),
									new KeyValuePair<string, object>("Unit", string.Empty));

								throw new StanzaErrors.ResourceConstraintException(string.Empty, e.Query);
							}
						}
						else
							i = 0;

						i++;
						this.pendingAssuredMessagesPerSource[FromBareJid] = i;
						this.receivedMessages[FromBareJid + " " + MsgId] = e2;
					}

					this.SendIqResult(e.Id, e.To, e.From, "<received msgId='" + XML.Encode(MsgId) + "'/>");
					return;
				}
			}

			throw new BadRequestException(string.Empty, e.Query);
		}

		private void DeliverQoSMessageHandler(object Sender, IqEventArgs e)
		{
			MessageEventArgs e2;
			string MsgId = XML.Attribute(e.Query, "msgId");
			string From = XmppClient.GetBareJID(e.From);
			string Key = From + " " + MsgId;
			int i;

			lock (this.rosterSyncObject)
			{
				if (this.receivedMessages.TryGetValue(Key, out e2))
				{
					this.receivedMessages.Remove(Key);
					this.nrAssuredMessagesPending--;

					if (this.pendingAssuredMessagesPerSource.TryGetValue(From, out i))
					{
						i--;
						if (i <= 0)
							this.pendingAssuredMessagesPerSource.Remove(From);
						else
							this.pendingAssuredMessagesPerSource[From] = i;
					}
				}
				else
					e2 = null;
			}

			this.SendIqResult(e.Id, e.To, e.From, string.Empty);

			if (e2 != null)
				this.ProcessMessage(e2);
		}

		private void SecondTimerCallback(object State)
		{
			if (DateTime.Now >= this.nextPing)
			{
				this.nextPing = DateTime.Now.AddMilliseconds(this.keepAliveSeconds * 500);
				try
				{
					if (this.supportsPing)
						this.SendPing(this.componentSubDomain, string.Empty, this.PingResult, null);
					else
						this.BeginWrite(" ", null);
				}
				catch (Exception ex)
				{
					this.Exception(ex);
					this.Reconnect();
				}
			}

			List<PendingRequest> Retries = null;
			DateTime Now = DateTime.Now;
			DateTime TP;
			bool Retry;

			lock (this.synchObject)
			{
				foreach (KeyValuePair<DateTime, PendingRequest> P in this.pendingRequestsByTimeout)
				{
					if (P.Key <= Now)
					{
						if (Retries == null)
							Retries = new List<PendingRequest>();

						Retries.Add(P.Value);
					}
					else
						break;
				}
			}

			if (Retries != null)
			{
				foreach (PendingRequest Request in Retries)
				{
					lock (this.synchObject)
					{
						this.pendingRequestsByTimeout.Remove(Request.Timeout);

						if (Retry = Request.CanRetry())
						{
							TP = Request.Timeout;

							while (this.pendingRequestsByTimeout.ContainsKey(TP))
								TP = TP.AddTicks(this.gen.Next(1, 10));

							Request.Timeout = TP;

							this.pendingRequestsByTimeout[Request.Timeout] = Request;
						}
						else
							this.pendingRequestsBySeqNr.Remove(Request.SeqNr);
					}

					try
					{
						if (Retry)
							this.BeginWrite(Request.Xml, null);
						else
						{
							StringBuilder Xml = new StringBuilder();

							Xml.Append("<iq xmlns='jabber:client' type='error' from='");
							Xml.Append(Request.To);
							Xml.Append("' id='");
							Xml.Append(Request.SeqNr.ToString());
							Xml.Append("'><error type='wait'><recipient-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>");
							Xml.Append("<text xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'>Timeout.</text></error></iq>");

							XmlDocument Doc = new XmlDocument();
							Doc.LoadXml(Xml.ToString());

							IqResultEventArgs e = new IqResultEventArgs(Doc.DocumentElement, Request.SeqNr.ToString(), string.Empty, Request.To, false,
								Request.State);

							IqResultEventHandler h = Request.Callback;
							if (h != null)
								h(this, e);
						}
					}
					catch (Exception ex)
					{
						this.Exception(ex);
					}
				}
			}
		}

		private void PingResult(object Sender, IqResultEventArgs e)
		{
			if (!e.Ok)
			{
				if (e.StanzaError is RecipientUnavailableException)
				{
					try
					{
						this.Reconnect();
					}
					catch (Exception ex)
					{
						Log.Critical(ex);
					}
				}
				else
					this.supportsPing = false;
			}
		}

		/// <summary>
		/// Sends an XMPP ping request.
		/// </summary>
		/// <param name="From">Address of sender.</param>
		/// <param name="To">Destination address.</param>
		/// <param name="Callback">Method to call when response or error is returned.</param>
		/// <param name="State">State object to pass on to callback method.</param>
		public void SendPing(string From, string To, IqResultEventHandler Callback, object State)
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<ping xmlns='");
			Xml.Append(XmppClient.NamespacePing);
			Xml.Append("'/>");

			this.SendIqGet(From, To, Xml.ToString(), Callback, State);
		}

		private void PingRequestHandler(object Sender, IqEventArgs e)
		{
			e.IqResult(string.Empty);
		}

		private void ServiceDiscoveryRequestHandler(object Sender, IqEventArgs e)
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<query xmlns='");
			Xml.Append(XmppClient.NamespaceServiceDiscoveryInfo);
			Xml.Append("'>");

			// TODO: Discovery on accounts.

			Xml.Append("<identity category='");
			Xml.Append(XML.Encode(this.identityCategory));
			Xml.Append("' type='");
			Xml.Append(XML.Encode(this.identityType));
			Xml.Append("' name='");
			Xml.Append(XML.Encode(this.identityName));
			Xml.Append("'/>");

			lock (this.synchObject)
			{
				foreach (string Feature in this.clientFeatures.Keys)
				{
					Xml.Append("<feature var='");
					Xml.Append(XML.Encode(Feature));
					Xml.Append("'/>");
				}
			}

			Xml.Append("</query>");

			e.IqResult(Xml.ToString());
		}

		/// <summary>
		/// Identity category, as defined in XEP-0030.
		/// </summary>
		public string IdentityCategory
		{
			get { return this.identityCategory; }
		}

		/// <summary>
		/// Identity type, as defined in XEP-0030.
		/// </summary>
		public string IdentityType
		{
			get { return this.identityType; }
		}

		/// <summary>
		/// Identity name, as defined in XEP-0030.
		/// </summary>
		public string IdentityName
		{
			get { return this.identityName; }
		}

		// TODO: Encryption
	}
}