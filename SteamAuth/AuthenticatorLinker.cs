﻿using System;
using System.Collections.Specialized;
using System.Net;
using System.Threading;
using Newtonsoft.Json;

namespace SteamAuth
{
    /// <summary>
    /// Handles the linking process for a new mobile authenticator.
    /// </summary>
    public class AuthenticatorLinker
    {
        /// <summary>
        /// Set to register a new phone number when linking. If a phone number is not set on the account, this must be set. If a phone number is set on the account, this must be null.
        /// </summary>
        public string PhoneNumber = null;

        /// <summary>
        /// Randomly-generated device ID. Should only be generated once per linker.
        /// </summary>
        public string DeviceID { get; }

        /// <summary>
        /// After the initial link step, if successful, this will be the SteamGuard data for the account. PLEASE save this somewhere after generating it; it's vital data.
        /// </summary>
        public SteamGuardAccount LinkedAccount { get; private set; }

        /// <summary>
        /// True if the authenticator has been fully finalized.
        /// </summary>
        public bool Finalized = false;

        private readonly SessionData session;
        private readonly CookieContainer cookies;
        private bool confirmationEmailSent;

        public AuthenticatorLinker(SessionData session)
        {
            this.session = session;
            DeviceID = GenerateDeviceID();

            cookies = new CookieContainer();
            session.AddCookies(cookies);
        }

        public LinkResult AddAuthenticator()
        {
            bool hasPhone = hasPhoneAttached();
            if (hasPhone && PhoneNumber != null)
                return LinkResult.MustRemovePhoneNumber;
            if (!hasPhone && PhoneNumber == null)
                return LinkResult.MustProvidePhoneNumber;

            if (!hasPhone)
            {
                if (confirmationEmailSent)
                {
                    if (!checkEmailConfirmation())
                    {
                        return LinkResult.GeneralFailure;
                    }
                }
                else if (!addPhoneNumber())
                {
                    return LinkResult.GeneralFailure;
                }
                else
                {
                    confirmationEmailSent = true;
                    return LinkResult.MustConfirmEmail;
                }
            }

            var postData = new NameValueCollection
            {
                { "access_token", session.OAuthToken },
                { "steamid", session.SteamID.ToString() },
                { "authenticator_type", "1" },
                { "device_identifier", DeviceID },
                { "sms_phone_id", "1" }
            };

            string response = SteamWeb.MobileLoginRequest(APIEndpoints.STEAMAPI_BASE + "/ITwoFactorService/AddAuthenticator/v0001", "POST", postData);
            if (response == null) return LinkResult.GeneralFailure;

            var addAuthenticatorResponse = JsonConvert.DeserializeObject<AddAuthenticatorResponse>(response);
            if (addAuthenticatorResponse == null || addAuthenticatorResponse.Response == null)
            {
                return LinkResult.GeneralFailure;
            }

            if (addAuthenticatorResponse.Response.Status == 29)
            {
                return LinkResult.AuthenticatorPresent;
            }

            if (addAuthenticatorResponse.Response.Status != 1)
            {
                return LinkResult.GeneralFailure;
            }

            LinkedAccount = addAuthenticatorResponse.Response;
            LinkedAccount.Session = session;
            LinkedAccount.DeviceID = DeviceID;

            return LinkResult.AwaitingFinalization;
        }

        public FinalizeResult FinalizeAddAuthenticator(string smsCode)
        {
            //The act of checking the SMS code is necessary for Steam to finalize adding the phone number to the account.
            //Of course, we only want to check it if we're adding a phone number in the first place...

            if (!string.IsNullOrEmpty(PhoneNumber) && !checkSMSCode(smsCode))
            {
                return FinalizeResult.BadSMSCode;
            }

            var postData = new NameValueCollection
            {
                { "steamid", session.SteamID.ToString() },
                { "access_token", session.OAuthToken },
                { "activation_code", smsCode }
            };
            int tries = 0;
            while (tries <= 30)
            {
                postData.Set("authenticator_code", LinkedAccount.GenerateSteamGuardCode());
                postData.Set("authenticator_time", TimeAligner.GetSteamTime().ToString());

                string response = SteamWeb.MobileLoginRequest(APIEndpoints.STEAMAPI_BASE + "/ITwoFactorService/FinalizeAddAuthenticator/v0001", "POST", postData);
                if (response == null) return FinalizeResult.GeneralFailure;

                var finalizeResponse = JsonConvert.DeserializeObject<FinalizeAuthenticatorResponse>(response);

                if (finalizeResponse == null || finalizeResponse.Response == null)
                {
                    return FinalizeResult.GeneralFailure;
                }

                if (finalizeResponse.Response.Status == 89)
                {
                    return FinalizeResult.BadSMSCode;
                }

                if (finalizeResponse.Response.Status == 88)
                {
                    if (tries >= 30)
                    {
                        return FinalizeResult.UnableToGenerateCorrectCodes;
                    }
                }

                if (!finalizeResponse.Response.Success)
                {
                    return FinalizeResult.GeneralFailure;
                }

                if (finalizeResponse.Response.WantMore)
                {
                    tries++;
                    continue;
                }

                LinkedAccount.FullyEnrolled = true;
                return FinalizeResult.Success;
            }

            return FinalizeResult.GeneralFailure;
        }

        private bool checkSMSCode(string smsCode)
        {
            var postData = new NameValueCollection
            {
                { "op", "check_sms_code" },
                { "arg", smsCode },
                { "checkfortos", "0" },
                { "skipvoip", "1" },
                { "sessionid", session.SessionID }
            };

            string response = SteamWeb.Request(APIEndpoints.COMMUNITY_BASE + "/steamguard/phoneajax", "POST", postData, cookies);
            if (response == null) return false;

            var addPhoneNumberResponse = JsonConvert.DeserializeObject<AddPhoneResponse>(response);

            if (!addPhoneNumberResponse.Success)
            {
                Thread.Sleep(3500); //It seems that Steam needs a few seconds to finalize the phone number on the account.
                return hasPhoneAttached();
            }

            return true;
        }

        private bool addPhoneNumber()
        {
            var postData = new NameValueCollection
            {
                { "op", "add_phone_number" },
                { "arg", PhoneNumber },
                { "sessionid", session.SessionID }
            };

            string response = SteamWeb.Request(APIEndpoints.COMMUNITY_BASE + "/steamguard/phoneajax", "POST", postData, cookies);
            if (response == null) return false;

            var addPhoneNumberResponse = JsonConvert.DeserializeObject<AddPhoneResponse>(response);
            return addPhoneNumberResponse.Success;
        }

        private bool checkEmailConfirmation()
        {
            var postData = new NameValueCollection
            {
                { "op", "email_confirmation" },
                { "arg", "" },
                { "sessionid", session.SessionID }
            };

            string response = SteamWeb.Request(APIEndpoints.COMMUNITY_BASE + "/steamguard/phoneajax", "POST", postData, cookies);
            if (response == null) return false;

            var emailConfirmationResponse = JsonConvert.DeserializeObject<AddPhoneResponse>(response);
            return emailConfirmationResponse.Success;
        }

        private bool hasPhoneAttached()
        {
            var postData = new NameValueCollection
            {
                { "op", "has_phone" },
                { "arg", "null" },
                { "sessionid", session.SessionID }
            };

            string response = SteamWeb.Request(APIEndpoints.COMMUNITY_BASE + "/steamguard/phoneajax", "POST", postData, cookies);
            if (response == null) return false;

            var hasPhoneResponse = JsonConvert.DeserializeObject<HasPhoneResponse>(response);
            return hasPhoneResponse.HasPhone;
        }

        public enum LinkResult
        {
            MustProvidePhoneNumber, //No phone number on the account
            MustRemovePhoneNumber, //A phone number is already on the account
            MustConfirmEmail, //User need to click link from confirmation email
            AwaitingFinalization, //Must provide an SMS code
            GeneralFailure, //General failure (really now!)
            AuthenticatorPresent
        }

        public enum FinalizeResult
        {
            BadSMSCode,
            UnableToGenerateCorrectCodes,
            Success,
            GeneralFailure
        }

        private class AddAuthenticatorResponse
        {
            [JsonProperty("response")]
            public SteamGuardAccount Response { get; set; }
        }

        private class FinalizeAuthenticatorResponse
        {
            [JsonProperty("response")]
            public FinalizeAuthenticatorInternalResponse Response { get; set; }

            internal class FinalizeAuthenticatorInternalResponse
            {
                [JsonProperty("status")]
                public int Status { get; set; }

                [JsonProperty("server_time")]
                public long ServerTime { get; set; }

                [JsonProperty("want_more")]
                public bool WantMore { get; set; }

                [JsonProperty("success")]
                public bool Success { get; set; }
            }
        }

        private class HasPhoneResponse
        {
            [JsonProperty("has_phone")]
            public bool HasPhone { get; set; }
        }

        private class AddPhoneResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }
        }

        public static string GenerateDeviceID()
        {
            return "android:" + Guid.NewGuid().ToString();
        }
    }
}
