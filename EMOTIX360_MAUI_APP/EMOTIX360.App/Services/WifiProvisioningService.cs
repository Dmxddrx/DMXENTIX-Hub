using System;
using System.Collections.Generic;
using System.Text;
using EMOTIX360.App.Models;

namespace EMOTIX360.App.Services
{
    public sealed class WifiProvisioningService
    {
        private const int MaximumFrameLength = 20;
        private const int MaximumChunkLength = 12;

        private const int MaximumSsidBytes = 32;
        private const int MaximumPasswordBytes = 63;

        private readonly BluetoothService _bluetooth;

        private static readonly UTF8Encoding StrictUtf8 =
            new(false, true);

        public WifiProvisioningService(BluetoothService bluetooth)
        {
            _bluetooth = bluetooth;

            _bluetooth.WifiProvisioningStatusPayloadReceived +=
                OnWifiProvisioningStatusReceived;

            _bluetooth.ConnectionChanged +=
                OnBluetoothConnectionChanged;

            // Recover the latest status if BLE connected before
            // this service was created.
            if (_bluetooth.CurrentWifiProvisioningStatusPayload is string payload)
            {
                OnWifiProvisioningStatusReceived(payload);
            }
        }

        public WifiProvisioningStatus? CurrentStatus { get; private set; }

        public event Action<WifiProvisioningStatus>? StatusUpdated;

        // --------------------------------------------------------
        // SEND WIFI CREDENTIALS
        // --------------------------------------------------------

        public async Task SendCredentialsAsync(
            string ssid,
            string password,
            CancellationToken cancellationToken = default)
        {
            if (!_bluetooth.IsConnected)
            {
                throw new InvalidOperationException(
                    "Connect to EMOTIX360 before sending Wi-Fi credentials."
                );
            }

            if (string.IsNullOrEmpty(ssid))
            {
                throw new ArgumentException(
                    "Wi-Fi network name cannot be empty.",
                    nameof(ssid)
                );
            }

            if (ssid.Contains('\0') || password.Contains('\0'))
            {
                throw new ArgumentException(
                    "SSID and password cannot contain null characters."
                );
            }

            byte[] ssidBytes = StrictUtf8.GetBytes(ssid);
            byte[] passwordBytes = StrictUtf8.GetBytes(password);

            if (ssidBytes.Length > MaximumSsidBytes)
            {
                throw new ArgumentException(
                    "The SSID must be no longer than 32 UTF-8 bytes."
                );
            }

            if (passwordBytes.Length > MaximumPasswordBytes ||
                (passwordBytes.Length != 0 && passwordBytes.Length < 8))
            {
                throw new ArgumentException(
                    "The password must be empty for an open network " +
                    "or between 8 and 63 UTF-8 bytes."
                );
            }

            string encodedSsid =
                Convert.ToBase64String(ssidBytes);

            string encodedPassword =
                Convert.ToBase64String(passwordBytes);

            string[] ssidChunks = SplitBase64(encodedSsid);
            string[] passwordChunks = SplitBase64(encodedPassword);

            // ----------------------------------------------------
            // BEGIN TRANSACTION
            // ----------------------------------------------------

            await SendFrameAsync(
                $"B,{ssidBytes.Length},{passwordBytes.Length}," +
                $"{ssidChunks.Length},{passwordChunks.Length}",
                cancellationToken
            );

            // ----------------------------------------------------
            // SEND SSID CHUNKS
            // ----------------------------------------------------

            for (int i = 0; i < ssidChunks.Length; i++)
            {
                await SendFrameAsync(
                    $"S,{i}/{ssidChunks.Length},{ssidChunks[i]}",
                    cancellationToken
                );
            }

            // ----------------------------------------------------
            // SEND PASSWORD CHUNKS
            // ----------------------------------------------------

            for (int i = 0; i < passwordChunks.Length; i++)
            {
                await SendFrameAsync(
                    $"P,{i}/{passwordChunks.Length},{passwordChunks[i]}",
                    cancellationToken
                );
            }

            // ----------------------------------------------------
            // COMMIT TRANSACTION
            // ----------------------------------------------------

            await SendFrameAsync(
                "COMMIT",
                cancellationToken
            );
        }

        // --------------------------------------------------------
        // CLEAR STORED APP-SENT NETWORKS
        // --------------------------------------------------------

        public async Task ClearStoredNetworksAsync(
            CancellationToken cancellationToken = default)
        {
            if (!_bluetooth.IsConnected)
            {
                throw new InvalidOperationException(
                    "Connect to EMOTIX360 before clearing stored networks."
                );
            }

            await SendFrameAsync(
                "CLEAR",
                cancellationToken
            );
        }

        // --------------------------------------------------------
        // SEND ONE BLE FRAME
        // --------------------------------------------------------

        private async Task SendFrameAsync(
            string frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Encoding.ASCII.GetByteCount(frame) > MaximumFrameLength)
            {
                throw new InvalidOperationException(
                    $"Provisioning frame exceeds {MaximumFrameLength} bytes."
                );
            }

            await _bluetooth.WriteWifiProvisioningFrameAsync(frame);
        }

        // --------------------------------------------------------
        // SPLIT BASE64 INTO FIRMWARE CHUNK SIZE
        // --------------------------------------------------------

        private static string[] SplitBase64(string encoded)
        {
            if (encoded.Length == 0)
                return [];

            int chunkCount =
                (encoded.Length + MaximumChunkLength - 1) /
                MaximumChunkLength;

            string[] chunks = new string[chunkCount];

            for (int i = 0; i < chunkCount; i++)
            {
                int start = i * MaximumChunkLength;

                int length = Math.Min(
                    MaximumChunkLength,
                    encoded.Length - start
                );

                chunks[i] = encoded.Substring(start, length);
            }

            return chunks;
        }

        // --------------------------------------------------------
        // HANDLE DEVICE STATUS NOTIFICATIONS
        // --------------------------------------------------------

        private void OnWifiProvisioningStatusReceived(string payload)
        {
            if (!WifiProvisioningStatus.TryParse(
                    payload,
                    out WifiProvisioningStatus status))
            {
                return;
            }

            CurrentStatus = status;

            StatusUpdated?.Invoke(status);
        }
        // --------------------------------------------------------
        // HANDLE BLE CONNECTION CHANGES
        // --------------------------------------------------------

        private void OnBluetoothConnectionChanged(
            bool connected,
            string? detail)
        {
            if (connected)
                return;

            // Do not retain a previous device's provisioning status.
            CurrentStatus = null;
        }
    }
}
