using System;
using System.Net;
using System.Xml;

using Leedom.Common;
using Leedom.Common.Utils;

namespace SDETech.Api {

    public class ShortMessageService: IProvideSMS {

        /// <summary>The successful status code expected from the SDE Tech API send method call.</summary>
        public const string API_SEND_SUCCESS_CODE = "1";

        /// <summary>
        /// Called to record a user or system action for
        /// accountability and debugging purposes.
        /// </summary>
        public event PostEntry OnPostEntry;

        /// <summary>
        /// Called to report a probelm as an HTTP response status
        /// and message.
        /// </summary>
        public event ApplyHttpResponse OnHttpResponse;

        /// <summary>
        /// Clean up any resources.
        /// </summary>
        public void Dispose() {
        }

        /// <summary>
        /// Send a messsage to a cell recipient.
        /// </summary>
        /// <param name="user">The user identity (api key) to authenticate with the provider.</param>
        /// <param name="password">The user password (secondary key) to authenticate with the provider.</param>
        /// <param name="to">The cell phone number to send the message to.</param>
        /// <param name="from">The long or short code to send the message from.</param>
        /// <param name="message">The SMS message content.</param>
        /// <param name="reference">An optional reference to associate and keep with the message.</param>
        /// <param name="note">An optional note to associate and keep with the message.</param> 
        /// <returns>Indicates the success or failure of the send.</returns>
        public bool Send(string user, string password, string to, string from, string message, string reference, string note) {
            if (String.IsNullOrEmpty(user)) {
                this.ApplyResponse(HttpStatusCode.InternalServerError, "Client api key not configured.");
                return false;
            }

            HttpPostAdapter adapter = new HttpPostAdapter(String.Format("{0}{1}", sdetech.Default.api_base_url, sdetech.Default.send_method_name));

            adapter.AppendPostParameter("ApiKey", user);
            adapter.AppendPostParameter("PhoneNumber", to);
            adapter.AppendPostParameter("FromSMS", String.Format("{0}", from));
            adapter.AppendPostParameter("ClientUserId", reference);
            adapter.AppendPostParameter("Message", message);
            string response = null;
            
            try {
                response = adapter.Post();
            }
            catch (Exception ex) {
                this.PostEntry("SYSTEM:ERROR", String.Format("Problem dispatching message: {0}", ex.Message));
                this.ApplyResponse(HttpStatusCode.InternalServerError, "Problem dispatching message. It was not delivered.");
                return false;
            }
            
            if (String.IsNullOrEmpty(response)) {
                this.ApplyResponse(HttpStatusCode.InternalServerError, adapter.AccquireLastError());
                return false;
            }
            
            if (this.DispatchFailed(response)) {
                return false;
            }

            adapter.Clear();
            return true;
        }

        /// <summary>
		/// Check to see if the API response from the SMS
		/// provider reported an error in processing the
		/// send.
		/// </summary>
		/// <param name="response">The raw response from the SMS API provider.</param>
		/// <returns>Indicates whether or not the dispatch failed (true) or not (false).</returns>
		private bool DispatchFailed(string response) {
			if (String.IsNullOrEmpty(response)) {
				this.ApplyResponse(HttpStatusCode.InternalServerError, "Dispatch response was empty.");
				return true;
			}
			
			XmlDocument dispatchDoc = new XmlDocument();
			try {
				dispatchDoc.LoadXml(response);
			}
			catch (Exception ex) {
				this.PostEntry("SYSTEM:ERROR", String.Format("Unable to parse dispatch response: {0}", ex.Message));
				this.ApplyResponse(HttpStatusCode.InternalServerError, "Unable to parse dispatch response.");
				return true;
			}
			
			if ((dispatchDoc == null) || (dispatchDoc.ChildNodes.Count <= 0)) {
				this.ApplyResponse(HttpStatusCode.InternalServerError, "Dispatch response invalid.");
				return true;
			}
			
			if (this.ApiCallErrored(dispatchDoc)) {
				return true;
			}
			
			return false;			
		}

        /// <summary>
        /// Check to see if the given phone digits are
        /// indeed a cell phone. 
        /// </summary>
        /// <param name="user">The user identity (api key) to authenticate with the provider.</param>
        /// <param name="password">The user password (secondary key) to authenticate with the provider.</param>
        /// <param name="digits">The phone number digits to check for a cell.</param>
        /// <returns>Indicates whether or not the digits are a cell.</returns>
        public bool IsACell(string user, string password, string digits) {
            if (String.IsNullOrEmpty(user)) {
                this.ApplyResponse(HttpStatusCode.InternalServerError, "Client api key not configured.");
                return false;
            }

            HttpPostAdapter adapter = new HttpPostAdapter(String.Format("{0}{1}", sdetech.Default.api_base_url, sdetech.Default.confirm_method_name));
			adapter.AppendPostParameter("ApiKey", user);
			adapter.AppendPostParameter("PhoneNumber", String.Format("{0}", digits));
			string response = null;
			
			try {
				response = adapter.Post();
			}
			catch (Exception ex) {
				this.PostEntry("SYSTEM:ERROR", String.Format("Problem confirming number as a cell phone: {0}", ex.Message));
				this.ApplyResponse(HttpStatusCode.InternalServerError, "Problem confirming number as a cell phone.");
				return false;
			}
			
			return this.CheckConfirmation(digits, response);
        }

        /// <summary>
		/// Parse the cell phone confirmation response to see
		/// if the number is indeed a cell phone. If so, let's
		/// mark it as such in the database.
		/// </summary>
		/// <param name="cell">The phone number being confirmed as a cell.</param>
		/// <param name="response">The raw response received from the SMS provider.</param>
		/// <returns>Indiciates if the cell phone was confirmed.</returns>
		private bool CheckConfirmation(string cell, string response) {
			if (String.IsNullOrEmpty(response)) {
				this.ApplyResponse(HttpStatusCode.InternalServerError, "Cell phone confirmation response was empty.");
				return false;
			}
			
			XmlDocument verifyDoc = new XmlDocument();
			try {
				verifyDoc.LoadXml(response);
			}
			catch (Exception ex) {
				this.PostEntry("SYSTEM:WARNING", String.Format("Unable to parse cell confirmation response: {0}", ex.Message));
				this.ApplyResponse(HttpStatusCode.InternalServerError, "Unable to parse cell confirmation response.");
				return false;
			}
			
			if ((verifyDoc == null) || (verifyDoc.ChildNodes.Count <= 0)) {				
				return false;
			}
			
			if (this.ApiCallErrored(verifyDoc)) {
				return false;
			}
			
			XmlNamespaceManager namespaceMgr = new XmlNamespaceManager(verifyDoc.NameTable);
			namespaceMgr.AddNamespace("meta", verifyDoc.DocumentElement.NamespaceURI);			
			XmlNode metaNode = verifyDoc.SelectSingleNode("/meta:Response/meta:Wireless", namespaceMgr);
			if ((metaNode == null) || (String.IsNullOrEmpty(metaNode.InnerText))) {
				this.ApplyResponse(HttpStatusCode.InternalServerError, "Unable to obtain wireless confirmation result.");
				return false;
			}
			
			bool confirmed = false;
			bool parsed = Boolean.TryParse(metaNode.InnerText, out confirmed);

            if ((!parsed) || (!confirmed)) {
                return false;
            }

			string carrier = null;
			metaNode = verifyDoc.SelectSingleNode("/meta:Response/meta:Company", namespaceMgr);
			if (metaNode != null) { carrier = metaNode.InnerText; }
				
			string city = null;
			metaNode = verifyDoc.SelectSingleNode("/meta:Response/meta:RC", namespaceMgr);
			if (metaNode != null) { city = metaNode.InnerText; }
				
			string state = null;
			metaNode = verifyDoc.SelectSingleNode("/meta:Response/meta:State", namespaceMgr);
			if (metaNode != null) { state = metaNode.InnerText; }

			return true;
		}

        /// <summary>
        /// Check to see if the given short code
        /// keyword is available. Typically used 
        /// for shared short codes since Textmaxx
        /// knows all keywords for owned short 
        /// codes.
        /// </summary>
        /// <param name="user">The user identity (api key) to authenticate with the provider.</param>
        /// <param name="password">The user password (secondary key) to authenticate with the provider.</param>
        /// <param name="did">The short code DID to check if the given keyword already exists.</param>
        /// <param name="keyword">The keyword to for availability.</param>
        /// <returns>Indicates whether or not the keyword is available.</returns>
        public bool IsKeywordAvailable(string user, string password, string did, string keyword) {
            return false;
        }

        /// <summary>
		/// Check the SMS API response for an error. The error
		/// response is standard amongst all the method calls.
		/// </summary>
		/// <param name="responseDoc">The parsed XML response document from the SMS API provider.</param>
		/// <returns>Indicates whether or not an error was found (true) or not (false) in the API response.</returns>
		private bool ApiCallErrored(XmlDocument responseDoc) {
            XmlNamespaceManager namespaceMgr = new XmlNamespaceManager(responseDoc.NameTable);
            namespaceMgr.AddNamespace("meta", responseDoc.DocumentElement.NamespaceURI);
            XmlNode metaNode = responseDoc.SelectSingleNode("/meta:Response/meta:Success", namespaceMgr);
            if ((metaNode == null) || (String.IsNullOrEmpty(metaNode.InnerText))) {
                return this.ApiSendErrored(responseDoc);
            }

            bool successful = false;
            Boolean.TryParse(metaNode.InnerText, out successful);

            if (!successful) {
                metaNode = responseDoc.SelectSingleNode("/meta:Response/meta:Data", namespaceMgr);
                if ((metaNode == null) || (String.IsNullOrEmpty(metaNode.InnerText))) {
                    this.ApplyResponse(HttpStatusCode.InternalServerError, "Message failed for an unknown reason (data node missing).");
                    return true;
                }

                this.ApplyResponse(HttpStatusCode.InternalServerError, String.Format("Message failed: {0}", metaNode.InnerText));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check the SMS API send response for an error.The
        /// send method can return an error in a different way.
        /// </summary>
        /// <param name="responseDoc">The parsed XML response document from the SMS API provider.</param>
        /// <returns>Indicates whether or not an error was found (true) or not (false) in the API send response.</returns>
        private bool ApiSendErrored(XmlDocument responseDoc) {
            XmlNamespaceManager namespaceMgr = new XmlNamespaceManager(responseDoc.NameTable);
            namespaceMgr.AddNamespace("meta", responseDoc.DocumentElement.NamespaceURI);
            XmlNode metaNode = responseDoc.SelectSingleNode("/meta:SMS/meta:Status", namespaceMgr);
            if ((metaNode == null) || (String.IsNullOrEmpty(metaNode.InnerText))) {
                return false;
            }

            bool successful = metaNode.InnerText.Trim().Equals(API_SEND_SUCCESS_CODE);

            if (!successful) {
                metaNode = responseDoc.SelectSingleNode("/meta:SMS/meta:Value", namespaceMgr);
                if ((metaNode == null) || (String.IsNullOrEmpty(metaNode.InnerText))) {
                    this.ApplyResponse(HttpStatusCode.InternalServerError, "Message failed for an unknown reason (value node missing).");
                    return true;
                }

                this.ApplyResponse(HttpStatusCode.InternalServerError, String.Format("Message failed: {0}", metaNode.InnerText));
                return true;
            }

            return false;
        }

        /// <summary>
		/// Record a user or system action for later retrival.
		/// </summary>
		/// <param name="action">The user or system action performed.</param>
		/// <param name="message">A succinct description of what happened.</param>
		private void PostEntry(string action, string message) {
            this.OnPostEntry?.Invoke(action, message);
        }

        /// <summary>
        /// Update the HTTP response status to report an issue to the 
        /// API user.
        /// </summary>
        /// <param name="statusCode">The new HTTP status code.</param>
        /// <param name="message">A succinct description of the issue encountered.</param>
        private void ApplyResponse(HttpStatusCode statusCode, string message) {
            this.OnHttpResponse?.Invoke(statusCode, message);
        }

    }

}
