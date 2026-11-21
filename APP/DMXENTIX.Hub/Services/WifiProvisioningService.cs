using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DMXENTIX.Hub.Models;
using DMXENTIX.Hub.Services.Bluetooth;

namespace DMXENTIX.Hub.Services
{
    public sealed class WifiProvisioningService : IDisposable
    {
        private const int MaximumFrameLength = 20;
        private const int MaximumChunkLength = 12;

        private const int MaximumSsidBytes = 32;
        private const int MaximumPasswordBytes = 63;

        private IDeviceSession? _session;

        private static readonly UTF8Encoding StrictUtf8 =
            new(false, true);

        public WifiProvisioningService()
        {
        }

        // --------------------------------------------------------
        // SELECT DEVICE SESSION
        // --------------------------------------------------------

        public void SetSession(IDeviceSession? session)
        {
            if (ReferenceEquals(_session, session))
                return;

            // Remove subscriptions from the previous session.
            if (_session is not null)
            {
                _session.ProvisioningStatusPayloadReceived -=
                    OnWifiProvisioningStatusReceived;

                _session.StateChanged -=
                    OnSessionStateChanged;
            }

            _session = session;

            // Do not retain another device's provisioning status.
            CurrentStatus = null;
            StatusCleared?.Invoke();

            if (_session is null)
                return;

            // Subscribe directly to the selected session.
            _session.ProvisioningStatusPayloadReceived +=
                OnWifiProvisioningStatusReceived;

            _session.StateChanged +=
                OnSessionStateChanged;

            // Recover the latest status if this session was
            // already connected before it was selected.
            if (_session.IsConnected &&
                !string.IsNullOrWhiteSpace(
                    _session.LastProvisioningStatusPayload))
            {
                ProcessProvisioningStatus(
                    _session.LastProvisioningStatusPayload
                );
            }
        }

        public WifiProvisioningStatus? CurrentStatus { get; private set; }

        public event Action<WifiProvisioningStatus>? StatusUpdated;

        public event Action? StatusCleared;

        // --------------------------------------------------------
        // SEND WIFI CREDENTIALS
        // --------------------------------------------------------

        public async Task SendCredentialsAsync(
            IDeviceSession session,
            string ssid,
            string password,
            CancellationToken cancellationToken = default)
        {
            EnsureSelectedConnectedSession(session);

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
                session,
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
                    session,
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
                    session,
                    $"P,{i}/{passwordChunks.Length},{passwordChunks[i]}",
                    cancellationToken
                );
            }

            // ----------------------------------------------------
            // COMMIT TRANSACTION
            // ----------------------------------------------------

            await SendFrameAsync(
                session,
                "COMMIT",
                cancellationToken
            );
        }

        // --------------------------------------------------------
        // CLEAR STORED APP-SENT NETWORKS
        // --------------------------------------------------------

        public async Task ClearStoredNetworksAsync(
            IDeviceSession session,
            CancellationToken cancellationToken = default)
        {
            EnsureSelectedConnectedSession(session);

            await SendFrameAsync(
                session,
                "CLEAR",
                cancellationToken
            );
        }

        // --------------------------------------------------------
        // VALIDATE SELECTED SESSION
        // --------------------------------------------------------

        private void EnsureSelectedConnectedSession(
            IDeviceSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            if (!session.IsConnected)
            {
                throw new InvalidOperationException(
                    "Connect to EMOTIX360 before sending Wi-Fi credentials."
                );
            }

            if (!ReferenceEquals(session, _session))
            {
                throw new InvalidOperationException(
                    "The selected device session has changed. " +
                    "Select the device again before provisioning Wi-Fi."
                );
            }
        }

        // --------------------------------------------------------
        // SEND ONE BLE FRAME
        // --------------------------------------------------------

        private async Task SendFrameAsync(
            IDeviceSession session,
            string frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check the selected session again before every frame.
            EnsureSelectedConnectedSession(session);

            if (Encoding.ASCII.GetByteCount(frame) > MaximumFrameLength)
            {
                throw new InvalidOperationException(
                    $"Provisioning frame exceeds {MaximumFrameLength} bytes."
                );
            }

            await session.WriteWifiProvisioningFrameAsync(
                frame,
                cancellationToken
            );
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

        private void OnWifiProvisioningStatusReceived(
            IDeviceSession session,
            string payload)
        {
            // Ignore notifications from sessions that are no longer selected.
            if (!ReferenceEquals(session, _session))
                return;

            ProcessProvisioningStatus(payload);
        }

        private void ProcessProvisioningStatus(string payload)
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
        // HANDLE SESSION CONNECTION CHANGES
        // --------------------------------------------------------

        private void OnSessionStateChanged(IDeviceSession session)
        {
            if (!ReferenceEquals(session, _session))
                return;

            if (!session.IsConnected)
            {
                CurrentStatus = null;
                StatusCleared?.Invoke();

                return;
            }

            // Restore the latest status after reconnection.
            if (!string.IsNullOrWhiteSpace(
                    session.LastProvisioningStatusPayload))
            {
                ProcessProvisioningStatus(
                    session.LastProvisioningStatusPayload
                );
            }
        }

        // --------------------------------------------------------
        // DISPOSE
        // --------------------------------------------------------

        public void Dispose()
        {
            if (_session is not null)
            {
                _session.ProvisioningStatusPayloadReceived -=
                    OnWifiProvisioningStatusReceived;

                _session.StateChanged -=
                    OnSessionStateChanged;

                _session = null;
            }
        }
    }
}