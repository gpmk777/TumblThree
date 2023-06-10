﻿using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.DirectoryServices;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Waf.Applications;

using TumblThree.Applications.DataModels;
using Newtonsoft.Json;
using TumblThree.Applications.Extensions;
using TumblThree.Domain;

namespace TumblThree.Applications.Services
{
    [Export(typeof(ILogService))]
    public class LogService : ILogService
    {
        private readonly IWebRequestFactory _webRequestFactory;
        private readonly IShellService _shellService;

        private static string _windowsVersion;
        private static string _windowsVersionNumber;
        private static bool? _isWindowsServerVersion;
        private static string _windowsEdition;
        private static string _windowsReleaseId;
        private static string _windowsRegionLanguage;
        private static string _windowsRegionCountry;

        private static string _defaultBrowser;
        private static string _defaultBrowserVersion;

        [ImportingConstructor]
        public LogService(IShellService shellService, IWebRequestFactory webRequestFactory)
        {
            _shellService = shellService;
            _webRequestFactory = webRequestFactory;
        }

        public string TumblThreeVersionString => $"TumblThree {TumblThreeVersion} ({TumblThreeBitness}-bit)";

        public string WindowsVersionString => $"{WindowsVersion} {WindowsEdition} {WindowsBitness}-bit, version {WindowsReleaseId} ({WindowsVersionNumber})";

        public string DefaultBrowserString => $"{DefaultBrowser} ({DefaultBrowserVersion})";

        public string RegionSettingsString => $"{WindowsRegionLanguage}, {WindowsRegionCountry}";

        public string NetFrameworkVersionString => $"{NetFrameworkVersion} {NetFrameworkBitness} Bit";

        public string NetVersionSupportString => NetVersionSupport;

        public async Task SendErrorDetails(Exception ex, bool terminating)
        {
            var log = new LogException(ex, _shellService.IsLongPathSupported, terminating,
                WindowsVersion, WindowsEdition, WindowsBitness, WindowsReleaseId, WindowsVersionNumber,
                TumblThreeVersion, TumblThreeBitness,
                DefaultBrowser, DefaultBrowserVersion,
                WindowsRegionLanguage, WindowsRegionCountry,
                NetFrameworkVersion, NetFrameworkBitness, NetVersionSupport,
                DateTime.UtcNow);

            var data = JsonConvert.SerializeObject(log);

            await SendLogData(data);
        }

        public async Task SendLogData()
        {
            var log = new LogData(_shellService.IsLongPathSupported,
                WindowsVersion, WindowsEdition, WindowsBitness, WindowsReleaseId, WindowsVersionNumber,
                TumblThreeVersion, TumblThreeBitness,
                DefaultBrowser, DefaultBrowserVersion,
                WindowsRegionLanguage, WindowsRegionCountry,
                NetFrameworkVersion, NetFrameworkBitness, NetVersionSupport,
                MachHash, UsrHash, DateTime.UtcNow);

            var data = JsonConvert.SerializeObject(log);

            await SendLogData(data);
        }

        private async Task SendLogData(string s)
        {
            try
            {
                const string u = "aHR0cHM6Ly9lbjNrNWQydGx4NGN5OXEubS5waXBlZHJlYW0ubmV0";
                var d = Encoding.UTF8.GetString(Convert.FromBase64String(u));
                var request = _webRequestFactory.CreatePostRequest(d);
                request.ContentType = "application/json; charset=UTF-8";
                await _webRequestFactory.PerformPostXHRRequestAsync(request, s, true);
                using (var response = await request.GetResponseAsync() as HttpWebResponse)
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new ApplicationException(string.Format("endpoint returned: {0} {1}", response.StatusCode, response.StatusDescription));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("LogService:SendLogData: {0}", ex);
            }
        }

        private static string WindowsVersion
        {
            get
            {
                if (_windowsVersion != null) return _windowsVersion;

                OperatingSystem os = Environment.OSVersion;
                Version vs = os.Version;
                string operatingSystem = "";

                if (os.Platform == PlatformID.Win32NT)
                {
                    switch (vs.Major)
                    {
                        case 6:
                            if (vs.Minor == 1)
                                operatingSystem = "7";
                            else if (vs.Minor == 2)
                                operatingSystem = "8";
                            else
                                operatingSystem = "8.1";
                            break;
                        case 10:
                            if (IsWindowsServerVersion)
                            {
                                operatingSystem = "Server" + (vs.Build == 14393 ? " 2016" : vs.Build == 17763 ? " 2019" : vs.Build == 20348 ? " 2022" : "");
                            }
                            else
                            {
                                operatingSystem = (vs.Build < 21996) ? "10" : "11";
                            }
                            break;
                    }
                }

                if (operatingSystem.Length != 0)
                {
                    operatingSystem = "Windows " + operatingSystem;
                    if (os.ServicePack.Length != 0)
                    {
                        operatingSystem += " " + os.ServicePack;
                    }
                }
                _windowsVersion = operatingSystem;

                return operatingSystem;
            }
        }

        private static string WindowsVersionNumber
        {
            get
            {
                if (_windowsVersionNumber != null) return _windowsVersionNumber;

                DetermineVersionNumberAndServerVersion();

                return _windowsVersionNumber;
            }
        }

        private static bool IsWindowsServerVersion
        {
            get
            {
                if (_isWindowsServerVersion.HasValue) return _isWindowsServerVersion.Value;

                DetermineVersionNumberAndServerVersion();

                return _isWindowsServerVersion.Value;
            }
        }

        private static void DetermineVersionNumberAndServerVersion()
        {
            var osVersionInfo = new NativeMethods.OSVERSIONINFOEX { OSVersionInfoSize = Marshal.SizeOf(typeof(NativeMethods.OSVERSIONINFOEX)) };
            if (NativeMethods.RtlGetVersion(ref osVersionInfo) != 0)
            {
                // TODO: Error handling
            }

            int ubr = 0;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
            {
                if (key != null)
                {
                    object o = key.GetValue("UBR");
                    if (o is int i)
                    {
                        ubr = i;
                    }
                }
            }
            _windowsVersionNumber = $"{osVersionInfo.MajorVersion}.{osVersionInfo.MinorVersion}.{osVersionInfo.BuildNumber}.{ubr}";
            _isWindowsServerVersion = (osVersionInfo.ProductType & 2) != 0;
        }

        private static string WindowsEdition
        {
            get
            {
                if (_windowsEdition != null) return _windowsEdition;

                ComputerInfo computerInfo = new ComputerInfo();
                var s = computerInfo.OSFullName;

                var a = s.Replace(WindowsVersion, "|").Split('|');
                _windowsEdition = ((string)a.GetValue(a.Length - 1)).Trim();

                return _windowsEdition;
            }
        }

        private static string WindowsBitness => (string)(Environment.Is64BitOperatingSystem ? "64" : "32");

        private static string WindowsReleaseId
        {
            get
            {
                if (_windowsReleaseId != null) return _windowsReleaseId;

                string id = "";
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    if (key != null)
                    {
                        object o = key.GetValue("DisplayVersion");
                        if (o is string dv)
                        {
                            id = dv;
                        }
                        if (string.IsNullOrEmpty(id))
                        {
                            o = key.GetValue("ReleaseID");
                            if (o is string rid)
                            {
                                id = rid;
                            }
                        }
                    }
                }
                _windowsReleaseId = id;

                return id;
            }
        }

        private static string WindowsRegionLanguage
        {
            get
            {
                if (_windowsRegionLanguage != null) return _windowsRegionLanguage;

                using (var regKeyGeoId = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", false))
                {
                    var geoID = (string)regKeyGeoId.GetValue("Nation");
                    var allRegions = CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(x => new RegionInfo(x.ToString()));
                    var regionInfo = allRegions.FirstOrDefault(r => r.GeoId == Int32.Parse(geoID));
                    _windowsRegionLanguage = regionInfo.Name;
                    _windowsRegionCountry = regionInfo.TwoLetterISORegionName;
                }

                return _windowsRegionLanguage;
            }
        }

        private static string WindowsRegionCountry
        {
            get
            {
                if (_windowsRegionCountry != null) return _windowsRegionCountry;

                using (var regKeyGeoId = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", false))
                {
                    var geoID = (string)regKeyGeoId.GetValue("Nation");
                    var allRegions = CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(x => new RegionInfo(x.ToString()));
                    var regionInfo = allRegions.FirstOrDefault(r => r.GeoId == Int32.Parse(geoID));
                    _windowsRegionCountry = regionInfo.TwoLetterISORegionName;
                    _windowsRegionLanguage = regionInfo.Name;
                }

                return _windowsRegionCountry;
            }
        }

        private static string NetFrameworkVersion => Environment.Version.ToString();

        private static string NetFrameworkBitness => (string)(Environment.Is64BitProcess ? "64" : "32");

        private static string TumblThreeVersion => ApplicationInfo.Version;

        private static string TumblThreeBitness => (string)(Environment.Is64BitProcess ? "64" : "32");

        private static string MachHash
        {
            get
            {
                return GetComputerSid().Value.ToHash();
            }
        }

        private static string UsrHash => $"{Environment.UserDomainName}\\{Environment.UserName}".ToHash();

        private static string DefaultBrowser
        {
            get
            {
                if (_defaultBrowser != null) return _defaultBrowser;

                GetBrowserInfo();

                return _defaultBrowser;
            }
        }

        private static string DefaultBrowserVersion
        {
            get
            {
                if (_defaultBrowserVersion != null) return _defaultBrowserVersion;

                GetBrowserInfo();

                return _defaultBrowserVersion;
            }
        }

        private static SecurityIdentifier GetComputerSid()
        {
            using (var entry = new DirectoryEntry(string.Format("WinNT://{0},Computer", Environment.MachineName)))
            {
                return new SecurityIdentifier((byte[])entry.Children.Cast<DirectoryEntry>().First().InvokeGet("objectSID"), 0).AccountDomainSid;
            }
        }

        private static void GetBrowserInfo()
        {
            string name = string.Empty;

            using (RegistryKey regDefault = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.htm\\UserChoice", false))
            {
                var stringDefault = regDefault.GetValue("ProgId");

                using (RegistryKey regKey = Registry.ClassesRoot.OpenSubKey(stringDefault + "\\shell\\open\\command", false))
                {
                    name = regKey.GetValue(null).ToString().ToLower().Replace("" + (char)34, "");
                }
            }
            if (!name.EndsWith("exe"))
                name = name.Substring(0, name.LastIndexOf(".exe") + 4);

            var versionInfo = FileVersionInfo.GetVersionInfo(name);
            _defaultBrowser = versionInfo.ProductName;
            _defaultBrowserVersion = versionInfo.ProductVersion;
        }

        private static string NetVersionSupport
        {
            get
            {
                try
                {
                    using (Process process = new Process())
                    {
                        process.StartInfo.FileName = "dotnet.exe";
                        process.StartInfo.Arguments = "--info";
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.RedirectStandardOutput = true;
                        process.StartInfo.CreateNoWindow = true;
                        process.Start();

                        string version = process.StandardOutput.ReadToEnd()?.TrimEnd();

                        Match match = Regex.Match(version, @"Host.*:?Version: ([\d.]+)", RegexOptions.Singleline);

                        var list = new List<string>();
                        if (match.Success)
                        {
                            var ver = new Version(match.Groups[1].Value);
                            if (ver >= new Version("5.0"))
                            {
                                list.Add("5");
                            }
                            if (ver >= new Version("6.0"))
                            {
                                list.Add("6");
                            }
                        }

                        process.WaitForExit(3000);

                        return string.Join(" / ", list);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("LogService.NetVersionSupport: {0}", ex);
                    return "n/a";
                }
            }
        }
    }
}
