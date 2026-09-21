using System;
using UnityEngine;
using UnityEngine.Networking;
using NativeWebSocket;

// =====================================================================
// AIWebSocketService
//
// Talks to the FastAPI /ws/ask endpoint using the following protocol:
//
//   Unity -> Server (JSON text frames):
//     { "type": "request", "text": "...", "lang": "ar" }
//     { "type": "cancel" }
//
//   Server -> Unity:
//     JSON text frame:   { "type": "start",    "request_id": "..." }
//     JSON text frame:   { "type": "text",     "request_id": "...", "sequence": N, "text": "..." }
//     JSON text frame:   { "type": "audio",    "request_id": "...", "sequence": N, "size": N }
//     BINARY frame:      raw MP3 bytes for the sequence announced by the "audio" message above
//     JSON text frame:   { "type": "complete", "request_id": "...", "total_sequences": N }
//     JSON text frame:   { "type": "cancelled","request_id": "..." }
//     JSON text frame:   { "type": "error",    "request_id": "...", "message": "..." }
//
// NativeWebSocket's OnMessage callback delivers raw bytes for both text
// and binary frames without exposing the opcode directly, so we detect
// JSON control messages by attempting to parse them; anything that is
// not valid JSON starting with '{' is treated as the binary MP3 payload
// for the most recently announced "audio" message.
// =====================================================================

public class AIWebSocketService : MonoBehaviour
{
    [Header("Server")]
    public string wsURL = "wss://web-production-733e5.up.railway.app/ws/ask";

    [Header("Auth")]
    public string apiKey = "SECRET123";

    WebSocket socket;

    public string CurrentRequestId { get; private set; } = "";

    public event Action<string> OnStart;
    public event Action<string, int, string> OnSentenceText;   // requestId, sequence, text
    public event Action<string, int, byte[]> OnAudioChunk;     // requestId, sequence, mp3Bytes
    public event Action<string, int> OnComplete;               // requestId, totalSequences
    public event Action<string> OnCancelled;                   // requestId
    public event Action<string, string> OnError;               // requestId (maybe empty), message

    class PendingAudioMeta
    {
        public string requestId;
        public int sequence;
    }

    PendingAudioMeta pendingAudioMeta = null;

    public bool IsConnected =>
        socket != null && socket.State == WebSocketState.Open;

    public bool IsConnecting =>
        socket != null && socket.State == WebSocketState.Connecting;

    // =====================================================
    // CONNECT
    // =====================================================

    public async void Connect()
    {
        if (IsConnected || IsConnecting)
            return;

        string separator = wsURL.Contains("?") ? "&" : "?";
        string urlWithKey = wsURL + separator + "x-api-key=" + UnityWebRequest.EscapeURL(apiKey);

        socket = new WebSocket(urlWithKey);

        socket.OnOpen += () =>
        {
            Debug.Log("🔌 AI WebSocket connected");
        };

        socket.OnError += (errorMsg) =>
        {
            Debug.LogError("🔌 AI WebSocket error: " + errorMsg);
            OnError?.Invoke(CurrentRequestId, "ws_error: " + errorMsg);
        };

        socket.OnClose += (code) =>
        {
            Debug.LogWarning("🔌 AI WebSocket closed: " + code);
        };

        socket.OnMessage += HandleRawMessage;

        try
        {
            await socket.Connect();
        }
        catch (Exception e)
        {
            Debug.LogError("🔌 AI WebSocket connect failed: " + e.Message);
            OnError?.Invoke("", "connect_failed: " + e.Message);
        }
    }

    public async void Disconnect()
    {
        if (socket != null)
        {
            try
            {
                await socket.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    // =====================================================
    // UNITY LOOP - required so NativeWebSocket can dispatch
    // queued callbacks on the main thread (non-WebGL platforms).
    // =====================================================

    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        socket?.DispatchMessageQueue();
#endif
    }

    async void OnDestroy()
    {
        if (socket != null)
        {
            try
            {
                await socket.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    // =====================================================
    // OUTGOING MESSAGES
    // =====================================================

    public void SendRequest(string text, string lang)
    {
        if (!IsConnected)
        {
            Debug.LogError("Cannot send AI request, WebSocket is not connected.");
            OnError?.Invoke("", "not_connected");
            return;
        }

        var payload = new WsOutgoingRequest
        {
            type = "request",
            text = text,
            lang = lang
        };

        socket.SendText(JsonUtility.ToJson(payload));
    }

    public void SendCancel()
    {
        if (!IsConnected)
            return;

        var payload = new WsOutgoingCancel { type = "cancel" };
        socket.SendText(JsonUtility.ToJson(payload));
    }

    // =====================================================
    // INCOMING MESSAGE HANDLING
    // =====================================================

    void HandleRawMessage(byte[] rawBytes)
    {
        string asText = null;

        try
        {
            asText = System.Text.Encoding.UTF8.GetString(rawBytes);
        }
        catch
        {
            asText = null;
        }

        bool looksLikeJson =
            !string.IsNullOrEmpty(asText) &&
            asText.TrimStart().StartsWith("{");

        if (looksLikeJson)
        {
            WsControlMessage msg = null;

            try
            {
                msg = JsonUtility.FromJson<WsControlMessage>(asText);
            }
            catch
            {
                msg = null;
            }

            if (msg != null && !string.IsNullOrEmpty(msg.type))
            {
                HandleControlMessage(msg);
                return;
            }
        }

        // Not a recognizable JSON control message -> this is the binary
        // MP3 payload for the most recently announced "audio" message.
        if (pendingAudioMeta != null)
        {
            var meta = pendingAudioMeta;
            pendingAudioMeta = null;

            OnAudioChunk?.Invoke(meta.requestId, meta.sequence, rawBytes);
        }
        else
        {
            Debug.LogWarning("⚠️ Received audio bytes with no pending 'audio' metadata; dropped.");
        }
    }

    void HandleControlMessage(WsControlMessage msg)
    {
        switch (msg.type)
        {
            case "start":
                CurrentRequestId = msg.request_id;
                OnStart?.Invoke(msg.request_id);
                break;

            case "text":
                OnSentenceText?.Invoke(msg.request_id, msg.sequence, msg.text);
                break;

            case "audio":
                pendingAudioMeta = new PendingAudioMeta
                {
                    requestId = msg.request_id,
                    sequence = msg.sequence
                };
                break;

            case "complete":
                OnComplete?.Invoke(msg.request_id, msg.total_sequences);
                break;

            case "cancelled":
                OnCancelled?.Invoke(msg.request_id);
                break;

            case "error":
                OnError?.Invoke(msg.request_id, msg.message);
                break;

            default:
                Debug.LogWarning("Unknown WS control message type: " + msg.type);
                break;
        }
    }

    // =====================================================
    // MESSAGE DTOs
    // =====================================================

    [Serializable]
    class WsControlMessage
    {
        public string type;
        public string request_id;
        public int sequence;
        public string text;
        public string message;
        public int size;
        public int total_sequences;
    }

    [Serializable]
    class WsOutgoingRequest
    {
        public string type;
        public string text;
        public string lang;
    }

    [Serializable]
    class WsOutgoingCancel
    {
        public string type;
    }
}
