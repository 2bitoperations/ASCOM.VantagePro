using System;
using System.Drawing;
using System.IO;
using System.Net;
using ASCOM.Utilities;
using Newtonsoft.Json.Linq;

namespace ASCOM.VantagePro
{
    /// <summary>
    /// Speaks the modern WeatherLink Live local API instead of the legacy
    /// binary WeatherLinkIP protocol: a plain HTTP GET of
    /// /v1/current_conditions (port 80 on a real WeatherLink Live hub),
    /// returning JSON rather than a wakeup/WRD/LOOP binary exchange.
    ///
    /// Shares its IP address/port profile keys with <see cref="SocketFetcher"/>
    /// since both are just "the WeatherLinkIP mode" (VantagePro.OpMode.IP) with
    /// a different wire protocol selected via VantagePro.IPProtocol -- same
    /// physical host:port, same Setup dialog fields.
    /// </summary>
    public class WeatherLinkLiveFetcher : Fetcher
    {
        public const ushort defaultPort = 80;

        private static readonly Util util = new Util();

        private string _stationModel = "Unknown";
        private string _stationName = "Unknown";

        public string Address { get; set; }
        public UInt16 Port { get; set; } = defaultPort;

        public WeatherLinkLiveFetcher()
        {
            ReadLowerProfile();
            lowerFetcher = this;
        }

        /// <summary>
        /// Read the device configuration from the ASCOM Profile store. Reuses
        /// SocketFetcher's profile keys: this is the same "IP" operational
        /// mode, just a different protocol.
        /// </summary>
        public override void ReadLowerProfile()
        {
            using (Profile driverProfile = new Profile() { DeviceType = "ObservingConditions" })
            {
                string op = "ReadLowerProfile";
                Address = driverProfile.GetValue(DriverId, SocketFetcher.Profile_IPAddress, string.Empty, "");
                Port = Convert.ToUInt16(driverProfile.GetValue(DriverId, SocketFetcher.Profile_IPPort, string.Empty, defaultPort.ToString()));
                #region trace
                VantagePro.LogMessage(op, $"Address: '{Address}', Port: '{Port}'");
                #endregion
            }
        }

        public override void WriteLowerProfile()
        {
            using (Profile driverProfile = new Profile() { DeviceType = "ObservingConditions" })
            {
                string op = "WriteLowerProfile";

                driverProfile.WriteValue(DriverId, SocketFetcher.Profile_IPAddress, Address);
                driverProfile.WriteValue(DriverId, SocketFetcher.Profile_IPPort, Port.ToString());
                #region trace
                VantagePro.LogMessage(op, $"Address: '{Address}', Port: '{Port}'");
                #endregion
            }
        }

        public string Source
        {
            get
            {
                return $"[{Address}:{Port}]";
            }
        }

        private string Url
        {
            get
            {
                return $"http://{Address}:{Port}/v1/current_conditions";
            }
        }

        /// <summary>
        /// Fetches and parses one /v1/current_conditions response.
        /// Returns null (and logs why) on any network failure, malformed
        /// JSON, or a well-formed "no data" response -- callers treat that
        /// exactly like a WeatherLinkIP LOOP request that got no ACK: leave
        /// LastRead untouched so TimeSinceLastUpdate reflects the outage.
        /// </summary>
        private JObject FetchCurrentConditions(out string error)
        {
            string op = "WeatherLinkLive.Fetch";
            error = null;

            if (string.IsNullOrWhiteSpace(Address))
            {
                error = "empty address";
                return null;
            }

            string body;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Url);
                request.Method = "GET";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    body = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                #region trace
                VantagePro.LogMessage(op, $"{Source}: GET {Url} failed: {error}");
                #endregion
                return null;
            }

            JObject root;
            try
            {
                root = JObject.Parse(body);
            }
            catch (Exception ex)
            {
                error = $"malformed JSON: {ex.Message}";
                #region trace
                VantagePro.LogMessage(op, $"{Source}: {error}. Body: {body}");
                #endregion
                return null;
            }

            if (!(root["data"] is JObject data))
            {
                error = (string)root["error"] ?? "no \"data\" in response (station reports no current conditions)";
                #region trace
                VantagePro.LogMessage(op, $"{Source}: {error}");
                #endregion
                return null;
            }

            #region trace
            VantagePro.LogMessage(op, $"{Source}: Got conditions, did: {(string)data["did"]}, ts: {(string)data["ts"]}");
            #endregion
            return data;
        }

        private static double? AsDouble(JToken token)
        {
            return (token == null || token.Type == JTokenType.Null) ? (double?)null : token.Value<double>();
        }

        public override void FetchSensorData()
        {
            string op = "FetchSensorData";

            JObject data = FetchCurrentConditions(out string error);
            if (data == null)
            {
                #region trace
                VantagePro.LogMessage(op, $"{Source}: {error}");
                #endregion
                return;
            }

            if (!(data["conditions"] is JArray conditions))
            {
                #region trace
                VantagePro.LogMessage(op, $"{Source}: response has no \"conditions\" array");
                #endregion
                return;
            }

            JObject iss = null, bar = null;
            foreach (JToken cond in conditions)
            {
                int? type = (int?)cond["data_structure_type"];
                if (type == 1 && iss == null)
                    iss = cond as JObject;
                else if (type == 3 && bar == null)
                    bar = cond as JObject;
            }

            if (iss == null)
            {
                #region trace
                VantagePro.LogMessage(op, $"{Source}: no data_structure_type=1 (ISS) block in conditions -- nothing to parse");
                #endregion
                return;
            }

            lock (sensorDataLock)
            {
                double? tempF = AsDouble(iss["temp"]);
                if (tempF.HasValue)
                {
                    sensorData["outsideTemp"] = util.ConvertUnits(tempF.Value, Units.degreesFahrenheit, Units.degreesCelsius).ToString();
                    #region trace
                    VantagePro.tl.LogMessage("outsideTemp", $"Fahrenheit: {tempF} -> sensorData[\"outsideTemp\"]: {sensorData["outsideTemp"]} (Celsius)");
                    #endregion
                }

                double? hum = AsDouble(iss["hum"]);
                if (hum.HasValue)
                    sensorData["outsideHumidity"] = hum.Value.ToString();

                double? dewF = AsDouble(iss["dew_point"]);
                if (dewF.HasValue)
                    sensorData["outsideDewPt"] = util.ConvertUnits(dewF.Value, Units.degreesFahrenheit, Units.degreesCelsius).ToString();

                double? windAvgMph = AsDouble(iss["wind_speed_avg_last_2_min"]);
                if (windAvgMph.HasValue)
                {
                    sensorData["windSpeed"] = util.ConvertUnits(windAvgMph.Value, Units.milesPerHour, Units.metresPerSecond).ToString();
                    #region trace
                    VantagePro.tl.LogMessage("windSpeed", $"mph: {windAvgMph} -> sensorData[\"windSpeed\"]: {sensorData["windSpeed"]} (m/s)");
                    #endregion
                }

                double? windDir = AsDouble(iss["wind_dir_scalar_avg_last_2_min"]);
                if (windDir.HasValue)
                    sensorData["windDir"] = windDir.Value.ToString();

                // "Peak 3 second wind gust over the last 2 minutes" per the
                // ASCOM IObservingConditions contract has no exact analog in
                // this API; wind_speed_hi_last_10_min (the highest recent
                // sample) is the closest match, same as the legacy binary
                // path's use of the LOOP packet's "10-min high" field.
                double? windGustMph = AsDouble(iss["wind_speed_hi_last_10_min"]);
                if (windGustMph.HasValue)
                {
                    sensorData["windGust"] = util.ConvertUnits(windGustMph.Value, Units.milesPerHour, Units.metresPerSecond).ToString();
                    #region trace
                    VantagePro.tl.LogMessage("windGust", $"mph: {windGustMph} -> sensorData[\"windGust\"]: {sensorData["windGust"]}");
                    #endregion
                }

                // Already a physical rain rate (in/hr), unlike the legacy
                // binary path which stores a raw, unconverted click count.
                double? rainRate = AsDouble(iss["rain_rate_last"]);
                if (rainRate.HasValue)
                    sensorData["rainRate"] = rainRate.Value.ToString();

                if (bar != null)
                {
                    double? barInHg = AsDouble(bar["bar_sea_level"]);
                    if (barInHg.HasValue)
                        sensorData["barometer"] = util.ConvertUnits(barInHg.Value, Units.inHg, Units.hPa).ToString();
                }

                _stationName = (string)data["did"] ?? _stationName;
            }

            LastRead = DateTime.Now;
            #region trace
            VantagePro.LogMessage(op, $"{Source}: End");
            #endregion
        }

        public override string StationModel
        {
            get
            {
                return _stationModel;
            }

            set
            {
                _stationModel = value;
            }
        }

        public override string StationName
        {
            get
            {
                return _stationName;
            }

            set
            {
                _stationName = value;
            }
        }

        public override VantagePro.DataSourceClass DataSource
        {
            get
            {
                return new VantagePro.DataSourceClass
                {
                    Type = "http",
                    Details = Url,
                };
            }
        }

        public void Test(string address, string port, ref string result, ref Color color)
        {
            string op = "WeatherLinkLive.Test";

            #region trace
            VantagePro.LogMessage(op, "Start");
            #endregion
            if (string.IsNullOrWhiteSpace(address))
            {
                result = "Empty IP address";
                color = VantagePro.colorError;
                return;
            }
            Address = address;

            if (string.IsNullOrWhiteSpace(port))
            {
                Port = defaultPort;
            }
            else
            {
                try
                {
                    Port = Convert.ToUInt16(port);
                }
                catch
                {
                    Port = defaultPort;
                }
            }

            JObject data = FetchCurrentConditions(out string error);
            if (data != null)
            {
                string did = (string)data["did"];
                result = $"Found a WeatherLink Live station (did: {did}) at {Url}.";
                color = VantagePro.colorGood;
                #region trace
                VantagePro.LogMessage(op, result);
                #endregion
            }
            else
            {
                result = $"Could not get current conditions from {Url}: {error}";
                color = VantagePro.colorError;
                #region trace
                VantagePro.LogMessage(op, result);
                #endregion
            }
            #region trace
            VantagePro.LogMessage(op, "Done");
            #endregion
        }
    }
}
