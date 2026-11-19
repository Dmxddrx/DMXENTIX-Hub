#include "wifi_manager.h"

#include "bluetooth_manager.h"

#include "display_manager.h"
#include "buzzer.h"

#include <WiFi.h>
#include <Wire.h>

#include <stdio.h>
#include <string.h>
#include <atomic>
#include <stdlib.h>
#include <Preferences.h>

#include <esp_sntp.h>

#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/queue.h>


#if __has_include("arduino_secrets.h")
#include "arduino_secrets.h"
#else
#define WIFI_SSID ""
#define WIFI_PASSWORD ""
#endif


namespace WifiManager {
namespace {

// ------------------------------------------------------------
// WIFI CONFIGURATION
// ------------------------------------------------------------

constexpr char TIME_ZONE[] = "<+0530>-5:30";

constexpr char NTP_SERVER_1[] = "pool.ntp.org";
constexpr char NTP_SERVER_2[] = "time.google.com";
constexpr char NTP_SERVER_3[] = "time.cloudflare.com";

constexpr uint32_t RECONNECT_INTERVAL_MS = 15000;

// ------------------------------------------------------------
// FREERTOS CONFIGURATION
// ------------------------------------------------------------

constexpr uint32_t WIFI_TASK_STACK_SIZE = 4096;

constexpr UBaseType_t WIFI_TASK_PRIORITY = 2;

constexpr BaseType_t WIFI_TASK_CORE = 0;

constexpr uint8_t WIFI_EVENT_QUEUE_LENGTH = 24;

constexpr size_t WIFI_FRAME_MAX_LENGTH = 20;

constexpr uint32_t WIFI_TRANSFER_TIMEOUT_MS = 30000;


// ------------------------------------------------------------
// EVENT DEFINITIONS
// ------------------------------------------------------------

enum class ManagerEvent : uint8_t {
    GOT_IP,
    DISCONNECTED,
    TIME_SYNCED,
    PROVISIONING_FRAME
};


// The same queue handles Wi-Fi events and BLE provisioning
// frames. This prevents the Wi-Fi task from blocking while
// waiting for a different queue.
struct ManagerMessage {
    ManagerEvent event;

    uint8_t length;

    char frame[WIFI_FRAME_MAX_LENGTH + 1];
};

QueueHandle_t wifiEventQueue = nullptr;

TaskHandle_t wifiTaskHandle = nullptr;


// ------------------------------------------------------------
// DRIVER STATE
// ------------------------------------------------------------

bool initialized = false;

bool ntpConfigured = false;

std::atomic<bool> connected{false};

std::atomic<bool> ntpHasSynced{false};


// ------------------------------------------------------------
// WIFI CREDENTIAL CHECK
// ------------------------------------------------------------

bool hasCredentials()
{
    return WIFI_SSID[0] != '\0';
}


// ------------------------------------------------------------
// EVENT QUEUE HELPER
// ------------------------------------------------------------

void enqueueEvent(ManagerEvent event)
{
    if (wifiEventQueue == nullptr) {
        return;
    }

    ManagerMessage message{};

    message.event = event;

    xQueueSend(
        wifiEventQueue,
        &message,
        0
    );
}


// ------------------------------------------------------------
// WIFI EVENT CALLBACK
// ------------------------------------------------------------

void wifiEventHandler(WiFiEvent_t event)
{
    switch (event) {

        case ARDUINO_EVENT_WIFI_STA_GOT_IP:

            enqueueEvent(ManagerEvent::GOT_IP);

            break;


        case ARDUINO_EVENT_WIFI_STA_DISCONNECTED:

        case ARDUINO_EVENT_WIFI_STA_LOST_IP:

            enqueueEvent(ManagerEvent::DISCONNECTED);

            break;


        default:
            break;
    }
}


// ------------------------------------------------------------
// NTP TIME SYNCHRONIZATION CALLBACK
// ------------------------------------------------------------

void onTimeSync(struct timeval *tv)
{
    (void)tv;

    ntpHasSynced.store(true, std::memory_order_relaxed);

    enqueueEvent(ManagerEvent::TIME_SYNCED);
}

// ============================================================
// MULTI WIFI PROFILE STORAGE AND BLE PROVISIONING
// ============================================================

namespace {

// ------------------------------------------------------------
// PROFILE CONFIGURATION
// ------------------------------------------------------------

constexpr char WIFI_NAMESPACE[] = "emotixwifi";

constexpr char WIFI_STORAGE_KEY[] = "profiles";

constexpr uint32_t WIFI_STORAGE_MAGIC = 0xE3602026;

constexpr uint8_t WIFI_STORAGE_VERSION = 1;

constexpr uint8_t MAX_WIFI_PROFILES = 8;

constexpr uint8_t MAX_SSID_LENGTH = 32;

constexpr uint8_t MAX_PASSWORD_LENGTH = 63;

constexpr uint8_t MAX_CHUNK_LENGTH = 12;

constexpr uint8_t MAX_SSID_CHUNKS = 4;

constexpr uint8_t MAX_PASSWORD_CHUNKS = 7;


// ------------------------------------------------------------
// WIFI PROFILE STRUCTURES
// ------------------------------------------------------------

struct WifiProfile {

    char ssid[MAX_SSID_LENGTH + 1];

    char password[MAX_PASSWORD_LENGTH + 1];
};


struct WifiProfileStorage {

    uint32_t magic;

    uint8_t version;

    uint8_t count;

    WifiProfile profiles[MAX_WIFI_PROFILES];
};


// Persistent application-supplied Wi-Fi profiles.
WifiProfileStorage storedProfiles{};

// Thread-safe network count for BLE status reporting.
std::atomic<uint8_t> publishedStoredProfileCount{0};

// ------------------------------------------------------------
// TRANSFER STATE
// ------------------------------------------------------------

struct WifiTransfer {

    bool active = false;

    uint8_t ssidLength = 0;

    uint8_t passwordLength = 0;

    uint8_t ssidChunkCount = 0;

    uint8_t passwordChunkCount = 0;

    uint32_t lastActivityMs = 0;


    char ssidChunks[MAX_SSID_CHUNKS][MAX_CHUNK_LENGTH + 1]{};

    char passwordChunks[MAX_PASSWORD_CHUNKS][MAX_CHUNK_LENGTH + 1]{};


    bool ssidReceived[MAX_SSID_CHUNKS]{};

    bool passwordReceived[MAX_PASSWORD_CHUNKS]{};
};


WifiTransfer transfer;


// ------------------------------------------------------------
// RESET PROFILE STORAGE
// ------------------------------------------------------------

void resetProfileStorage()
{
    memset(
        &storedProfiles,
        0,
        sizeof(storedProfiles)
    );

    storedProfiles.magic = WIFI_STORAGE_MAGIC;

    storedProfiles.version = WIFI_STORAGE_VERSION;

    storedProfiles.count = 0;

    publishedStoredProfileCount.store(
        0,
        std::memory_order_relaxed
    );
}


// ------------------------------------------------------------
// RESET ACTIVE TRANSFER
// ------------------------------------------------------------

void resetTransfer()
{
    transfer = WifiTransfer{};
}


// ------------------------------------------------------------
// PUBLISH PROVISIONING STATUS
// ------------------------------------------------------------

void publishProvisioningStatus(const char *status)
{
    BluetoothManager::setWifiProvisioningStatus(
        status,
        storedProfiles.count
    );
}


// ------------------------------------------------------------
// VALIDATE A STORED PROFILE
// ------------------------------------------------------------

bool isValidStoredProfile(const WifiProfile &profile)
{
    const bool hasSsidTerminator =
        memchr(
            profile.ssid,
            '\0',
            sizeof(profile.ssid)
        ) != nullptr;

    const bool hasPasswordTerminator =
        memchr(
            profile.password,
            '\0',
            sizeof(profile.password)
        ) != nullptr;

    if (!hasSsidTerminator || !hasPasswordTerminator) {
        return false;
    }

    if (profile.ssid[0] == '\0') {
        return false;
    }

    return true;
}


// ------------------------------------------------------------
// LOAD WIFI PROFILES FROM NVS
// ------------------------------------------------------------

bool loadWifiProfiles()
{
    resetProfileStorage();

    Preferences preferences;

    if (!preferences.begin(WIFI_NAMESPACE, false)) {

        Serial.println(
            "[WIFI] Could not open profile storage"
        );

        return false;
    }

    if (!preferences.isKey(WIFI_STORAGE_KEY)) {

        preferences.end();

        Serial.println(
            "[WIFI] No app-sent Wi-Fi profiles found"
        );

        return true;
    }


    if (preferences.getBytesLength(WIFI_STORAGE_KEY) !=
        sizeof(WifiProfileStorage)) {

        preferences.end();

        Serial.println(
            "[WIFI] Invalid stored profile data size"
        );

        return false;
    }


    WifiProfileStorage loaded{};

    const size_t bytesRead =
        preferences.getBytes(
            WIFI_STORAGE_KEY,
            &loaded,
            sizeof(loaded)
        );

    preferences.end();


    if (bytesRead != sizeof(loaded) ||
        loaded.magic != WIFI_STORAGE_MAGIC ||
        loaded.version != WIFI_STORAGE_VERSION ||
        loaded.count > MAX_WIFI_PROFILES) {

        Serial.println(
            "[WIFI] Stored profile validation failed"
        );

        return false;
    }


    for (uint8_t i = 0; i < loaded.count; ++i) {

        if (!isValidStoredProfile(loaded.profiles[i])) {

            Serial.println(
                "[WIFI] Invalid stored profile detected"
            );

            return false;
        }
    }


    storedProfiles = loaded;

    publishedStoredProfileCount.store(
        storedProfiles.count,
        std::memory_order_relaxed
    );

    Serial.printf(
        "[WIFI] Loaded %u app-sent Wi-Fi profiles\n",
        storedProfiles.count
    );

    return true;
}


// ------------------------------------------------------------
// SAVE WIFI PROFILES TO NVS
// ------------------------------------------------------------

bool saveWifiProfiles(
    const WifiProfileStorage &profiles
)
{
    Preferences preferences;

    if (!preferences.begin(WIFI_NAMESPACE, false)) {

        Serial.println(
            "[WIFI] Could not open profile storage for writing"
        );

        return false;
    }

    const size_t bytesWritten =
        preferences.putBytes(
            WIFI_STORAGE_KEY,
            &profiles,
            sizeof(profiles)
        );

    preferences.end();


    if (bytesWritten != sizeof(profiles)) {

        Serial.println(
            "[WIFI] Failed to save Wi-Fi profiles"
        );

        return false;
    }

    return true;
}


// ------------------------------------------------------------
// CLEAR APP-SENT WIFI PROFILES
// ------------------------------------------------------------

bool clearWifiProfiles()
{
    Preferences preferences;

    if (!preferences.begin(WIFI_NAMESPACE, false)) {
        return false;
    }

    bool result = true;

    if (preferences.isKey(WIFI_STORAGE_KEY)) {

        result = preferences.remove(WIFI_STORAGE_KEY);
    }

    preferences.end();


    if (!result) {

        Serial.println(
            "[WIFI] Failed to remove app-sent profiles"
        );

        return false;
    }

    resetProfileStorage();

    return true;
}


// ------------------------------------------------------------
// ADD OR UPDATE WIFI PROFILE
// ------------------------------------------------------------

bool saveWifiProfile(
    const char *ssid,
    const char *password
)
{
    if (ssid == nullptr || password == nullptr) {
        return false;
    }

    if (ssid[0] == '\0') {
        return false;
    }

    const size_t ssidLength = strlen(ssid);

    const size_t passwordLength = strlen(password);

    if (ssidLength > MAX_SSID_LENGTH ||
        passwordLength > MAX_PASSWORD_LENGTH) {

        return false;
    }


    WifiProfile newProfile{};

    memcpy(
        newProfile.ssid,
        ssid,
        ssidLength
    );

    memcpy(
        newProfile.password,
        password,
        passwordLength
    );


    WifiProfileStorage updated = storedProfiles;

    int existingIndex = -1;


    // Check whether this SSID is already stored.
    for (uint8_t i = 0; i < updated.count; ++i) {

        if (strcmp(
                updated.profiles[i].ssid,
                ssid
            ) == 0) {

            existingIndex = i;

            break;
        }
    }


    if (existingIndex >= 0) {

        // Move an updated profile to priority 1.
        for (int i = existingIndex; i > 0; --i) {

            updated.profiles[i] =
                updated.profiles[i - 1];
        }
    }
    else {

        // Insert a new profile at priority 1.
        if (updated.count < MAX_WIFI_PROFILES) {
            ++updated.count;
        }

        for (int i = updated.count - 1; i > 0; --i) {

            updated.profiles[i] =
                updated.profiles[i - 1];
        }
    }


    updated.profiles[0] = newProfile;


    // Save before replacing the active in-memory list.
    if (!saveWifiProfiles(updated)) {
        return false;
    }

    storedProfiles = updated;

    publishedStoredProfileCount.store(
        storedProfiles.count,
        std::memory_order_relaxed
    );

    Serial.printf(
        "[WIFI] App-sent profile saved. Stored networks: %u\n",
        storedProfiles.count
    );

    return true;
}


// ============================================================
// BASE64 DECODING
// ============================================================

int base64Value(char value)
{
    if (value >= 'A' && value <= 'Z') {
        return value - 'A';
    }

    if (value >= 'a' && value <= 'z') {
        return value - 'a' + 26;
    }

    if (value >= '0' && value <= '9') {
        return value - '0' + 52;
    }

    if (value == '+') {
        return 62;
    }

    if (value == '/') {
        return 63;
    }

    return -1;
}


// ------------------------------------------------------------
// DECODE BASE64 DATA
// ------------------------------------------------------------

bool decodeBase64(
    const char *input,
    size_t inputLength,
    uint8_t *output,
    size_t expectedOutputLength
)
{
    if (expectedOutputLength == 0) {
        return inputLength == 0;
    }

    if (input == nullptr || output == nullptr) {
        return false;
    }

    if ((inputLength % 4) != 0) {
        return false;
    }


    size_t outputPosition = 0;


    for (size_t i = 0; i < inputLength; i += 4) {

        const bool finalGroup =
            (i + 4 == inputLength);


        const int a = base64Value(input[i]);

        const int b = base64Value(input[i + 1]);

        if (a < 0 || b < 0) {
            return false;
        }


        const char cCharacter = input[i + 2];

        const char dCharacter = input[i + 3];


        const bool cPadding = cCharacter == '=';

        const bool dPadding = dCharacter == '=';


        int c = 0;

        int d = 0;


        if (cPadding) {

            if (!finalGroup || !dPadding) {
                return false;
            }
        }
        else {

            c = base64Value(cCharacter);

            if (c < 0) {
                return false;
            }
        }


        if (dPadding && !finalGroup) {
            return false;
        }


        if (!dPadding) {

            d = base64Value(dCharacter);

            if (d < 0) {
                return false;
            }
        }


        const uint32_t value =
            (static_cast<uint32_t>(a) << 18) |
            (static_cast<uint32_t>(b) << 12) |
            (static_cast<uint32_t>(c) << 6) |
            static_cast<uint32_t>(d);


        const uint8_t decodedBytes =
            cPadding ? 1 :
            dPadding ? 2 : 3;


        if (outputPosition + decodedBytes >
            expectedOutputLength) {

            return false;
        }


        output[outputPosition++] =
            static_cast<uint8_t>((value >> 16) & 0xFF);


        if (decodedBytes >= 2) {

            output[outputPosition++] =
                static_cast<uint8_t>((value >> 8) & 0xFF);
        }


        if (decodedBytes >= 3) {

            output[outputPosition++] =
                static_cast<uint8_t>(value & 0xFF);
        }
    }


    return outputPosition == expectedOutputLength;
}


// ------------------------------------------------------------
// EXPECTED BASE64 LENGTH
// ------------------------------------------------------------

size_t getBase64Length(size_t byteLength)
{
    return ((byteLength + 2) / 3) * 4;
}


// ------------------------------------------------------------
// EXPECTED CHUNK COUNT
// ------------------------------------------------------------

uint8_t getChunkCount(size_t encodedLength)
{
    return static_cast<uint8_t>(
        (encodedLength + MAX_CHUNK_LENGTH - 1) /
        MAX_CHUNK_LENGTH
    );
}


// ============================================================
// TRANSFER VALIDATION
// ============================================================

// ------------------------------------------------------------
// BEGIN NEW TRANSFER
// ------------------------------------------------------------

bool beginWifiTransfer(const char *frame)
{
    unsigned int ssidLength = 0;

    unsigned int passwordLength = 0;

    unsigned int ssidChunks = 0;

    unsigned int passwordChunks = 0;

    char extra;


    const int parsed = sscanf(
        frame,
        "B,%u,%u,%u,%u%c",
        &ssidLength,
        &passwordLength,
        &ssidChunks,
        &passwordChunks,
        &extra
    );


    if (parsed != 4) {
        return false;
    }


    if (ssidLength == 0 ||
        ssidLength > MAX_SSID_LENGTH ||
        passwordLength > MAX_PASSWORD_LENGTH ||
        (passwordLength != 0 && passwordLength < 8)) {

        return false;
    }


    const size_t expectedSsidLength =
        getBase64Length(ssidLength);

    const size_t expectedPasswordLength =
        getBase64Length(passwordLength);


    const uint8_t expectedSsidChunks =
        getChunkCount(expectedSsidLength);

    const uint8_t expectedPasswordChunks =
        getChunkCount(expectedPasswordLength);


    if (ssidChunks != expectedSsidChunks ||
        passwordChunks != expectedPasswordChunks ||
        ssidChunks > MAX_SSID_CHUNKS ||
        passwordChunks > MAX_PASSWORD_CHUNKS) {

        return false;
    }


    resetTransfer();

    transfer.active = true;
    transfer.lastActivityMs = millis();

    transfer.ssidLength =
        static_cast<uint8_t>(ssidLength);

    transfer.passwordLength =
        static_cast<uint8_t>(passwordLength);

    transfer.ssidChunkCount =
        static_cast<uint8_t>(ssidChunks);

    transfer.passwordChunkCount =
        static_cast<uint8_t>(passwordChunks);


    publishProvisioningStatus("RECEIVING");

    Serial.println(
        "[WIFI] Credential transfer started"
    );

    return true;
}


// ------------------------------------------------------------
// RECEIVE SSID / PASSWORD CHUNK
// ------------------------------------------------------------

bool receiveWifiChunk(const char *frame)
{
    char type = '\0';

    unsigned int index = 0;

    unsigned int count = 0;

    char chunk[MAX_CHUNK_LENGTH + 1]{};

    char extra;


    const int parsed = sscanf(
        frame,
        "%c,%u/%u,%12s%c",
        &type,
        &index,
        &count,
        chunk,
        &extra
    );


    if (parsed != 4) {
        return false;
    }


    if (type != 'S' && type != 'P') {
        return false;
    }


    if (!transfer.active) {
        return false;
    }


    const size_t chunkLength = strlen(chunk);


    if (type == 'S') {

        if (count != transfer.ssidChunkCount ||
            index >= transfer.ssidChunkCount) {

            return false;
        }


        const size_t encodedLength =
            getBase64Length(transfer.ssidLength);

        const size_t expectedLength =
            (index + 1 < count)
                ? MAX_CHUNK_LENGTH
                : encodedLength -
                  (index * MAX_CHUNK_LENGTH);


        if (chunkLength != expectedLength) {
            return false;
        }


        strcpy(
            transfer.ssidChunks[index],
            chunk
        );

        transfer.ssidReceived[index] = true;
    }
    else {

        if (count != transfer.passwordChunkCount ||
            index >= transfer.passwordChunkCount) {

            return false;
        }


        const size_t encodedLength =
            getBase64Length(transfer.passwordLength);

        const size_t expectedLength =
            (index + 1 < count)
                ? MAX_CHUNK_LENGTH
                : encodedLength -
                  (index * MAX_CHUNK_LENGTH);


        if (chunkLength != expectedLength) {
            return false;
        }


        strcpy(
            transfer.passwordChunks[index],
            chunk
        );

        transfer.passwordReceived[index] = true;
    }

    transfer.lastActivityMs = millis();

    return true;
}


// ------------------------------------------------------------
// CHECK TRANSFER COMPLETENESS
// ------------------------------------------------------------

bool isTransferComplete()
{
    if (!transfer.active) {
        return false;
    }


    for (uint8_t i = 0;
         i < transfer.ssidChunkCount;
         ++i) {

        if (!transfer.ssidReceived[i]) {
            return false;
        }
    }


    for (uint8_t i = 0;
         i < transfer.passwordChunkCount;
         ++i) {

        if (!transfer.passwordReceived[i]) {
            return false;
        }
    }


    return true;
}


// ------------------------------------------------------------
// COMMIT WIFI TRANSFER
// ------------------------------------------------------------

bool commitWifiTransfer()
{
    if (!isTransferComplete()) {
        return false;
    }


    char encodedSsid[45]{};

    char encodedPassword[85]{};


    size_t ssidPosition = 0;

    size_t passwordPosition = 0;


    for (uint8_t i = 0;
         i < transfer.ssidChunkCount;
         ++i) {

        const size_t length =
            strlen(transfer.ssidChunks[i]);

        memcpy(
            encodedSsid + ssidPosition,
            transfer.ssidChunks[i],
            length
        );

        ssidPosition += length;
    }


    for (uint8_t i = 0;
         i < transfer.passwordChunkCount;
         ++i) {

        const size_t length =
            strlen(transfer.passwordChunks[i]);

        memcpy(
            encodedPassword + passwordPosition,
            transfer.passwordChunks[i],
            length
        );

        passwordPosition += length;
    }


    uint8_t decodedSsid[MAX_SSID_LENGTH]{};

    uint8_t decodedPassword[MAX_PASSWORD_LENGTH]{};


    if (!decodeBase64(
            encodedSsid,
            ssidPosition,
            decodedSsid,
            transfer.ssidLength)) {

        return false;
    }


    if (!decodeBase64(
            encodedPassword,
            passwordPosition,
            decodedPassword,
            transfer.passwordLength)) {

        return false;
    }


    // SSID/password are stored as C strings, so embedded
    // zero bytes are not allowed.
    if (memchr(
            decodedSsid,
            '\0',
            transfer.ssidLength) != nullptr) {

        return false;
    }


    if (memchr(
            decodedPassword,
            '\0',
            transfer.passwordLength) != nullptr) {

        return false;
    }


    char ssid[MAX_SSID_LENGTH + 1]{};

    char password[MAX_PASSWORD_LENGTH + 1]{};


    memcpy(
        ssid,
        decodedSsid,
        transfer.ssidLength
    );

    memcpy(
        password,
        decodedPassword,
        transfer.passwordLength
    );


    return saveWifiProfile(
        ssid,
        password
    );
}


// ------------------------------------------------------------
// PROCESS RECEIVED PROVISIONING FRAME
// ------------------------------------------------------------

bool processProvisioningFrame(
    const char *frame,
    bool &profilesChanged
)
{
    profilesChanged = false;

    if (frame == nullptr || frame[0] == '\0') {
        return false;
    }

    // Only printable ASCII protocol characters are accepted.
    for (const char *p = frame; *p != '\0'; ++p) {

        if (*p < 0x21 || *p > 0x7E) {
            resetTransfer();
            publishProvisioningStatus("ERROR");
            return false;
        }
    }


    // --------------------------------------------------------
    // CLEAR ALL APP-SENT WIFI CREDENTIALS
    // --------------------------------------------------------

    if (strcmp(frame, "CLEAR") == 0) {

        resetTransfer();

        if (!clearWifiProfiles()) {

            publishProvisioningStatus("ERROR");

            return false;
        }

        profilesChanged = true;

        publishProvisioningStatus("CLEARED");

        Serial.println(
            "[WIFI] App-sent Wi-Fi profiles cleared"
        );

        return true;
    }


    // --------------------------------------------------------
    // BEGIN TRANSFER
    // --------------------------------------------------------

    if (frame[0] == 'B' && frame[1] == ',') {

        if (!beginWifiTransfer(frame)) {

            resetTransfer();

            publishProvisioningStatus("ERROR");

            return false;
        }

        return true;
    }


    // --------------------------------------------------------
    // COMMIT TRANSFER
    // --------------------------------------------------------

    if (strcmp(frame, "COMMIT") == 0) {

        const bool saved = commitWifiTransfer();

        resetTransfer();


        if (!saved) {

            publishProvisioningStatus("ERROR");

            Serial.println(
                "[WIFI] Credential transfer rejected"
            );

            return false;
        }


        profilesChanged = true;

        publishProvisioningStatus("SAVED");

        Serial.println(
            "[WIFI] Credential transfer saved successfully"
        );

        return true;
    }


    // --------------------------------------------------------
    // RECEIVE CHUNK
    // --------------------------------------------------------

    if ((frame[0] == 'S' || frame[0] == 'P') &&
        frame[1] == ',') {

        if (!receiveWifiChunk(frame)) {

            resetTransfer();

            publishProvisioningStatus("ERROR");

            return false;
        }

        return true;
    }


    resetTransfer();

    publishProvisioningStatus("ERROR");

    return false;
}


// ------------------------------------------------------------
// GET NUMBER OF AVAILABLE NETWORK CANDIDATES
// ------------------------------------------------------------

uint8_t getCandidateCount()
{
    uint8_t count = storedProfiles.count;

    if (WIFI_SSID[0] != '\0') {
        ++count;
    }

    return count;
}


// ------------------------------------------------------------
// GET A NETWORK CANDIDATE
// ------------------------------------------------------------

bool getCandidate(
    uint8_t index,
    char *ssid,
    size_t ssidSize,
    char *password,
    size_t passwordSize
)
{
    if (ssid == nullptr ||
        password == nullptr ||
        ssidSize == 0 ||
        passwordSize == 0) {

        return false;
    }


    if (index < storedProfiles.count) {

        strncpy(
            ssid,
            storedProfiles.profiles[index].ssid,
            ssidSize - 1
        );

        strncpy(
            password,
            storedProfiles.profiles[index].password,
            passwordSize - 1
        );

        ssid[ssidSize - 1] = '\0';

        password[passwordSize - 1] = '\0';

        return true;
    }


    // The hardcoded network is always the final fallback.
    if (WIFI_SSID[0] != '\0' &&
        index == storedProfiles.count) {

        strncpy(
            ssid,
            WIFI_SSID,
            ssidSize - 1
        );

        strncpy(
            password,
            WIFI_PASSWORD,
            passwordSize - 1
        );

        ssid[ssidSize - 1] = '\0';

        password[passwordSize - 1] = '\0';

        return true;
    }


    return false;
}

} // namespace

// ------------------------------------------------------------
// UPDATE NETWORK STATUS
// ------------------------------------------------------------

void publishNetworkStatus()
{
    const bool wifiConnected =
        connected.load(std::memory_order_relaxed);

    const bool timeSynced =
        ntpHasSynced.load(std::memory_order_relaxed);

    char ip[16] = "-";

    if (wifiConnected) {

        const IPAddress address = WiFi.localIP();

        snprintf(
            ip,
            sizeof(ip),
            "%u.%u.%u.%u",
            address[0],
            address[1],
            address[2],
            address[3]
        );
    }

    // The OLED is optional and may currently be disabled.
    if (DisplayManager::isReady()) {

        DisplayManager::setNetworkStatus(
            wifiConnected,
            timeSynced,
            ip
        );
    }
}


// ------------------------------------------------------------
// CONFIGURE NTP
// ------------------------------------------------------------

void configureNTP()
{
    if (ntpConfigured) {
        return;
    }

    // Receive notification after the first successful time sync.
    sntp_set_time_sync_notification_cb(onTimeSync);

    configTzTime(
        TIME_ZONE,
        NTP_SERVER_1,
        NTP_SERVER_2,
        NTP_SERVER_3
    );

    ntpConfigured = true;

    Serial.println("[WIFI] NTP synchronization started");
}


// ------------------------------------------------------------
// WIFI FREERTOS TASK
// ------------------------------------------------------------

void wifiTask(void *parameter)
{
    (void)parameter;

    Serial.println("[WIFI] Manager task started");

    constexpr uint32_t CONNECT_TIMEOUT_MS = 12000;

    uint8_t candidateIndex = 0;

    bool attemptInProgress = false;

    bool connectionSoundPlayed = false;

    bool noCredentialsReported = false;

    uint32_t attemptStartedMs = 0;

    uint32_t nextSweepMs = 0;


    auto startCandidate = [&](uint8_t index) {

        char ssid[33]{};

        char password[64]{};


        if (!getCandidate(
                index,
                ssid,
                sizeof(ssid),
                password,
                sizeof(password))) {

            return false;
        }


        // Disconnect from any previous candidate.
        WiFi.disconnect(false, false);

        vTaskDelay(pdMS_TO_TICKS(100));


        Serial.printf(
            "[WIFI] Trying network candidate %u\n",
            index + 1
        );


        if (!connectionSoundPlayed) {

            Buzzer::play(
                Buzzer::Sound::WIFI_CONNECTING
            );

            connectionSoundPlayed = true;
        }


        // Credentials are never printed to Serial.
        WiFi.begin(
            ssid,
            password
        );


        attemptInProgress = true;

        attemptStartedMs = millis();

        return true;
    };


    for (;;)
    {
        // ----------------------------------------------------
        // WAIT FOR WIFI EVENTS OR PROVISIONING FRAMES
        // ----------------------------------------------------

        TickType_t waitTicks = portMAX_DELAY;

        const uint8_t candidateCount =
            getCandidateCount();


        // Check the pending credential-transfer timeout.
        if (transfer.active)
        {
            const uint32_t elapsed =
                millis() - transfer.lastActivityMs;

            const uint32_t remaining =
                (elapsed >= WIFI_TRANSFER_TIMEOUT_MS)
                    ? 0
                    : WIFI_TRANSFER_TIMEOUT_MS - elapsed;

            waitTicks = pdMS_TO_TICKS(remaining);
        }


        // Calculate the next Wi-Fi connection event timeout.
        TickType_t connectionWait = portMAX_DELAY;

        if (!connected.load(std::memory_order_relaxed))
        {
            if (attemptInProgress)
            {
                const uint32_t elapsed =
                    millis() - attemptStartedMs;

                const uint32_t remaining =
                    (elapsed >= CONNECT_TIMEOUT_MS)
                        ? 0
                        : CONNECT_TIMEOUT_MS - elapsed;

                connectionWait = pdMS_TO_TICKS(remaining);
            }
            else if (candidateCount > 0)
            {
                const int32_t remaining =
                    static_cast<int32_t>(
                        nextSweepMs - millis()
                    );

                connectionWait =
                    (nextSweepMs == 0 || remaining <= 0)
                        ? 0
                        : pdMS_TO_TICKS(remaining);
            }
        }

        // Use whichever event deadline occurs first.
        if (connectionWait < waitTicks) {
            waitTicks = connectionWait;
        }

        ManagerMessage message{};

        const BaseType_t messageReceived =
    xQueueReceive(
        wifiEventQueue,
        &message,
        waitTicks
    );


        // Expire an abandoned transfer even when no new BLE frame arrives.
        if (transfer.active &&
            (uint32_t)(millis() - transfer.lastActivityMs) >=
                WIFI_TRANSFER_TIMEOUT_MS)
        {
            resetTransfer();

            publishProvisioningStatus("ERROR");

            Serial.println(
                "[WIFI] Credential transfer timed out"
            );
        }


        if (messageReceived == pdTRUE)
        {
            switch (message.event)
            {
                // --------------------------------------------
                // WIFI CONNECTED
                // --------------------------------------------

                case ManagerEvent::GOT_IP:
                {
                    // Ignore a stale connection event if the station has
                    // already disconnected while processing new credentials.
                    if (WiFi.status() != WL_CONNECTED) {

                        Serial.println(
                            "[WIFI] Ignoring stale GOT_IP event"
                        );

                        break;
                    }

                    connected.store(
                        true,
                        std::memory_order_relaxed
                    );

                    attemptInProgress = false;

                    const IPAddress address =
                        WiFi.localIP();

                    Serial.printf(
                        "[WIFI] Connected. IP: %u.%u.%u.%u\n",
                        address[0],
                        address[1],
                        address[2],
                        address[3]
                    );

                    Buzzer::play(
                        Buzzer::Sound::WIFI_CONNECTED
                    );

                    configureNTP();

                    publishNetworkStatus();

                    break;
                }


                // --------------------------------------------
                // WIFI DISCONNECTED
                // --------------------------------------------

                case ManagerEvent::DISCONNECTED:
                {
                    // Ignore a delayed disconnect event if the station
                    // has already established another connection.
                    if (WiFi.status() == WL_CONNECTED)
                    {
                        Serial.println(
                            "[WIFI] Ignoring stale DISCONNECTED event"
                        );

                        break;
                    }

                    const bool wasConnected =
                        connected.exchange(
                            false,
                            std::memory_order_relaxed
                        );


                    if (wasConnected)
                    {
                        Serial.println(
                            "[WIFI] Connection lost"
                        );

                        Buzzer::play(
                            Buzzer::Sound::WIFI_DISCONNECTED
                        );

                        // Retry the priority list after the
                        // normal reconnection interval.
                        attemptInProgress = false;

                        candidateIndex = 0;

                        nextSweepMs =
                            millis() + RECONNECT_INTERVAL_MS;

                        connectionSoundPlayed = false;

                        publishNetworkStatus();
                    }

                    break;
                }


                // --------------------------------------------
                // NTP TIME SYNCHRONIZED
                // --------------------------------------------

                case ManagerEvent::TIME_SYNCED:
                {
                    Serial.println(
                        "[WIFI] NTP time synchronized"
                    );

                    publishNetworkStatus();

                    break;
                }


                // --------------------------------------------
                // BLE WIFI PROVISIONING FRAME
                // --------------------------------------------

                case ManagerEvent::PROVISIONING_FRAME:
                {
                    message.frame[
                        sizeof(message.frame) - 1
                    ] = '\0';


                    bool profilesChanged = false;

                    processProvisioningFrame(
                        message.frame,
                        profilesChanged
                    );


                    if (profilesChanged)
                    {
                        const bool wasConnected =
                            connected.exchange(
                                false,
                                std::memory_order_relaxed
                            );

                        // Re-evaluate the complete priority list.
                        WiFi.disconnect(false, false);

                        attemptInProgress = false;

                        candidateIndex = 0;

                        nextSweepMs = 0;

                        connectionSoundPlayed = false;

                        if (wasConnected) {
                            publishNetworkStatus();
                        }
                    }

                    break;
                }
            }
        }


        // ----------------------------------------------------
        // HANDLE CONNECTION TIMEOUT
        // ----------------------------------------------------

        if (connected.load(std::memory_order_relaxed)) {
            continue;
        }


        const uint8_t currentCandidateCount =
            getCandidateCount();


        // ----------------------------------------------------
        // NO AVAILABLE CREDENTIALS
        // ----------------------------------------------------

        if (currentCandidateCount == 0)
        {
            attemptInProgress = false;

            candidateIndex = 0;

            nextSweepMs = 0;

            if (!noCredentialsReported)
            {
                Serial.println(
                    "[WIFI] No saved or hardcoded networks. "
                    "Waiting for BLE provisioning."
                );

                noCredentialsReported = true;
            }

            continue;
        }


        noCredentialsReported = false;


        // ----------------------------------------------------
        // CHECK CURRENT CONNECTION ATTEMPT
        // ----------------------------------------------------

        if (attemptInProgress)
        {
            const uint32_t elapsed =
                millis() - attemptStartedMs;

            if (elapsed < CONNECT_TIMEOUT_MS) {
                continue;
            }


            Serial.printf(
                "[WIFI] Candidate %u timed out\n",
                candidateIndex + 1
            );

            attemptInProgress = false;

            ++candidateIndex;


            // All candidates failed.
            if (candidateIndex >= currentCandidateCount)
            {
                candidateIndex = 0;

                nextSweepMs =
                    millis() + RECONNECT_INTERVAL_MS;

                connectionSoundPlayed = false;

                Serial.println(
                    "[WIFI] All networks failed. "
                    "Retrying the priority list later."
                );
            }
        }


        // ----------------------------------------------------
        // START NEXT CONNECTION ATTEMPT
        // ----------------------------------------------------

        if (!attemptInProgress)
        {
            const int32_t remaining =
                static_cast<int32_t>(
                    nextSweepMs - millis()
                );

            if (nextSweepMs != 0 && remaining > 0) {
                continue;
            }


            if (candidateIndex >= currentCandidateCount) {
                candidateIndex = 0;
            }


            if (!startCandidate(candidateIndex))
            {
                Serial.println(
                    "[WIFI] Could not start network attempt"
                );

                attemptInProgress = false;

                nextSweepMs =
                    millis() + RECONNECT_INTERVAL_MS;
            }
        }
    }
}

} // namespace


// ============================================================
// PUBLIC FUNCTIONS
// ============================================================


// ------------------------------------------------------------
// INITIALIZE WIFI
// ------------------------------------------------------------

bool begin()
{
    if (initialized) {
        return true;
    }

    resetProfileStorage();

    loadWifiProfiles();

    wifiEventQueue = xQueueCreate(
        WIFI_EVENT_QUEUE_LENGTH,
        sizeof(ManagerMessage)
    );

    if (wifiEventQueue == nullptr) {
        return false;
    }

    WiFi.persistent(false);

    // Reconnection is managed by our own task.
    WiFi.setAutoReconnect(false);

    // Register callback before starting the station.
    WiFi.onEvent(wifiEventHandler);

    if (!WiFi.mode(WIFI_STA)) {

        vQueueDelete(wifiEventQueue);

        wifiEventQueue = nullptr;

        return false;
    }

    initialized = true;

    return true;
}


// ------------------------------------------------------------
// START WIFI TASK
// ------------------------------------------------------------

bool startTask()
{
    if (!initialized || wifiEventQueue == nullptr ||
        wifiTaskHandle != nullptr) {
        return false;
    }

    if (getCandidateCount() == 0) {

        Serial.println(
            "[WIFI] No initial networks. "
            "Waiting for BLE provisioning."
        );
    }

    BaseType_t result = xTaskCreatePinnedToCore(
        wifiTask,
        "WiFiManager",
        WIFI_TASK_STACK_SIZE,
        nullptr,
        WIFI_TASK_PRIORITY,
        &wifiTaskHandle,
        WIFI_TASK_CORE
    );

    if (result != pdPASS) {

        wifiTaskHandle = nullptr;

        return false;
    }

    return true;
}


// ------------------------------------------------------------
// TASK STATUS
// ------------------------------------------------------------

bool isTaskRunning()
{
    return wifiTaskHandle != nullptr;
}


// ------------------------------------------------------------
// CONNECTION STATUS
// ------------------------------------------------------------

bool isConnected()
{
    return connected.load(std::memory_order_relaxed);
}


// ------------------------------------------------------------
// TIME STATUS
// ------------------------------------------------------------

bool isTimeSynced()
{
    return ntpHasSynced.load(std::memory_order_relaxed);
}


bool isTimeValid()
{
    if (!isTimeSynced()) {
        return false;
    }

    const time_t now = time(nullptr);

    return now > 1700000000;
}


// ------------------------------------------------------------
// GET LOCAL TIME
// ------------------------------------------------------------

bool getLocalTime(struct tm &out)
{
    if (!isTimeValid()) {
        return false;
    }

    const time_t now = time(nullptr);

    return localtime_r(&now, &out) != nullptr;
}


// ------------------------------------------------------------
// GET IP ADDRESS
// ------------------------------------------------------------

bool getIPAddress(char *buffer, size_t bufferSize)
{
    if (buffer == nullptr || bufferSize == 0) {
        return false;
    }

    if (!isConnected()) {

        snprintf(buffer, bufferSize, "-");

        return false;
    }

    const IPAddress address = WiFi.localIP();

    snprintf(
        buffer,
        bufferSize,
        "%u.%u.%u.%u",
        address[0],
        address[1],
        address[2],
        address[3]
    );

    return true;
}


// ------------------------------------------------------------
// GET CONNECTED SSID
// ------------------------------------------------------------

bool getSSID(char *buffer, size_t bufferSize)
{
    if (buffer == nullptr || bufferSize == 0) {
        return false;
    }

    if (!isConnected()) {

        snprintf(buffer, bufferSize, "-");

        return false;
    }

    const String currentSSID = WiFi.SSID();

    snprintf(
        buffer,
        bufferSize,
        "%s",
        currentSSID.c_str()
    );

    return true;
}

// ------------------------------------------------------------
// QUEUE WIFI PROVISIONING FRAME
// ------------------------------------------------------------

bool enqueueProvisioningFrame(
    const char *frame,
    size_t length
)
{
    if (frame == nullptr ||
        length == 0 ||
        length > WIFI_FRAME_MAX_LENGTH ||
        wifiEventQueue == nullptr) {

        return false;
    }


    ManagerMessage message{};

    message.event = ManagerEvent::PROVISIONING_FRAME;

    message.length =
        static_cast<uint8_t>(length);


    memcpy(
        message.frame,
        frame,
        length
    );

    message.frame[length] = '\0';


    return xQueueSend(
        wifiEventQueue,
        &message,
        0
    ) == pdTRUE;
}


// ------------------------------------------------------------
// GET STORED WIFI NETWORK COUNT
// ------------------------------------------------------------

uint8_t getStoredNetworkCount()
{
    return publishedStoredProfileCount.load(
        std::memory_order_relaxed
    );
}

} // namespace WifiManager