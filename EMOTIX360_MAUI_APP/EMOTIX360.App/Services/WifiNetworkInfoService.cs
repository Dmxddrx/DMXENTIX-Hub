
using System.Runtime.Versioning;
using Microsoft.Maui.ApplicationModel;

#if ANDROID
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
#endif

namespace EMOTIX360.App.Services;

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
                "Location permission is required to read " +
                "the connected Wi-Fi network name."
            );
        }

        Context context = Android.App.Application.Context;

        // Android 12 and newer.
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return await GetCurrentSsidAndroid12Async(context);
        }

        // Android 11 and earlier.
        var wifiManager =
            (WifiManager?)context.GetSystemService(
                Context.WifiService
            );

        return NormalizeSsid(
            wifiManager?.ConnectionInfo?.SSID
        );

#else

        return null;

#endif
    }

#if ANDROID

    // --------------------------------------------------------
    // ANDROID 12+ WIFI SSID RETRIEVAL
    // --------------------------------------------------------

    [SupportedOSPlatform("android31.0")]
    private static async Task<string?> GetCurrentSsidAndroid12Async(
        Context context)
    {
        var connectivityManager =
            (ConnectivityManager?)context.GetSystemService(
                Context.ConnectivityService
            );

        if (connectivityManager is null)
            return null;

        var callback = new WifiSsidCallback();

        try
        {
            connectivityManager.RegisterDefaultNetworkCallback(
                callback
            );

            try
            {
                string? ssid = await callback.Result.WaitAsync(
                    TimeSpan.FromSeconds(5)
                );

                return NormalizeSsid(ssid);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
        finally
        {
            try
            {
                connectivityManager.UnregisterNetworkCallback(
                    callback
                );
            }
            catch
            {
                // Callback might already have been removed.
            }
        }
    }

    // --------------------------------------------------------
    // ANDROID WIFI NETWORK CALLBACK
    // --------------------------------------------------------

    [SupportedOSPlatform("android31.0")]
    private sealed class WifiSsidCallback
        : ConnectivityManager.NetworkCallback
    {
        private readonly TaskCompletionSource<string?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WifiSsidCallback()
            : base((int)NetworkCallbackFlags.IncludeLocationInfo)
        {
        }

        public Task<string?> Result => _completion.Task;

        public override void OnCapabilitiesChanged(
            Network network,
            NetworkCapabilities networkCapabilities)
        {
            if (networkCapabilities.HasTransport(
                    TransportType.Wifi) &&
                networkCapabilities.TransportInfo is WifiInfo wifiInfo)
            {
                _completion.TrySetResult(wifiInfo.SSID);
            }
            else
            {
                _completion.TrySetResult(null);
            }
        }

        public override void OnLost(Network network)
        {
            _completion.TrySetResult(null);
        }
    }

    // --------------------------------------------------------
    // NORMALIZE SSID
    // --------------------------------------------------------

    private static string? NormalizeSsid(string? ssid)
    {
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

        return string.IsNullOrEmpty(ssid) ? null : ssid;
    }

#endif
}
