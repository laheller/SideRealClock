#pragma warning disable CS0618 // Type or member is obsolete

using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Locations;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Timers;
using System.Xml;
using Xamarin.Essentials;
using Com.Caverock.Androidsvg;
using AASharp;
using JLocale = Java.Util.Locale;
using Android.Graphics;
using SysTrace = System.Diagnostics.Trace;

namespace SideRealClock {
    [Activity(Label = "@string/app_name", Theme = "@style/Theme.AppCompat", MainLauncher = true)]
    public class MainActivity : AppCompatActivity, ILocationListener {
        private LocationManager lm = null;
        private double longitude = 0.0;
        private double latitude = 0.0;
        private Timer SideRealTimer = null;
        private Timer LunarTimer = null;
        private Geocoder geocoder = null;

        protected override async void OnCreate(Bundle savedInstanceState) {
            base.OnCreate(savedInstanceState);
            Platform.Init(this, savedInstanceState);
            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.activity_main);

            if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted) await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

            lm = LocationManager.FromContext(this);
            if (!lm.IsProviderEnabled(LocationManager.GpsProvider) || !lm.IsProviderEnabled(LocationManager.NetworkProvider)) {
                Toast.MakeText(this, "Please turn on GPS!", ToastLength.Short).Show();
                StartActivity(new Intent(Settings.ActionLocationSourceSettings));
            }

            var tvZone = FindViewById<AppCompatTextView>(Resource.Id.tvZone);
            var tvSide = FindViewById<AppCompatTextView>(Resource.Id.tvSide);
            tvSide.SetTextColor(Color.Green);
            var tvMoonPhase = FindViewById<AppCompatTextView>(Resource.Id.tvMoonPhase);
            var tvMoonIlm = FindViewById<AppCompatTextView>(Resource.Id.tvMoonIlm);

            var ClockType = true; // means sidereal

            var iv1 = FindViewById<AppCompatImageView>(Resource.Id.iv1);
            //iv1.SetLayerType(LayerType.Hardware, null);
            iv1.Click += (s, e) => {
                if (!ClockType) {
                    ClockType = true; // switch to sidereal
                    tvSide.SetTextColor(Color.Green);
                    tvZone.SetTextColor(Color.LightGray);
                }
                else {
                    ClockType = false;    // switch to zone
                    tvSide.SetTextColor(Color.LightGray);
                    tvZone.SetTextColor(Color.Green);
                }
            };

            SupportActionBar.Subtitle = $"SVG lib version: {SVG.Version}";
            
            var str = "";
            using (var reader = new StreamReader(Resources.OpenRawResource(Resource.Raw.clock))) str = await reader.ReadToEndAsync();
            var svg1 = SVG.GetFromString(str);
            iv1.SetImageDrawable(new PictureDrawable(svg1.RenderToPicture()));

            var doc = new XmlDocument();
            doc.LoadXml(str);

            var secs = doc.SelectSingleNode("//*[local-name() = 'path' and @id = 'seconds']") as XmlElement;
            var mins = doc.SelectSingleNode("//*[local-name() = 'path' and @id = 'minutes']") as XmlElement;
            var hour = doc.SelectSingleNode("//*[local-name() = 'path' and @id = 'hours']") as XmlElement;

            var moon_dial = doc.SelectSingleNode("//*[local-name() = 'g' and @id = 'moon_dial']") as XmlElement;

            // load moon phase names
            var MP_NewMoon = Resources.GetString(Resource.String.NewMoon);
            var MP_WaxingCrescent = Resources.GetString(Resource.String.WaxingCrescent);
            var MP_FirstQuarter = Resources.GetString(Resource.String.FirstQuarter);
            var MP_WaxingGibbous = Resources.GetString(Resource.String.WaxingGibbous);
            var MP_FullMoon = Resources.GetString(Resource.String.FullMoon);
            var MP_WaningGibbous = Resources.GetString(Resource.String.WaningGibbous);
            var MP_LastQuarter = Resources.GetString(Resource.String.LastQuarter);
            var MP_WaningCrescent = Resources.GetString(Resource.String.WaningCrescent);

            SideRealTimer = new Timer(1000.0) { AutoReset = true, Enabled = false };
            SideRealTimer.Elapsed += (s, e) => {
                var current = DateTime.UtcNow;
                var jd = new AASDate(current.Year, current.Month, current.Day, current.Hour, current.Minute, current.Second, true).Julian;
                var gmst = AASSidereal.MeanGreenwichSiderealTime(jd);
                var lmst = AASCoordinateTransformation.MapTo0To24Range(gmst + AASCoordinateTransformation.DegreesToHours(longitude));
                var ts1 = TimeSpan.FromHours(lmst);
                var now = DateTime.Now;
                var ts2 = TimeSpan.Zero;
                if (!ClockType) {
                    ts2 = ts1;              // backup sidereal time
                    ts1 = now.TimeOfDay;    // switch to zone time clock
                }

                var totalMins = ts1.Minutes + ts1.Seconds / 60.0f;
                var totalHours = ts1.Hours + totalMins / 60.0f;
                if (totalHours >= 12.0f) totalHours -= 12.0f;

                hour.SetAttribute("transform", $"rotate({(totalHours * 30.0f).ToString(CultureInfo.InvariantCulture)} 525,840)");
                mins.SetAttribute("transform", $"rotate({(totalMins * 6.0f).ToString(CultureInfo.InvariantCulture)} 525,840)");
                secs.SetAttribute("transform", $"rotate({ts1.Seconds * 6} 525,840)");

                var svg2 = SVG.GetFromString(doc.OuterXml);
                iv1.SetImageDrawable(new PictureDrawable(svg2.RenderToPicture()));

                if (!ClockType) ts1 = ts2;  // restore the sidereal time when needed

                RunOnUiThread(() => {
                    tvZone.Text = $"Zone time:           {now:HH\\:mm\\:ss}";
                    tvSide.Text = $"Local sidereal time: {ts1:hh\\:mm\\:ss}";
                });
            };
            SideRealTimer.Start();

            LunarTimer = new Timer(15 * 1000.0) { AutoReset = true, Enabled = false };
            LunarTimer.Elapsed += (s, e) => {
                var now = DateTime.UtcNow;
                var jd = GetJulianDay(now);

                var ilm = GetMoonIllumination(jd.Julian);
                //var jdtt = AASDynamicalTime.UTC2TT(jd.Julian);
                //var rv = AASMoon.RadiusVector(jdtt);

                var k = (int)AASMoonPhases.K(jd.FractionalYear);

                var PrevNewMoonJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k - 1));
                var PrevFirstQuarterJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k - 0.75));
                var PrevFullMoonJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k - 0.5));
                var PrevLastQuarterJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k - 0.25));
                var NewMoonJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k));
                var FirstQuarterJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k + 0.25));
                var FullMoonJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k + 0.5));
                var LastQuarterJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k + 0.75));
                var NextNewMoonJD = AASDynamicalTime.TT2UTC(AASMoonPhases.TruePhase(k + 1));

                var PrevNewMoon = GetDateFromJulian(PrevNewMoonJD);
                var PrevFirstQuarter = GetDateFromJulian(PrevFirstQuarterJD);
                var PrevFullMoon = GetDateFromJulian(PrevFullMoonJD);
                var PrevLastQuarter = GetDateFromJulian(PrevLastQuarterJD);
                var NewMoon = GetDateFromJulian(NewMoonJD);
                var FirstQuarter = GetDateFromJulian(FirstQuarterJD);
                var FullMoon = GetDateFromJulian(FullMoonJD);
                var LastQuarter = GetDateFromJulian(LastQuarterJD);
                var NextNewMoon = GetDateFromJulian(NextNewMoonJD);

                var Date2Phase = new Hashtable() {
                    { PrevFirstQuarter.Date, MP_FirstQuarter },
                    { PrevFullMoon.Date, MP_FullMoon },
                    { PrevLastQuarter.Date, MP_LastQuarter },
                    { NewMoon.Date, MP_NewMoon },
                    { FirstQuarter.Date, MP_FirstQuarter },
                    { FullMoon.Date, MP_FullMoon },
                    { LastQuarter.Date, MP_LastQuarter }
                };

                // setting up final moon phase name to display
                var msg = "";
                if (Date2Phase.ContainsKey(now.Date)) {
                    msg = Date2Phase[now.Date].ToString();
                }
                else {
                    if (PrevFirstQuarter.Date < now.Date && now.Date < PrevFullMoon.Date) msg = MP_WaxingGibbous;
                    else if (PrevFullMoon.Date < now.Date && now.Date < PrevLastQuarter.Date) msg = MP_WaningGibbous;
                    else if (PrevLastQuarter.Date < now.Date && now.Date < NewMoon.Date) msg = MP_WaningCrescent;
                    else if (NewMoon.Date < now.Date && now.Date < FirstQuarter.Date) msg = MP_WaxingCrescent;
                    else if (FirstQuarter.Date < now.Date && now.Date < FullMoon.Date) msg = MP_WaxingGibbous;
                    else if (FullMoon.Date < now.Date && now.Date < LastQuarter.Date) msg = MP_WaningGibbous;
                    else if (LastQuarter.Date < now.Date && now.Date < NextNewMoon.Date) msg = MP_WaningCrescent;
                    else msg = "N/A";
                }

                var SynodicPeriod = NewMoonJD - PrevNewMoonJD;
                var diff = TimeSpan.Zero;
                if (NewMoon <= now) diff = now - NewMoon;
                else diff = now - PrevNewMoon;

                //var LunarDay = (jd.Julian - PrevNewMoonJD) % SynodicPeriod;
                var rot = 180.0 * diff.TotalDays / SynodicPeriod;

                moon_dial.SetAttribute("transform", $"rotate({rot.ToString(CultureInfo.InvariantCulture)},525,1005.3931)");

                var svg3 = SVG.GetFromString(doc.OuterXml);
                iv1.SetImageDrawable(new PictureDrawable(svg3.RenderToPicture()));

                RunOnUiThread(() => {
                    tvMoonPhase.Text = $"Moon phase:          Day {diff.TotalDays:F2} - {msg}";
                    tvMoonIlm.Text = $"Moon illumination:   {(ilm * 100):F2}%";
                });
            };
            LunarTimer.Start();

            geocoder = new Geocoder(this, JLocale.Default);
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Android.Content.PM.Permission[] grantResults) {
            Platform.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }

        protected override void OnPause() {
            base.OnPause();
            lm?.RemoveUpdates(this);
        }

        protected override void OnResume() {
            base.OnResume();
            if (lm == null) return;

            if (lm.IsProviderEnabled(LocationManager.GpsProvider)) lm.RequestLocationUpdates(LocationManager.GpsProvider, 1000L, 1.0f, this);
            else if (lm.IsProviderEnabled(LocationManager.NetworkProvider)) lm.RequestLocationUpdates(LocationManager.NetworkProvider, 1000L, 1.0f, this);
            else Toast.MakeText(this, "Loactaion provider is disabled! Please enable it.", ToastLength.Short).Show();
        }

        protected override void OnDestroy() {
            base.OnDestroy();
            SideRealTimer?.Stop();
            LunarTimer?.Stop();
        }

        public async void OnLocationChanged(Android.Locations.Location location) {
            longitude = location.Longitude;
            latitude = location.Latitude;
            FindViewById<AppCompatTextView>(Resource.Id.tvLatitude).Text =  $"Latitude:            {FormatDMS(latitude, false)}";
            FindViewById<AppCompatTextView>(Resource.Id.tvLongitude).Text = $"Longitude:           {FormatDMS(longitude, true)}";

            var addrs = await geocoder.GetFromLocationAsync(latitude, longitude, 1);
            var addr0 = addrs[0];
            FindViewById<AppCompatTextView>(Resource.Id.tvLocation).Text = $"Location:            {addr0.Locality} {addr0.FeatureName}";
        }

        public void OnProviderDisabled(string provider) {
            //throw new NotImplementedException();
        }

        public void OnProviderEnabled(string provider) {
            //throw new NotImplementedException();
        }

        public void OnStatusChanged(string provider, [GeneratedEnum] Availability status, Bundle extras) {
            //throw new NotImplementedException();
        }

        private string FormatDMS(double th, bool b) {
            var timeSpan = TimeSpan.FromHours(Math.Abs(th));
            var text = $"{timeSpan.Days * 24 + timeSpan.Hours:D}°{timeSpan.Minutes:D2}′{timeSpan.Seconds:D2}″ ";
            if (b) {
                if (th >= 0.0) return $"{text}E";
                else return $"{text}W";
            }
            else {
                if (th >= 0.0) return $"{text}N";
                else return $"{text}S";
            }
        }

        private AAS2DCoordinate GetLunarCoord(double JD) {
            var jdMoon = AASDynamicalTime.UTC2TT(JD);
            var lambda = AASMoon.EclipticLongitude(jdMoon);
            var beta = AASMoon.EclipticLatitude(jdMoon);
            var epsilon = AASNutation.TrueObliquityOfEcliptic(jdMoon);
            return AASCoordinateTransformation.Ecliptic2Equatorial(lambda, beta, epsilon);
        }

        private AAS2DCoordinate GetSolarCoord(double JD) {
            var jdSun = AASDynamicalTime.UTC2TT(JD);
            var lambda = AASSun.ApparentEclipticLongitude(jdSun, false);
            var beta = AASSun.ApparentEclipticLatitude(jdSun, false);
            var epsilon = AASNutation.TrueObliquityOfEcliptic(jdSun);
            return AASCoordinateTransformation.Ecliptic2Equatorial(lambda, beta, epsilon);
        }

        private double GetMoonIllumination(double JD) {
            var MoonCoord = GetLunarCoord(JD);
            var SunCoord = GetSolarCoord(JD);
            var elongation = AASMoonIlluminatedFraction.GeocentricElongation(MoonCoord.X, MoonCoord.Y, SunCoord.X, SunCoord.Y);
            var phase_angle = AASMoonIlluminatedFraction.PhaseAngle(elongation, 368410.0, 149971520.0);
            return AASMoonIlluminatedFraction.IlluminatedFraction(phase_angle);
        }

        private AASDate GetJulianDay(DateTime date) {
            return new AASDate(date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second, true);
        }

        private DateTime GetDateFromJulian(double JD) {
            var jdn = new AASDate(JD, true);
            return new DateTime((int)jdn.Year, (int)jdn.Month, (int)jdn.Day, (int)jdn.Hour, (int)jdn.Minute, (int)jdn.Second, DateTimeKind.Utc);
        }
    }
}