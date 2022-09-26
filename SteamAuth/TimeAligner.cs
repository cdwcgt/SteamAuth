using System;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SteamAuth
{
    /// <summary>
    /// Class to help align system time with the Steam server time. Not super advanced; probably not taking some things into account that it should.
    /// Necessary to generate up-to-date codes. In general, this will have an error of less than a second, assuming Steam is operational.
    /// </summary>
    public class TimeAligner
    {
        private static bool aligned = false;
        private static int timeDifference = 0;

        public static long GetSteamTime()
        {
            if (!TimeAligner.aligned)
            {
                TimeAligner.AlignTime();
            }
            return Util.GetSystemUnixTime() + timeDifference;
        }

        public static async Task<long> GetSteamTimeAsync()
        {
            if (!TimeAligner.aligned)
            {
                await TimeAligner.AlignTimeAsync();
            }
            return Util.GetSystemUnixTime() + timeDifference;
        }

        public static void AlignTime()
        {
            long currentTime = Util.GetSystemUnixTime();
            using (WebClient client = new WebClient())
            {
                try
                {
                    string response = client.UploadString(APIEndpoints.TWO_FACTOR_TIME_QUERY(), "steamid=0");
                    TimeQuery query = JsonConvert.DeserializeObject<TimeQuery>(response);
                    TimeAligner.timeDifference = (int)(query.Response.ServerTime - currentTime);
                    TimeAligner.aligned = true;
                }
                catch (WebException)
                {
                    return;
                }
            }
        }

        public static async Task AlignTimeAsync()
        {
            long currentTime = Util.GetSystemUnixTime();
            WebClient client = new WebClient();
            try
            {
                string response = await client.UploadStringTaskAsync(new Uri(APIEndpoints.TWO_FACTOR_TIME_QUERY()), "steamid=0");
                TimeQuery query = JsonConvert.DeserializeObject<TimeQuery>(response);
                TimeAligner.timeDifference = (int)(query.Response.ServerTime - currentTime);
                TimeAligner.aligned = true;
            }
            catch (WebException)
            {
                return;
            }
        }

        internal class TimeQuery
        {
            [JsonProperty("response")]
            internal TimeQueryResponse Response { get; set; }

            internal class TimeQueryResponse
            {
                [JsonProperty("server_time")]
                public long ServerTime { get; set; }
            }

        }
    }
}
