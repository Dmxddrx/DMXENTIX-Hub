using Android.Bluetooth;
using Android.Content;
using Microsoft.Maui.ApplicationModel;
using System.Diagnostics;

using AndroidActivity = Android.App.Activity;
using AndroidResult = Android.App.Result;

namespace DMXENTIX.Hub.Platforms.Android;

/// <summary>
/// Requests Bluetooth activation through the Android system dialog.
///
/// Android requires user consent to enable Bluetooth.
/// This class never attempts to enable the adapter silently.
/// </summary>
internal static class BluetoothEnableRequest
{
    private const int RequestCode = 0xB360;

    private static readonly object _syncLock = new();

    private static TaskCompletionSource<bool>? _pendingRequest;

    public static Task<bool> RequestAsync(
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> completion;
        bool shouldLaunch = false;

        lock (_syncLock)
        {
            if (_pendingRequest is null)
            {
                completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );

                _pendingRequest = completion;
                shouldLaunch = true;
            }
            else
            {
                completion = _pendingRequest;
            }
        }

        if (shouldLaunch)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    AndroidActivity? activity =
                        Platform.CurrentActivity;

                    if (activity is null)
                    {
                        CompleteRequest(completion, false);
                        return;
                    }

                    var intent = new Intent(
                        BluetoothAdapter.ActionRequestEnable
                    );

                    activity.StartActivityForResult(
                        intent,
                        RequestCode
                    );

                    Debug.WriteLine(
                        "[BluetoothEnableRequest] Android activation dialog requested."
                    );
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[BluetoothEnableRequest] Failed: {ex.Message}"
                    );

                    CompleteRequest(completion, false);
                }
            });
        }

        return AwaitResultAsync(
            completion,
            cancellationToken
        );
    }

    // ========================================================
    // WAIT FOR ANDROID ACTIVITY RESULT
    // ========================================================

    private static async Task<bool> AwaitResultAsync(
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(90),
                cancellationToken
            );
        }
        catch (TimeoutException)
        {
            CompleteRequest(completion, false);

            Debug.WriteLine(
                "[BluetoothEnableRequest] Activation request timed out."
            );

            return false;
        }
        catch
        {
            lock (_syncLock)
            {
                if (ReferenceEquals(_pendingRequest, completion))
                {
                    _pendingRequest = null;
                }
            }

            throw;
        }
    }

    // ========================================================
    // RECEIVE ANDROID ACTIVITY RESULT
    // ========================================================

    public static void HandleActivityResult(
        int requestCode,
        AndroidResult resultCode)
    {
        if (requestCode != RequestCode)
            return;

        bool enabled = resultCode == AndroidResult.Ok;

        Debug.WriteLine(
            $"[BluetoothEnableRequest] Android result: " +
            $"{(enabled ? "Accepted" : "Rejected")}"
        );

        CompletePendingRequest(enabled);
    }

    // ========================================================
    // COMPLETE REQUEST
    // ========================================================

    private static void CompleteRequest(
        TaskCompletionSource<bool> completion,
        bool result)
    {
        lock (_syncLock)
        {
            if (ReferenceEquals(_pendingRequest, completion))
            {
                _pendingRequest = null;
            }
        }

        completion.TrySetResult(result);
    }

    private static void CompletePendingRequest(bool result)
    {
        TaskCompletionSource<bool>? completion;

        lock (_syncLock)
        {
            completion = _pendingRequest;
            _pendingRequest = null;
        }

        completion?.TrySetResult(result);
    }
}