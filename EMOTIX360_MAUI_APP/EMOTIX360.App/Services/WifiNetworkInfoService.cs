using System;
using System.Collections.Generic;
using System.Text;

using Microsoft.Maui.ApplicationModel;

#if ANDROID
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
#endif

namespace EMOTIX360.App.Services
{
    public sealed class WifiNetworkInfoService
    {
        public async Task<string?> GetCurrentSsidAsync()
        {
#if ANDROID

            // Android requires location permission to expose
            // the location-sensitive SSID field.
            PermissionStatus permission =
                await Permissions.CheckStatusAsync<
                    Permissions.LocationWhenInUse>();

            if (permission != PermissionStatus.Granted)
            {
                permission =
                    await Permissions.RequestAsync<
                        Permissions.LocationWhenInUse>();
            }

            if (permission != PermissionStatus.Granted)
            {
                throw new UnauthorizedAccessException(
                    "Location permission is required by Android " +
                    "to read the connected Wi-Fi network name."
                );
            }

            Context context = Android.App.Application.Context;

            WifiInfo? wifiInfo = null;

            // Android 12 and newer: retrieve WifiInfo through
            // ConnectivityManager and NetworkCapabilities.
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var connectivityManager =
                    (ConnectivityManager?)context.GetSystemService(
                        Context.ConnectivityService
                    );

                Network? activeNetwork =
                    connectivityManager?.ActiveNetwork;

                NetworkCapabilities? capabilities =
                    activeNetwork is null
                        ? null
                        : connectivityManager?.GetNetworkCapabilities(
                            activeNetwork
                        );

                if (capabilities?.HasTransport(
                        TransportType.Wifi) == true)
                {
                    wifiInfo =
                        capabilities.TransportInfo as WifiInfo;
                }
            }

            // Fallback for older Android versions or devices
            // that do not provide WifiInfo through TransportInfo.
            if (wifiInfo is null)
            {
                var wifiManager =
                    (WifiManager?)context.GetSystemService(
                        Context.WifiService
                    );

                wifiInfo = wifiManager?.ConnectionInfo;
            }

            string? ssid = wifiInfo?.SSID;

            if (string.IsNullOrWhiteSpace(ssid) ||
                string.Equals(
                    ssid,
                    "<unknown ssid>",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Android returns UTF-8 SSIDs surrounded by quotes.
            if (ssid.Length >= 2 &&
                ssid[0] == '"' &&
                ssid[^1] == '"')
            {
                ssid = ssid[1..^1];
            }

            return ssid;

#else

        // Current-network SSID reading is implemented for Android.
        return null;

#endif
        }
    }
}
