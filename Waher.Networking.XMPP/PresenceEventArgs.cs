﻿using System;
using System.Collections.Generic;
using System.Xml;

namespace Waher.Networking.XMPP
{
	/// <summary>
	/// Type of presence received.
	/// </summary>
	public enum PresenceType 
	{
		/// <summary>
		/// Contact is available.
		/// </summary>
		Available,

		/// <summary>
		/// An error has occurred regarding processing of a previously sent presence stanza; if the presence stanza is of type
		/// "error", it MUST include an <error/> child element (refer to [XMPP-CORE]).
		/// </summary>
		Error,

		/// <summary>
		/// A request for an entity's current presence; SHOULD be generated only by a server on behalf of a user.
		/// </summary>
		Probe,

		/// <summary>
		/// The sender wishes to subscribe to the recipient's presence.
		/// </summary>
		Subscribe,

		/// <summary>
		/// The sender has allowed the recipient to receive their presence.
		/// </summary>
		Subscribed,

		/// <summary>
		/// The sender is no longer available for communication.
		/// </summary>
		Unavailable,

		/// <summary>
		/// The sender is unsubscribing from the receiver's presence.
		/// </summary>
		Unsubscribe,

		/// <summary>
		/// The subscription request has been denied or a previously granted subscription has been canceled.
		/// </summary>
		Unsubscribed
	}

	/// <summary>
	/// Resource availability.
	/// </summary>
	public enum Availability
	{
		/// <summary>
		/// The entity or resource is online.
		/// </summary>
		Online,

		/// <summary>
		/// The entity or resource is offline.
		/// </summary>
		Offline,

		/// <summary>
		/// The entity or resource is temporarily away.
		/// </summary>
		Away,

		/// <summary>
		/// The entity or resource is actively interested in chatting.
		/// </summary>
		Chat,

		/// <summary>
		/// The entity or resource is busy.
		/// </summary>
		DoNotDisturb,

		/// <summary>
		/// The entity or resource is away for an extended period.
		/// </summary>
		ExtendedAway
	}

	/// <summary>
	/// Event arguments for presence events.
	/// </summary>
	public class PresenceEventArgs : EventArgs
	{
		private KeyValuePair<string, string>[] statuses;
		private XmlElement presence;
		private XmlElement errorElement = null;
		private ErrorType errorType = ErrorType.None;
		private XmppException stanzaError = null;
		private string errorText = string.Empty;
		private XmppClient client;
		private PresenceType type;
		private Availability availability;
		private string from;
		private string fromBaseJid;
		private string to;
		private string id;
		private string status;
		private int errorCode;
		private sbyte priority;
		private bool ok;

		internal PresenceEventArgs(XmppClient Client, XmlElement Presence)
		{
			XmlElement E;
			int i;

			this.presence = Presence;
			this.client = Client;
			this.from = XmppClient.XmlAttribute(Presence, "from");
			this.to = XmppClient.XmlAttribute(Presence, "to");
			this.id = XmppClient.XmlAttribute(Presence, "id");
			this.ok = true;
			this.errorCode = 0;
			this.availability = Availability.Online;

			i = this.from.IndexOf('/');
			if (i < 0)
				this.fromBaseJid = this.from;
			else
				this.fromBaseJid = this.from.Substring(0, i);

			switch (XmppClient.XmlAttribute(Presence, "type").ToLower())
			{
				case "error":
					this.type = PresenceType.Error;
					break;

				case "probe":
					this.type = PresenceType.Probe;
					break;

				case "subscribe":
					this.type = PresenceType.Subscribe;
					break;

				case "subscribed":
					this.type = PresenceType.Subscribed;
					break;

				case "unavailable":
					this.type = PresenceType.Unavailable;
					this.availability = Availability.Offline;
					break;

				case "unsubscribe":
					this.type = PresenceType.Unsubscribe;
					break;

				case "unsubscribed":
					this.type = PresenceType.Unsubscribed;
					break;

				default:
					this.type = PresenceType.Available;
					break;
			}

			SortedDictionary<string, string> Statuses = new SortedDictionary<string, string>();

			foreach (XmlNode N in Presence.ChildNodes)
			{
				E = N as XmlElement;
				if (E == null)
					continue;

				if (E.NamespaceURI == Presence.NamespaceURI)
				{
					switch (E.LocalName)
					{
						case "show":
							switch (E.InnerText.ToLower())
							{
								case "away":
									this.availability = Availability.Away;
									break;

								case "chat":
									this.availability = Availability.Chat;
									break;

								case "dnd":
									this.availability = Availability.DoNotDisturb;
									break;

								case "xa":
									this.availability = Availability.ExtendedAway;
									break;

								default:
									this.availability = Availability.Online;
									break;
							}
							break;

						case "status":
							if (string.IsNullOrEmpty(this.status))
								this.status = N.InnerText;

							string Language = XmppClient.XmlAttribute(E, "xml:lang");
							Statuses[Language] = N.InnerText;
							break;

						case "priority":
							if (!sbyte.TryParse(N.InnerText, out this.priority))
								this.priority = 0;
							break;

						case "error":
							this.errorElement = E;
							this.errorCode = XmppClient.XmlAttribute(E, "code", 0);
							this.ok = false;

							switch (XmppClient.XmlAttribute(E, "type"))
							{
								case "auth":
									this.errorType = ErrorType.Auth;
									break;

								case "cancel":
									this.errorType = ErrorType.Cancel;
									break;

								case "continue":
									this.errorType = ErrorType.Continue;
									break;

								case "modify":
									this.errorType = ErrorType.Modify;
									break;

								case "wait":
									this.errorType = ErrorType.Wait;
									break;

								default:
									this.errorType = ErrorType.Undefined;
									break;
							}

							this.stanzaError = XmppClient.GetStanzaExceptionObject(E);
							this.errorText = this.stanzaError.Message;
							break;
					}
				}
			}

			this.statuses = new KeyValuePair<string, string>[Statuses.Count];
			Statuses.CopyTo(this.statuses, 0);
		}

		/// <summary>
		/// Type of presence received.
		/// </summary>
		public PresenceType Type { get { return this.type; } }

		/// <summary>
		/// Resource availability.
		/// </summary>
		public Availability Availability { get { return this.availability; } }

		/// <summary>
		/// From where the presence was received.
		/// </summary>
		public string From { get { return this.from; } }

		/// <summary>
		/// Base JID of resource sending the presence.
		/// </summary>
		public string FromBaseJID { get { return this.fromBaseJid; } }

		/// <summary>
		/// To whom the presence was sent.
		/// </summary>
		public string To { get { return this.to; } }

		/// <summary>
		/// ID attribute of presence stanza.
		/// </summary>
		public string Id { get { return this.id; } }

		/// <summary>
		/// Human readable status.
		/// </summary>
		public string Status { get { return this.status; } }

		/// <summary>
		/// Presence element.
		/// </summary>
		public XmlElement Presence { get { return this.presence; } }

		/// <summary>
		/// If the response is an OK result response (true), or an error response (false).
		/// </summary>
		public bool Ok { get { return this.ok; } }

		/// <summary>
		/// Error Code
		/// </summary>
		public int ErrorCode { get { return this.errorCode; } }

		/// <summary>
		/// Error Type
		/// </summary>
		public ErrorType ErrorType { get { return this.errorType; } }

		/// <summary>
		/// Error element.
		/// </summary>
		public XmlElement ErrorElement { get { return this.errorElement; } }

		/// <summary>
		/// Any error specific text.
		/// </summary>
		public string ErrorText { get { return this.errorText; } }

		/// <summary>
		/// Any stanza error returned.
		/// </summary>
		public XmppException StanzaError { get { return this.stanzaError; } }

		/// <summary>
		/// Available set of (language,status) pairs.
		/// </summary>
		public KeyValuePair<string, string>[] Statuses { get { return this.statuses; } }

		/// <summary>
		/// Priority of presence stanza.
		/// </summary>
		public sbyte Priority { get { return this.priority; } }

		/// <summary>
		/// Accepts a subscription or unsubscription request.
		/// </summary>
		/// <exception cref="Exception">If the presence element is not a subscription or unsubscription request.</exception>
		public void Accept()
		{
			if (this.type == PresenceType.Subscribe)
				this.client.PresenceSubscriptionAccepted(this.id, this.fromBaseJid);
			else if (this.type == PresenceType.Unsubscribe)
				this.client.PresenceUnsubscriptionAccepted(this.id, this.fromBaseJid);
			else
				throw new Exception("Presence stanza is not a subscription or unsubscription.");
		}

		/// <summary>
		/// Declines a subscription or unsubscription request.
		/// </summary>
		/// <exception cref="Exception">If the presence element is not a subscription or unsubscription request.</exception>
		public void Decline()
		{
			if (this.type == PresenceType.Subscribe)
				this.client.PresenceSubscriptionDeclined(this.id, this.fromBaseJid);
			else if (this.type == PresenceType.Unsubscribe)
				this.client.PresenceUnsubscriptionDeclined(this.id, this.fromBaseJid);
			else
				throw new Exception("Presence stanza is not a subscription or unsubscription.");
		}
	}
}