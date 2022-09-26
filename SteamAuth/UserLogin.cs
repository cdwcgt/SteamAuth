﻿using System;
using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace SteamAuth
{
    /// <summary>
    /// Handles logging the user into the mobile Steam website. Necessary to generate OAuth token and session cookies.
    /// </summary>
    public class UserLogin
    {
        public string Username;
        public string Password;
        public ulong SteamID;

        public bool RequiresCaptcha;
        public string CaptchaGID = null;
        public string CaptchaText = null;

        public bool RequiresEmail;
        public string EmailDomain = null;
        public string EmailCode = null;

        public bool Requires2FA;
        public string TwoFactorCode = null;

        public SessionData Session = null;
        public bool LoggedIn = false;

        private readonly CookieContainer cookies = new CookieContainer();

        public UserLogin(string username, string password)
        {
            Username = username;
            Password = password;
        }

        public LoginResult DoLogin()
        {
            var postData = new NameValueCollection();
            var cookies = this.cookies;
            string response;

            if (cookies.Count == 0)
            {
                //Generate a SessionID
                cookies.Add(new Cookie("mobileClientVersion", "0 (2.1.3)", "/", ".steamcommunity.com"));
                cookies.Add(new Cookie("mobileClient", "android", "/", ".steamcommunity.com"));
                cookies.Add(new Cookie("Steam_Language", "english", "/", ".steamcommunity.com"));

                NameValueCollection headers = new NameValueCollection
                {
                    { "X-Requested-With", "com.valvesoftware.android.steam.community" }
                };

                SteamWeb.MobileLoginRequest("https://steamcommunity.com/login?oauth_client_id=DE45CD61&oauth_scope=read_profile%20write_profile%20read_client%20write_client", "GET", null, cookies, headers);
            }

            postData.Add("donotcache", (TimeAligner.GetSteamTime() * 1000).ToString());
            postData.Add("username", Username);
            response = SteamWeb.MobileLoginRequest(APIEndpoints.COMMUNITY_BASE + "/login/getrsakey", "POST", postData, cookies);
            if (response == null || response.Contains("<BODY>\nAn error occurred while processing your request.")) return LoginResult.GeneralFailure;

            var rsaResponse = JsonConvert.DeserializeObject<RSAResponse>(response);

            if (!rsaResponse.Success)
            {
                return LoginResult.BadRSA;
            }

            Thread.Sleep(350); //Sleep for a bit to give Steam a chance to catch up??

            // RNGCryptoServiceProvider secureRandom = new RNGCryptoServiceProvider();  不需要赋值
            byte[] encryptedPasswordBytes;
            using (var rsaEncryptor = new RSACryptoServiceProvider())
            {
                byte[] passwordBytes = Encoding.ASCII.GetBytes(Password);
                var rsaParameters = rsaEncryptor.ExportParameters(false);
                rsaParameters.Exponent = Util.HexStringToByteArray(rsaResponse.Exponent);
                rsaParameters.Modulus = Util.HexStringToByteArray(rsaResponse.Modulus);
                rsaEncryptor.ImportParameters(rsaParameters);
                encryptedPasswordBytes = rsaEncryptor.Encrypt(passwordBytes, false);
            }

            string encryptedPassword = Convert.ToBase64String(encryptedPasswordBytes);

            postData.Clear();
            postData.Add("donotcache", (TimeAligner.GetSteamTime() * 1000).ToString());

            postData.Add("password", encryptedPassword);
            postData.Add("username", Username);
            postData.Add("twofactorcode", TwoFactorCode ?? "");

            postData.Add("emailauth", RequiresEmail ? EmailCode : "");
            postData.Add("loginfriendlyname", "");
            postData.Add("captchagid", RequiresCaptcha ? CaptchaGID : "-1");
            postData.Add("captcha_text", RequiresCaptcha ? CaptchaText : "");
            postData.Add("emailsteamid", (Requires2FA || RequiresEmail) ? SteamID.ToString() : "");

            postData.Add("rsatimestamp", rsaResponse.Timestamp);
            postData.Add("remember_login", "true");
            postData.Add("oauth_client_id", "DE45CD61");
            postData.Add("oauth_scope", "read_profile write_profile read_client write_client");

            response = SteamWeb.MobileLoginRequest(APIEndpoints.COMMUNITY_BASE + "/login/dologin", "POST", postData, cookies);
            if (response == null) return LoginResult.GeneralFailure;

            var loginResponse = JsonConvert.DeserializeObject<LoginResponse>(response);

            if (loginResponse.Message != null)
            {
                if (loginResponse.Message.Contains("There have been too many login failures"))
                    return LoginResult.TooManyFailedLogins;

                if (loginResponse.Message.Contains("Incorrect login"))
                    return LoginResult.BadCredentials;
            }

            if (loginResponse.CaptchaNeeded)
            {
                RequiresCaptcha = true;
                CaptchaGID = loginResponse.CaptchaGID;
                return LoginResult.NeedCaptcha;
            }

            if (loginResponse.EmailAuthNeeded)
            {
                RequiresEmail = true;
                SteamID = loginResponse.EmailSteamID;
                return LoginResult.NeedEmail;
            }

            if (loginResponse.TwoFactorNeeded && !loginResponse.Success)
            {
                Requires2FA = true;
                return LoginResult.Need2FA;
            }

            if (loginResponse.OAuthData == null || loginResponse.OAuthData.OAuthToken == null || loginResponse.OAuthData.OAuthToken.Length == 0)
            {
                return LoginResult.GeneralFailure;
            }

            if (!loginResponse.LoginComplete)
            {
                return LoginResult.BadCredentials;
            }
            else
            {
                var readableCookies = cookies.GetCookies(new Uri("https://steamcommunity.com"));
                var oAuthData = loginResponse.OAuthData;

                SessionData session = new SessionData
                {
                    OAuthToken = oAuthData.OAuthToken,
                    SteamID = oAuthData.SteamID
                };
                session.SteamLogin = session.SteamID + "%7C%7C" + oAuthData.SteamLogin;
                session.SteamLoginSecure = session.SteamID + "%7C%7C" + oAuthData.SteamLoginSecure;
                session.WebCookie = oAuthData.Webcookie;
                session.SessionID = readableCookies["sessionid"].Value;
                Session = session;
                LoggedIn = true;
                return LoginResult.LoginOkay;
            }
        }

        private class LoginResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("login_complete")]
            public bool LoginComplete { get; set; }

            [JsonProperty("oauth")]
            public string OAuthDataString { get; set; }

            public OAuth OAuthData => OAuthDataString != null ? JsonConvert.DeserializeObject<OAuth>(OAuthDataString) : null;

            [JsonProperty("captcha_needed")]
            public bool CaptchaNeeded { get; set; }

            [JsonProperty("captcha_gid")]
            public string CaptchaGID { get; set; }

            [JsonProperty("emailsteamid")]
            public ulong EmailSteamID { get; set; }

            [JsonProperty("emailauth_needed")]
            public bool EmailAuthNeeded { get; set; }

            [JsonProperty("requires_twofactor")]
            public bool TwoFactorNeeded { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }

            internal class OAuth
            {
                [JsonProperty("steamid")]
                public ulong SteamID { get; set; }

                [JsonProperty("oauth_token")]
                public string OAuthToken { get; set; }

                [JsonProperty("wgtoken")]
                public string SteamLogin { get; set; }

                [JsonProperty("wgtoken_secure")]
                public string SteamLoginSecure { get; set; }

                [JsonProperty("webcookie")]
                public string Webcookie { get; set; }
            }
        }

        private class RSAResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("publickey_exp")]
            public string Exponent { get; set; }

            [JsonProperty("publickey_mod")]
            public string Modulus { get; set; }

            [JsonProperty("timestamp")]
            public string Timestamp { get; set; }

            [JsonProperty("steamid")]
            public ulong SteamID { get; set; }
        }
    }

    public enum LoginResult
    {
        LoginOkay,
        GeneralFailure,
        BadRSA,
        BadCredentials,
        NeedCaptcha,
        Need2FA,
        NeedEmail,
        TooManyFailedLogins,
    }
}
