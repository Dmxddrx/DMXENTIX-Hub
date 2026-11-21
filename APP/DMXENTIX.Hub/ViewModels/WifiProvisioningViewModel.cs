using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;
using DMXENTIX.Hub.Models;
using DMXENTIX.Hub.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using DMXENTIX.Hub.Services.Bluetooth;

namespace DMXENTIX.Hub.ViewModels
{
    public sealed class WifiProvisioningViewModel : ObservableObject
    {
        private readonly DeviceSessionSelectionService _sessionSelection;
        private readonly WifiProvisioningService _provisioning;
        private readonly WifiNetworkInfoService _phoneWifi;

        private IDeviceSession? _session;

        private string _ssid = string.Empty;
        private string _password = string.Empty;

        private string _currentPhoneSsidText =
            "Not loaded";

        private string? _currentPhoneSsid;

        private string _statusMessage =
            "Connect to EMOTIX360 to configure Wi-Fi.";

        private int _storedNetworkCount;

        private bool _hasDeviceStatus;
        private bool _isConnected;
        private bool _isRefreshingPhoneSsid;
        private bool _isBusy;

        private bool _waitingForDeviceResult;

        public WifiProvisioningViewModel(
            DeviceSessionSelectionService sessionSelection,
            WifiProvisioningService provisioning,
            WifiNetworkInfoService phoneWifi)
        {
            _sessionSelection = sessionSelection;
            _provisioning = provisioning;
            _phoneWifi = phoneWifi;

            RefreshPhoneSsidCommand = new Command(
                async () => await RefreshPhoneSsidAsync(),
                () => !IsRefreshingPhoneSsid
            );

            UsePhoneSsidCommand = new Command(
                UsePhoneSsid,
                () => CanUsePhoneSsid
            );

            SendCredentialsCommand = new Command(
                async () => await SendCredentialsAsync(),
                () => CanSendCredentials
            );

            ClearStoredNetworksCommand = new Command(
                async () => await ClearStoredNetworksAsync(),
                () => CanClearStoredNetworks
            );

            _sessionSelection.SelectedSessionChanged +=
                OnSelectedSessionChanged;

            _provisioning.StatusUpdated += OnStatusUpdated;
            _provisioning.StatusCleared += OnStatusCleared;

            // Restore the currently selected session.
            OnSelectedSessionChanged(
                _sessionSelection.SelectedSession
            );
        }

        // ========================================================
        // SELECTED DEVICE SESSION
        // ========================================================

        private void OnSelectedSessionChanged(IDeviceSession? session)
        {
            // Remove the previous session subscription.
            if (_session is not null)
            {
                _session.StateChanged -= OnSessionStateChanged;
            }

            _session = session;

            // Subscribe to the newly selected session.
            if (_session is not null)
            {
                _session.StateChanged += OnSessionStateChanged;
            }

            // Tell the provisioning service which device to monitor.
            _provisioning.SetSession(session);

            MainThread.BeginInvokeOnMainThread(
                UpdateConnectionState
            );
        }

        // ========================================================
        // SESSION CONNECTION STATE
        // ========================================================

        private void OnSessionStateChanged(IDeviceSession session)
        {
            if (!ReferenceEquals(session, _session))
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!ReferenceEquals(session, _session))
                    return;

                UpdateConnectionState();
            });
        }

        // ========================================================
        // UPDATE CONNECTION STATE
        // ========================================================

        private void UpdateConnectionState()
        {
            bool connected = _session?.IsConnected == true;

            IsConnected = connected;

            if (!connected)
            {
                _waitingForDeviceResult = false;

                HasDeviceStatus = false;
                StoredNetworkCount = 0;

                StatusMessage =
                    "Connect to EMOTIX360 to configure Wi-Fi.";

                return;
            }

            if (_provisioning.CurrentStatus is not null)
            {
                ApplyDeviceStatus(_provisioning.CurrentStatus);
            }
            else
            {
                StatusMessage =
                    "Connected. Reading device Wi-Fi status...";
            }
        }

        // --------------------------------------------------------
        // COMMANDS
        // --------------------------------------------------------

        public Command RefreshPhoneSsidCommand { get; }

        public Command UsePhoneSsidCommand { get; }

        public Command SendCredentialsCommand { get; }

        public Command ClearStoredNetworksCommand { get; }

        // --------------------------------------------------------
        // INPUT PROPERTIES
        // --------------------------------------------------------

        public string Ssid
        {
            get => _ssid;
            set
            {
                if (SetProperty(ref _ssid, value))
                    RefreshCommandStates();
            }
        }

        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
        }

        // --------------------------------------------------------
        // PHONE WIFI INFORMATION
        // --------------------------------------------------------

        public string CurrentPhoneSsidText
        {
            get => _currentPhoneSsidText;
            private set => SetProperty(
                ref _currentPhoneSsidText,
                value
            );
        }

        public bool IsRefreshingPhoneSsid
        {
            get => _isRefreshingPhoneSsid;
            private set
            {
                if (SetProperty(ref _isRefreshingPhoneSsid, value))
                {
                    OnPropertyChanged(nameof(CanUsePhoneSsid));

                    RefreshPhoneSsidCommand.ChangeCanExecute();
                    UsePhoneSsidCommand.ChangeCanExecute();
                }
            }
        }

        public bool CanUsePhoneSsid =>
            !IsRefreshingPhoneSsid &&
            !string.IsNullOrEmpty(_currentPhoneSsid);

        // --------------------------------------------------------
        // DEVICE CONNECTION
        // --------------------------------------------------------

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (SetProperty(ref _isConnected, value))
                {
                    OnPropertyChanged(nameof(ConnectionText));
                    RefreshCommandStates();
                }
            }
        }

        public string ConnectionText =>
            IsConnected ? "Connected" : "Not connected";

        // --------------------------------------------------------
        // DEVICE NETWORK STORAGE
        // --------------------------------------------------------

        public int StoredNetworkCount
        {
            get => _storedNetworkCount;
            private set
            {
                if (SetProperty(ref _storedNetworkCount, value))
                {
                    OnPropertyChanged(nameof(StoredNetworkCountText));
                    RefreshCommandStates();
                }
            }
        }

        public string StoredNetworkCountText =>
            HasDeviceStatus
                ? $"{StoredNetworkCount} / {WifiProvisioningStatus.MaximumStoredNetworks}"
                : "Not yet reported";

        public bool HasDeviceStatus
        {
            get => _hasDeviceStatus;
            private set
            {
                if (SetProperty(ref _hasDeviceStatus, value))
                    OnPropertyChanged(nameof(StoredNetworkCountText));
            }
        }

        // --------------------------------------------------------
        // OPERATION STATUS
        // --------------------------------------------------------

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    RefreshCommandStates();
            }
        }

        public bool CanSendCredentials =>
            IsConnected &&
            !IsBusy &&
            !string.IsNullOrEmpty(Ssid);

        public bool CanClearStoredNetworks =>
            IsConnected &&
            !IsBusy &&
            HasDeviceStatus &&
            StoredNetworkCount > 0;

        // ========================================================
        // REFRESH CURRENT PHONE WIFI
        // ========================================================

        private async Task RefreshPhoneSsidAsync()
        {
            if (IsRefreshingPhoneSsid)
                return;

            IsRefreshingPhoneSsid = true;

            _currentPhoneSsid = null;

            CurrentPhoneSsidText = "Reading current network...";

            try
            {
                string? ssid =
                    await _phoneWifi.GetCurrentSsidAsync();

                _currentPhoneSsid = ssid;

                CurrentPhoneSsidText =
                    string.IsNullOrEmpty(ssid)
                        ? "Unavailable. Check Wi-Fi and Android location access."
                        : ssid;
            }
            catch (UnauthorizedAccessException)
            {
                CurrentPhoneSsidText =
                    "Location permission was not granted.";
            }
            catch (Exception ex)
            {
                CurrentPhoneSsidText =
                    $"Could not read network: {ex.Message}";
            }
            finally
            {
                IsRefreshingPhoneSsid = false;
                RefreshCommandStates();
            }
        }

        // ========================================================
        // USE PHONE WIFI SSID
        // ========================================================

        private void UsePhoneSsid()
        {
            if (!CanUsePhoneSsid ||
                string.IsNullOrEmpty(_currentPhoneSsid))
            {
                return;
            }

            Ssid = _currentPhoneSsid;
        }

        // ========================================================
        // SEND WIFI CREDENTIALS
        // ========================================================

        private async Task SendCredentialsAsync()
        {
            IDeviceSession? session = _session;

            if (!CanSendCredentials ||
                session is null ||
                !session.IsConnected)
            {
                return;
            }

            IsBusy = true;

            _waitingForDeviceResult = true;

            StatusMessage =
                "Sending Wi-Fi credentials to EMOTIX360...";

            try
            {
                await _provisioning.SendCredentialsAsync(
                    session,
                    Ssid,
                    Password
                );

                // A status notification may already have arrived
                // while the BLE frames were being transmitted.
                if (_waitingForDeviceResult)
                {
                    StatusMessage =
                        "Credentials transmitted. Waiting for device confirmation...";
                }
            }
            catch (Exception ex)
            {
                _waitingForDeviceResult = false;

                StatusMessage =
                    $"Wi-Fi provisioning failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ========================================================
        // CLEAR STORED NETWORKS
        // ========================================================

        private async Task ClearStoredNetworksAsync()
        {
            IDeviceSession? session = _session;

            if (!CanClearStoredNetworks ||
                session is null ||
                !session.IsConnected)
            {
                return;
            }

            Shell? shell = Shell.Current;

            if (shell is null)
                return;

            bool confirmed = await shell.DisplayAlertAsync(
                "Clear saved Wi-Fi networks?",
                $"This will remove all {StoredNetworkCount} app-sent " +
                "Wi-Fi networks saved on EMOTIX360.\n\n" +
                "The hardcoded fallback Wi-Fi credentials in the " +
                "firmware will NOT be deleted.\n\n" +
                "Do you want to continue?",
                "Clear networks",
                "Cancel"
            );

            if (!confirmed)
                return;

            IsBusy = true;

            _waitingForDeviceResult = true;

            StatusMessage =
                "Sending clear request to EMOTIX360...";

            try
            {
                await _provisioning.ClearStoredNetworksAsync(session);

                if (_waitingForDeviceResult)
                {
                    StatusMessage =
                        "Clear request sent. Waiting for device confirmation...";
                }
            }
            catch (Exception ex)
            {
                _waitingForDeviceResult = false;

                StatusMessage =
                    $"Could not clear stored networks: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ========================================================
        // HANDLE DEVICE STATUS
        // ========================================================

        private void OnStatusCleared()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _waitingForDeviceResult = false;

                HasDeviceStatus = false;
                StoredNetworkCount = 0;

                StatusMessage = IsConnected
                    ? "Connected. Reading device Wi-Fi status..."
                    : "Connect to EMOTIX360 to configure Wi-Fi.";
            });
        }

        private void OnStatusUpdated(WifiProvisioningStatus status)
        {
            MainThread.BeginInvokeOnMainThread(
                () => ApplyDeviceStatus(status)
            );
        }

        private void ApplyDeviceStatus(WifiProvisioningStatus status)
        {
            HasDeviceStatus = true;

            StoredNetworkCount = status.StoredCount;

            switch (status.State)
            {
                case "IDLE":

                    StatusMessage =
                        "Wi-Fi provisioning is ready.";

                    break;

                case "RECEIVING":

                    StatusMessage =
                        "EMOTIX360 is receiving Wi-Fi credentials...";

                    break;

                case "SAVED":

                    _waitingForDeviceResult = false;

                    Password = string.Empty;

                    StatusMessage =
                        $"Wi-Fi credentials saved successfully. " +
                        $"{StoredNetworkCount} network(s) stored.";

                    break;

                case "CLEARED":

                    _waitingForDeviceResult = false;

                    Password = string.Empty;

                    StatusMessage =
                        "App-sent Wi-Fi networks were cleared. " +
                        "The hardcoded fallback remains available.";

                    break;

                case "ERROR":

                    _waitingForDeviceResult = false;

                    StatusMessage =
                        "EMOTIX360 rejected the Wi-Fi provisioning request.";

                    break;
            }

            RefreshCommandStates();
        }


        // ========================================================
        // COMMAND STATE UPDATES
        // ========================================================

        private void RefreshCommandStates()
        {
            OnPropertyChanged(nameof(CanSendCredentials));
            OnPropertyChanged(nameof(CanClearStoredNetworks));
            OnPropertyChanged(nameof(CanUsePhoneSsid));

            SendCredentialsCommand?.ChangeCanExecute();
            ClearStoredNetworksCommand?.ChangeCanExecute();
            UsePhoneSsidCommand?.ChangeCanExecute();
        }
    }
}
