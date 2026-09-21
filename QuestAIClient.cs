using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using Meta.WitAi;
using UnityEngine.InputSystem;
using System;
using System.Collections.Generic;


[RequireComponent(typeof(AudioSource))]
public class QuestAIClient : MonoBehaviour
{
    
    [Header("🌐 Server")]
    public string askURL =
        "https://web-production-733e5.up.railway.app/ask";

    public string ttsURL =
        "https://web-production-733e5.up.railway.app/tts";

    [Header("🔐 API")]
    public string apiKey = "SECRET123";

    [Header("🎤 Voice SDK")]
    public VoiceService voiceSDK;

    [Header("🔊 Audio")]
    public AudioSource audioSource;

    [Header("🎧 Music Manager")]
    public AudioDuckingManager duckingManager;

    [Header("🌊 Streaming AI (WebSocket)")]
    public AIWebSocketService aiSocket;
    public StreamingAudioQueuePlayer streamPlayer;

    [Header("🎙 Voice Settings")]
    public float voiceThreshold = 0.3f;
    public float silenceDelay = 1f;
    public float checkRate = 0.05f;

    bool systemEnabled = false;
    bool isWaitingResponse = false;
    bool isPlayingAI = false;

    bool lastButtonState = false;
    bool voiceDetected = false;
    bool characterSessionStarted = false;

    float micStartTime = 0f;
    float lastVoiceTime = 0f;
    float voiceHoldTime = 0.12f;
    float voiceStartTimer = 0f;

    string latestText = "";

    Coroutine silenceCoroutine;
    Coroutine currentProcessCoroutine;

    // =====================================================
    // STREAMING STATE (WebSocket AI pipeline)
    // =====================================================

    string currentWsRequestId = "";
    bool streamingStarted = false;
    bool streamingComplete = false;
    bool streamingCancelledFlag = false;

    // =====================================================
    // RESPONSE INTERRUPT CONTROL
    // =====================================================

    bool responseCancelled = false;

    // رقم لكل طلب.
    // لو الطلب القديم اتلغى، أي نتيجة ترجع منه يتم تجاهلها.
    int requestGeneration = 0;

    UnityWebRequest currentWebRequest = null;

    System.Random thinkingRandom =
        new System.Random();

    string CacheFolder =>
        Path.Combine(
            Application.persistentDataPath,
            "voice_cache"
        );

    // ================= SEMANTIC CACHE =================

    class CachedQuestion
    {
        public string text;
        public string filePath;
    }

    List<CachedQuestion> questionCache =
        new List<CachedQuestion>();


    // =====================================================
    // SIMILARITY
    // =====================================================

    float GetSimilarity(string a, string b)
    {
        bool isChinese =
            Regex.IsMatch(a, @"[\u4e00-\u9fff]") ||
            Regex.IsMatch(b, @"[\u4e00-\u9fff]");

        a = NormalizeText(a);
        b = NormalizeText(b);

        if (isChinese)
        {
            var aChars =
                new HashSet<char>(
                    a.Replace(" ", "")
                );

            var bChars =
                new HashSet<char>(
                    b.Replace(" ", "")
                );

            if (aChars.Count == 0 ||
                bChars.Count == 0)
                return 0;

            int match = 0;

            foreach (var c in aChars)
            {
                if (bChars.Contains(c))
                    match++;
            }

            return (float)match /
                Mathf.Max(
                    aChars.Count,
                    bChars.Count
                );
        }

        var aWords =
            new HashSet<string>(
                a.Split(' ')
            );

        var bWords =
            new HashSet<string>(
                b.Split(' ')
            );

        if (aWords.Count == 0 ||
            bWords.Count == 0)
            return 0;

        int wordMatch = 0;

        foreach (var w in aWords)
        {
            if (bWords.Contains(w))
                wordMatch++;
        }

        return (float)wordMatch /
            (aWords.Count +
             bWords.Count -
             wordMatch);
    }


    // =====================================================
    // FIND SEMANTIC CACHE
    // =====================================================

    string FindCachedAnswer(string text)
    {
        foreach (var item in questionCache)
        {
            float sim =
                GetSimilarity(
                    NormalizeText(text),
                    NormalizeText(item.text)
                );

            if (sim >= 0.7f)
            {
                Debug.Log(
                    "🔥 SEMANTIC CACHE HIT: " + sim
                );

                if (File.Exists(item.filePath))
                    return item.filePath;
            }
        }

        return null;
    }


    // =====================================================
    // STOP COMMAND DETECTION
    // =====================================================

    bool IsStopCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string t =
            text.Trim().ToLower();

        // إزالة علامات الترقيم
        t = Regex.Replace(
            t,
            @"[^\w\s\u0600-\u06FF\u4e00-\u9fffäöüßÄÖÜ]",
            " "
        );

        t = Regex.Replace(
            t,
            @"\s+",
            " "
        ).Trim();

        // =================================================
        // ARABIC
        // =================================================

        string[] arabicStops =
        {
            "اسكت",
            "اسكت خلاص",
            "اسكت بقى",
            "خلاص اسكت",
            "خلاص",
            "كفاية",
            "كفايه",
            "كفاية بقى",
            "كفايه بقى",
            "وقف",
            "وقف خلاص",
            "خلاص وقف",
            "توقف",
            "توقف خلاص",
            "بس",
            "بس خلاص",
            "متتكلمش",
            "ما تتكلمش",
            "لا تتكلم",
            "اسكتي",
            "اسكت يا رمسيس"
        };

        foreach (string command in arabicStops)
        {
            if (t == command)
                return true;
        }

        // =================================================
        // ENGLISH
        // =================================================

        string[] englishStops =
        {
            "stop",
            "stop talking",
            "stop speaking",
            "shut up",
            "be quiet",
            "quiet",
            "enough",
            "that's enough",
            "thats enough",
            "stop it",
            "please stop",
            "silence"
        };

        foreach (string command in englishStops)
        {
            if (t == command)
                return true;
        }

        // =================================================
        // GERMAN
        // =================================================

        string[] germanStops =
        {
            "stopp",
            "stop",
            "hör auf",
            "hoer auf",
            "hör auf zu reden",
            "hoer auf zu reden",
            "sei still",
            "halt",
            "genug",
            "das reicht",
            "ruhe",
            "sei ruhig"
        };

        foreach (string command in germanStops)
        {
            if (t == command)
                return true;
        }

        // =================================================
        // CHINESE
        // =================================================

        string[] chineseStops =
        {
            "停止",
            "停",
            "别说了",
            "不要说了",
            "别说",
            "安静",
            "够了",
            "够",
            "闭嘴",
            "停止说话"
        };

        foreach (string command in chineseStops)
        {
            if (t == command)
                return true;
        }

        return false;
    }


    // =====================================================
    // IMMEDIATE STOP EVERYTHING
    // =====================================================

    void InterruptAI()
    {
        Debug.Log(
            "🛑 AI INTERRUPTED BY USER"
        );

        // إلغاء أي طلب حالي
        responseCancelled = true;

        // تغيير رقم الطلب حتى أي نتيجة قديمة يتم تجاهلها
        requestGeneration++;

        // إلغاء أي stream حالي عبر الـWebSocket
        streamingCancelledFlag = true;
        aiSocket?.SendCancel();
        streamPlayer?.CancelPlayback();
        currentWsRequestId = "";

        // إلغاء UnityWebRequest الحالي (الطلبات القديمة عبر HTTP: fixed reply / thinking / semantic cache)
        if (currentWebRequest != null)
        {
            try
            {
                currentWebRequest.Abort();
            }
            catch
            {
            }

            currentWebRequest = null;
        }

        // وقف أي صوت فورًا
        if (audioSource != null)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }

        // إلغاء حالة الكلام
        isPlayingAI = false;
        isWaitingResponse = false;
        characterSessionStarted = false;

        // مهم:
        // لا نعيد السؤال القديم
        latestText = "";
        voiceDetected = false;
        voiceStartTimer = 0f;

        // إيقاف Coroutine الخاصة بالطلب
        if (currentProcessCoroutine != null)
        {
            StopCoroutine(
                currentProcessCoroutine
            );

            currentProcessCoroutine = null;
        }

        // الموسيقى ترجع فورًا
        duckingManager?.OnCharacterFinishedTalking();

        // تأكد أن الـ voice SDK متوقف لحظة
        if (voiceSDK != null)
        {
            voiceSDK.Deactivate();
        }

        // شغل المايك من جديد بعد لحظة صغيرة
        StartCoroutine(
            RestartListeningAfterInterrupt()
        );
    }


    // =====================================================
    // RESTART MIC AFTER STOP
    // =====================================================

    IEnumerator RestartListeningAfterInterrupt()
    {
        yield return new WaitForSeconds(0.15f);

        if (!systemEnabled)
            yield break;

        if (voiceSDK == null)
            yield break;

        latestText = "";
        voiceDetected = false;
        voiceStartTimer = 0f;

        micStartTime = Time.time;

        yield return new WaitForSeconds(0.2f);

        if (!isPlayingAI &&
            !isWaitingResponse &&
            systemEnabled)
        {
            voiceSDK.ActivateImmediately();

            Debug.Log(
                "🎤 MIC READY AFTER INTERRUPT"
            );
        }
    }


    // =====================================================
    // SERVER ASK (LEGACY HTTP PATH - KEPT AS MANUAL FALLBACK)
    // =====================================================
    //
    // This coroutine talks to the original /ask REST endpoint and is no
    // longer used by ProcessQuestion() for the main AI reply (that now
    // goes through the WebSocket streaming pipeline below). It is left
    // in place, untouched, in case a manual HTTP fallback is ever needed
    // (e.g. WebSocket completely unavailable on a given network).
    // =====================================================================

    IEnumerator AskServer(
        string text,
        Action<string> onDone
    )
    {
        int myGeneration =
            requestGeneration;

        Debug.Log(
            "🌐 SENDING TO SERVER: " +
            text
        );

        if (responseCancelled ||
            myGeneration != requestGeneration)
        {
            onDone(null);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text) ||
            text.Length < 2)
        {
            Debug.Log(
                "Blocked empty/noise request"
            );

            onDone(null);
            yield break;
        }

        // =================================================
        // SERVER CACHE
        // =================================================

        string cachedPath =
            Path.Combine(
                CacheFolder,
                "ask_" +
                text.GetHashCode() +
                ".mp3"
            );

        if (File.Exists(cachedPath))
        {
            if (responseCancelled ||
                myGeneration != requestGeneration)
            {
                onDone(null);
                yield break;
            }

            Debug.Log(
                "⚡ SERVER CACHE HIT"
            );

            onDone(cachedPath);
            yield break;
        }

        WWWForm form =
            new WWWForm();

        form.AddField(
            "text",
            text
        );

        form.AddField(
            "lang",
            "ar"
        );

        using (
            UnityWebRequest www =
            UnityWebRequest.Post(
                askURL,
                form
            )
        )
        {
            currentWebRequest = www;

            www.SetRequestHeader(
                "x-api-key",
                apiKey
            );

            yield return www.SendWebRequest();

            if (currentWebRequest == www)
                currentWebRequest = null;

            // =================================================
            // REQUEST WAS CANCELLED
            // =================================================

            if (responseCancelled ||
                myGeneration != requestGeneration)
            {
                Debug.Log(
                    "🛑 Server response ignored - request cancelled"
                );

                onDone(null);
                yield break;
            }

            if (
                www.result !=
                UnityWebRequest.Result.Success
            )
            {
                Debug.LogError(
                    "Server error: " +
                    www.error
                );

                onDone(null);
                yield break;
            }

            byte[] data =
                www.downloadHandler.data;

            if (data == null ||
                data.Length < 1000)
            {
                Debug.LogError(
                    "Invalid audio response"
                );

                onDone(null);
                yield break;
            }

            // لا تحفظ الرد لو المستخدم أوقفه
            if (responseCancelled ||
                myGeneration != requestGeneration)
            {
                onDone(null);
                yield break;
            }

            File.WriteAllBytes(
                cachedPath,
                data
            );

            onDone(cachedPath);
        }
    }


    // =====================================================
    // AWAKE
    // =====================================================

    void Awake()
    {
        Directory.CreateDirectory(
            CacheFolder
        );

        Debug.Log(
            "CACHE PATH: " +
            CacheFolder
        );

        if (audioSource == null)
            audioSource =
                GetComponent<AudioSource>();

        if (voiceSDK == null)
        {
            voiceSDK =
                FindAnyObjectByType<VoiceService>();
        }

        if (duckingManager == null)
        {
            duckingManager =
                FindAnyObjectByType<AudioDuckingManager>();
        }

        if (aiSocket == null)
        {
            aiSocket =
                FindAnyObjectByType<AIWebSocketService>();
        }

        if (streamPlayer == null)
        {
            streamPlayer =
                FindAnyObjectByType<StreamingAudioQueuePlayer>();
        }

        if (duckingManager != null)
        {
            duckingManager.BeginWarmup();
        }

        if (voiceSDK != null)
        {
            voiceSDK.VoiceEvents
                .OnPartialTranscription
                .AddListener(
                    OnPartialText
                );

            voiceSDK.VoiceEvents
                .OnFullTranscription
                .AddListener(
                    OnFinalText
                );

            voiceSDK.VoiceEvents
                .OnMicLevelChanged
                .AddListener(
                    OnMicLevelChanged
                );
        }

        if (aiSocket != null)
        {
            aiSocket.OnStart += HandleWsStart;
            aiSocket.OnSentenceText += HandleWsSentenceText;
            aiSocket.OnAudioChunk += HandleWsAudioChunk;
            aiSocket.OnComplete += HandleWsComplete;
            aiSocket.OnCancelled += HandleWsCancelled;
            aiSocket.OnError += HandleWsError;
        }

        if (streamPlayer != null)
        {
            streamPlayer.OnPlaybackStarted += HandleStreamPlaybackStarted;
            streamPlayer.OnPlaybackFinished += HandleStreamPlaybackFinished;
        }
    }


    void Start()
    {
        // نفتح اتصال الـWebSocket من بدري عشان يكون جاهز لحظة أول سؤال
        // (بدل ما نستنى الاتصال أثناء أول طلب فعلي، وده بيوفر وقت اتصال كامل).
        aiSocket?.Connect();
    }


    void OnDestroy()
    {
        if (aiSocket != null)
        {
            aiSocket.OnStart -= HandleWsStart;
            aiSocket.OnSentenceText -= HandleWsSentenceText;
            aiSocket.OnAudioChunk -= HandleWsAudioChunk;
            aiSocket.OnComplete -= HandleWsComplete;
            aiSocket.OnCancelled -= HandleWsCancelled;
            aiSocket.OnError -= HandleWsError;
        }

        if (streamPlayer != null)
        {
            streamPlayer.OnPlaybackStarted -= HandleStreamPlaybackStarted;
            streamPlayer.OnPlaybackFinished -= HandleStreamPlaybackFinished;
        }
    }


    // =====================================================
    // WEBSOCKET EVENT HANDLERS
    // =====================================================

    void HandleWsStart(string requestId)
    {
        currentWsRequestId = requestId;
        streamPlayer?.BeginNewRequest(requestId);
        streamingStarted = true;

        Debug.Log("🟢 AI STREAM STARTED: " + requestId);
    }

    void HandleWsSentenceText(string requestId, int sequence, string sentenceText)
    {
        if (requestId != currentWsRequestId)
            return;

        Debug.Log("📝 [" + sequence + "] " + sentenceText);
    }

    void HandleWsAudioChunk(string requestId, int sequence, byte[] mp3Bytes)
    {
        if (requestId != currentWsRequestId)
            return;

        streamPlayer?.EnqueueAudio(requestId, sequence, mp3Bytes);
    }

    void HandleWsComplete(string requestId, int totalSequences)
    {
        if (requestId != currentWsRequestId)
            return;

        streamPlayer?.MarkComplete(requestId, totalSequences);
    }

    void HandleWsCancelled(string requestId)
    {
        if (requestId != currentWsRequestId)
            return;

        streamingCancelledFlag = true;
    }

    void HandleWsError(string requestId, string message)
    {
        Debug.LogError("AI WS error [" + requestId + "]: " + message);

        if (string.IsNullOrEmpty(requestId) || requestId == currentWsRequestId)
        {
            streamingCancelledFlag = true;
        }
    }

    void HandleStreamPlaybackStarted()
    {
        isPlayingAI = true;

        duckingManager?.OnCharacterStartedTalking();
        characterSessionStarted = true;

        if (voiceSDK != null && !voiceSDK.Active)
        {
            // مهم: نسيب المايك شغال أثناء الكلام عشان نقدر نسمع "اسكت".
            voiceSDK.ActivateImmediately();
        }
    }

    void HandleStreamPlaybackFinished()
    {
        isPlayingAI = false;
        streamingComplete = true;
    }


    // =====================================================
    // UPDATE
    // =====================================================

    void Update()
    {
        var device =
            UnityEngine.XR.InputDevices
                .GetDeviceAtXRNode(
                    UnityEngine.XR.XRNode.RightHand
                );

        device.TryGetFeatureValue(
            UnityEngine.XR.CommonUsages.primaryButton,
            out bool vrPressed
        );

        bool keyboardPressed =
            Keyboard.current != null &&
            Keyboard.current.aKey.wasPressedThisFrame;

        if (
            (vrPressed || keyboardPressed) &&
            !lastButtonState
        )
        {
            systemEnabled = true;

            StartListening();
        }

        lastButtonState =
            vrPressed || keyboardPressed;
    }


    // =====================================================
    // START LISTENING
    // =====================================================

    public void StartListening()
    {
        if (!systemEnabled)
            return;

        if (isWaitingResponse)
            return;

        if (isPlayingAI)
            return;

        if (voiceSDK == null)
            return;

        if (voiceSDK.Active)
            return;

        if (audioSource != null &&
            audioSource.isPlaying)
            return;

        latestText = "";

        voiceDetected = false;

        lastVoiceTime =
            Time.time;

        micStartTime =
            Time.time;

        voiceStartTimer = 0f;

        if (silenceCoroutine != null)
        {
            StopCoroutine(
                silenceCoroutine
            );
        }

        silenceCoroutine =
            StartCoroutine(
                SilenceWatcher()
            );

        StartCoroutine(
            DelayedMicStart()
        );
    }


    // =====================================================
    // DELAYED MIC
    // =====================================================

    IEnumerator DelayedMicStart()
    {
        yield return new WaitForSeconds(
            0.3f
        );

        if (
            !isPlayingAI &&
            !isWaitingResponse &&
            systemEnabled &&
            voiceSDK != null
        )
        {
            micStartTime =
                Time.time;

            voiceSDK.ActivateImmediately();
        }
    }


    // =====================================================
    // MIC LEVEL
    // =====================================================

    void OnMicLevelChanged(float level)
    {
        if (voiceSDK == null)
            return;

        if (!voiceSDK.Active)
            return;

        // مهم:
        // هنا لا نوقف الاستماع أثناء AI.
        // لأننا نريد التقاط "اسكت / كفاية".
        //
        // لكننا لا نستخدم مستوى المايك أثناء AI
        // في حساب بداية سؤال جديد.

        if (isPlayingAI)
            return;

        if (audioSource != null &&
            audioSource.isPlaying)
            return;

        if (
            Time.time -
            micStartTime <
            0.25f
        )
            return;

        if (level < voiceThreshold)
        {
            voiceStartTimer = 0f;
            return;
        }

        voiceStartTimer +=
            Time.deltaTime;

        if (
            voiceStartTimer >=
            voiceHoldTime
        )
        {
            lastVoiceTime =
                Time.time;

            if (!voiceDetected)
            {
                voiceDetected = true;

                Debug.Log(
                    "🎤 USER STARTED TALKING"
                );

                duckingManager?.OnPlayerStartedTalking();
            }
        }
    }


    // =====================================================
    // PARTIAL TRANSCRIPTION
    // =====================================================

    void OnPartialText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        text = text.Trim();

        // =================================================
        // أثناء كلام الـAI:
        // نسمح فقط بأوامر الإيقاف.
        // =================================================

        if (isPlayingAI)
        {
            if (IsStopCommand(text))
            {
                Debug.Log(
                    "🛑 STOP COMMAND DETECTED: " +
                    text
                );

                InterruptAI();
            }

            return;
        }

        if (isWaitingResponse)
            return;

        latestText =
            text;

        lastVoiceTime =
            Time.time;
    }


    // =====================================================
    // FINAL TRANSCRIPTION
    // =====================================================

    void OnFinalText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        text = text.Trim();

        // =================================================
        // أثناء AI:
        // أي كلام = نفحص فقط هل هو Stop Command
        // =================================================

        if (isPlayingAI)
        {
            if (IsStopCommand(text))
            {
                Debug.Log(
                    "🛑 STOP COMMAND DETECTED: " +
                    text
                );

                InterruptAI();
            }

            return;
        }

        if (isWaitingResponse)
            return;

        if (text.Length < 2)
            return;

        // =================================================
        // لو المستخدم قال أمر إيقاف وهو ليس أثناء AI
        // لا نرسل الأمر للسيرفر.
        // =================================================

        if (IsStopCommand(text))
        {
            latestText = "";
            voiceDetected = false;
            return;
        }

        latestText =
            text;

        lastVoiceTime =
            Time.time;

        duckingManager?.OnPlayerStartedTalking();
    }


    // =====================================================
    // SILENCE WATCHER
    // =====================================================

    IEnumerator SilenceWatcher()
    {
        while (systemEnabled)
        {
            // أثناء AI لا نستخدم SilenceWatcher
            // لمعالجة سؤال جديد.
            if (
                !isPlayingAI &&
                !isWaitingResponse &&
                voiceDetected &&
                !string.IsNullOrWhiteSpace(
                    latestText
                ) &&
                Time.time -
                lastVoiceTime >=
                silenceDelay
            )
            {
                voiceDetected = false;

                string question =
                    latestText;

                latestText = "";

                currentProcessCoroutine =
                    StartCoroutine(
                        ProcessQuestion(
                            question
                        )
                    );
            }

            yield return new WaitForSeconds(
                checkRate
            );
        }
    }


    // =====================================================
    // PROCESS QUESTION
    // =====================================================

    IEnumerator ProcessQuestion(string text)
    {
        isWaitingResponse = true;

        responseCancelled = false;

        // رقم جديد لهذا الطلب
        requestGeneration++;

        int myGeneration =
            requestGeneration;

        if (voiceSDK != null)
            voiceSDK.Deactivate();

        latestText = "";
        voiceDetected = false;

        // =================================================
        // INVALID QUESTION
        // =================================================

        if (
            string.IsNullOrWhiteSpace(text) ||
            text.Trim().Length < 2
        )
        {
            isWaitingResponse = false;

            if (systemEnabled &&
                voiceSDK != null)
            {
                voiceSDK.ActivateImmediately();
            }

            yield break;
        }

        text = text.Trim();

        // =================================================
        // FIXED REPLY
        // =================================================

        string fixedReply =
            BuildInstantReply(text);

        if (!string.IsNullOrEmpty(fixedReply))
        {
            if (
                responseCancelled ||
                myGeneration != requestGeneration
            )
            {
                yield break;
            }

            yield return StartCoroutine(
                PlayTextCached(
                    fixedReply,
                    "fixed_" +
                    fixedReply.GetHashCode()
                )
            );

            if (
                responseCancelled ||
                myGeneration != requestGeneration
            )
            {
                yield break;
            }

            currentProcessCoroutine =
                StartCoroutine(
                    FinishRequest()
                );

            yield break;
        }


        // =================================================
        // SEMANTIC CACHE
        // =================================================

        string semanticCached =
            FindCachedAnswer(text);

        if (!string.IsNullOrEmpty(
            semanticCached
        ))
        {
            if (
                responseCancelled ||
                myGeneration != requestGeneration
            )
            {
                yield break;
            }

            yield return StartCoroutine(
                PlayFile(
                    semanticCached
                )
            );

            if (
                responseCancelled ||
                myGeneration != requestGeneration
            )
            {
                yield break;
            }

            currentProcessCoroutine =
                StartCoroutine(
                    FinishRequest()
                );

            yield break;
        }


        // =================================================
        // STREAMING AI REQUEST (WebSocket)
        //
        // Flow:
        //   1. Make sure the WebSocket is connected.
        //   2. Send the "request" message.
        //   3. While waiting for the first audio to start, optionally
        //      play one short transitional filler line (existing
        //      BuildThinkingText / PlayTextCached path) if latency is
        //      noticeable - never longer than one line, and never
        //      forced to finish once real audio is ready to take over.
        //   4. Wait until StreamingAudioQueuePlayer reports playback of
        //      the whole response is finished.
        // =================================================

        if (aiSocket == null || streamPlayer == null)
        {
            Debug.LogError(
                "AI streaming components not wired up (aiSocket / streamPlayer)."
            );

            currentProcessCoroutine =
                StartCoroutine(
                    FinishRequest()
                );

            yield break;
        }

        if (!aiSocket.IsConnected)
        {
            aiSocket.Connect();

            float connectWaitStart = Time.time;

            while (
                !aiSocket.IsConnected &&
                Time.time - connectWaitStart < 3f
            )
            {
                if (responseCancelled ||
                    myGeneration != requestGeneration)
                {
                    yield break;
                }

                yield return null;
            }

            if (!aiSocket.IsConnected)
            {
                Debug.LogError(
                    "Could not establish AI WebSocket connection in time."
                );

                currentProcessCoroutine =
                    StartCoroutine(
                        FinishRequest()
                    );

                yield break;
            }
        }

        streamingStarted = false;
        streamingComplete = false;
        streamingCancelledFlag = false;
        currentWsRequestId = "";

        aiSocket.SendRequest(text, "ar");

        float requestSentTime = Time.time;
        bool fillerAttempted = false;

        // =================================================
        // WAIT FOR STREAM TO START (with optional transitional filler)
        // =================================================

        while (!streamingStarted && !streamingCancelledFlag)
        {
            if (
                responseCancelled ||
                myGeneration != requestGeneration
            )
            {
                yield break;
            }

            if (!fillerAttempted && Time.time - requestSentTime >= 1.2f)
            {
                fillerAttempted = true;

                string thinkingText =
                    BuildThinkingText(text);

                yield return StartCoroutine(
                    PlayTextCached(
                        thinkingText,
                        "thinking_" +
                        thinkingText.GetHashCode()
                    )
                );

                if (
                    responseCancelled ||
                    myGeneration != requestGeneration
                )
                {
                    yield break;
                }
            }

            yield return null;
        }

        if (
            responseCancelled ||
            myGeneration != requestGeneration
        )
        {
            yield break;
        }

        if (streamingCancelledFlag)
        {
            currentProcessCoroutine =
                StartCoroutine(
                    FinishRequest()
                );

            yield break;
        }

        // =================================================
        // WAIT FOR STREAMING PLAYBACK TO FULLY FINISH
        //
        // StreamingAudioQueuePlayer starts playing the first sentence's
        // audio as soon as it is decoded (it will wait for the current
        // AudioSource to be free first - e.g. if a filler line above is
        // still finishing its own sentence - then take over immediately,
        // without waiting for the whole AI response to be ready).
        // =================================================

        while (!streamingComplete && !streamingCancelledFlag)
        {
            if (
                responseCancelled ||
                myGeneration != requestGeneration
            )
            {
                yield break;
            }

            yield return null;
        }

        if (
            responseCancelled ||
            myGeneration != requestGeneration
        )
        {
            yield break;
        }


        // =================================================
        // SAVE SEMANTIC CACHE
        //
        // Note: the streamed response is played sentence-by-sentence and
        // is not written to a single combined mp3 file, so it cannot be
        // added to the semantic cache the same way the old single-file
        // /ask response was. The semantic cache still works fully for
        // fixed replies and any previously-cached streamed answers are
        // simply re-streamed (still fast, since TTFA stays low).
        // =================================================

        currentProcessCoroutine =
            StartCoroutine(
                FinishRequest()
            );
    }


    // =====================================================
    // FINISH REQUEST
    // =====================================================

    IEnumerator FinishRequest()
    {
        if (responseCancelled)
            yield break;

        isWaitingResponse = false;

        if (characterSessionStarted)
        {
            duckingManager?.OnCharacterFinishedTalking();

            characterSessionStarted =
                false;
        }

        yield return new WaitForSeconds(
            0.3f
        );

        if (voiceSDK != null)
            voiceSDK.Deactivate();

        yield return new WaitForSeconds(
            0.5f
        );

        if (
            systemEnabled &&
            !responseCancelled &&
            !isPlayingAI &&
            !isWaitingResponse &&
            voiceSDK != null
        )
        {
            voiceSDK.ActivateImmediately();
        }

        currentProcessCoroutine = null;
    }


    // =====================================================
    // PLAY TEXT CACHED
    // =====================================================

    IEnumerator PlayTextCached(
        string text,
        string fileName
    )
    {
        if (responseCancelled)
            yield break;

        string path =
            Path.Combine(
                CacheFolder,
                fileName +
                ".mp3"
            );

        // =================================================
        // DOWNLOAD TTS
        // =================================================

        if (!File.Exists(path))
        {
            WWWForm form =
                new WWWForm();

            form.AddField(
                "text",
                text
            );

            using (
                UnityWebRequest www =
                    UnityWebRequest.Post(
                        ttsURL,
                        form
                    )
            )
            {
                currentWebRequest = www;

                www.SetRequestHeader(
                    "x-api-key",
                    apiKey
                );

                yield return www.SendWebRequest();

                if (currentWebRequest == www)
                    currentWebRequest = null;

                // لو اتقال اسكت أثناء تحميل الـTTS
                if (responseCancelled)
                {
                    Debug.Log(
                        "🛑 TTS DOWNLOAD CANCELLED"
                    );

                    yield break;
                }

                if (
                    www.result ==
                    UnityWebRequest.Result.Success
                )
                {
                    if (!responseCancelled)
                    {
                        File.WriteAllBytes(
                            path,
                            www.downloadHandler.data
                        );
                    }
                }
                else
                {
                    yield break;
                }
            }
        }


        // =================================================
        // PLAY
        // =================================================

        if (responseCancelled)
            yield break;

        yield return StartCoroutine(
            PlayFile(path)
        );
    }


    // =====================================================
    // PLAY FILE
    // =====================================================

    IEnumerator PlayFile(string path)
    {
        if (responseCancelled)
            yield break;

        isPlayingAI = true;

        // =================================================
        // مهم جدًا:
        //
        // لا نعمل Deactivate هنا.
        //
        // السبب:
        // لازم المايك يظل قادرًا على سماع:
        //
        // اسكت
        // كفاية
        // stop
        // stopp
        // 停止
        //
        // أثناء كلام الـAI.
        // =================================================

        if (voiceSDK != null &&
            !voiceSDK.Active)
        {
            voiceSDK.ActivateImmediately();
        }

        int myGeneration =
            requestGeneration;

        using (
            UnityWebRequest www =
                UnityWebRequestMultimedia.GetAudioClip(
                    "file://" + path,
                    AudioType.MPEG
                )
        )
        {
            currentWebRequest = www;

            yield return www.SendWebRequest();

            if (currentWebRequest == www)
                currentWebRequest = null;

            if (
                responseCancelled ||
                myGeneration != requestGeneration
            )
            {
                isPlayingAI = false;

                yield break;
            }

            if (
                www.result !=
                UnityWebRequest.Result.Success
            )
            {
                isPlayingAI = false;

                yield break;
            }

            AudioClip clip =
                DownloadHandlerAudioClip
                    .GetContent(www);

            if (
                responseCancelled ||
                myGeneration != requestGeneration
            )
            {
                isPlayingAI = false;

                yield break;
            }

            // =================================================
            // AI STARTED TALKING
            // =================================================

            duckingManager?.OnCharacterStartedTalking();

            characterSessionStarted =
                true;

            if (audioSource != null)
            {
                audioSource.Stop();

                audioSource.clip =
                    clip;

                audioSource.Play();
            }


            // =================================================
            // WAIT UNTIL AUDIO FINISHES
            // =================================================

            while (
                audioSource != null &&
                audioSource.isPlaying
            )
            {
                // =================================================
                // أهم جزء:
                //
                // لو قال المستخدم اسكت:
                // InterruptAI()
                // ستعمل Stop للصوت فورًا.
                //
                // وبالتالي isPlayingAI تصبح false
                // ونخرج من الحلقة فورًا.
                // =================================================

                if (responseCancelled)
                {
                    Debug.Log(
                        "🛑 AUDIO PLAYBACK INTERRUPTED"
                    );

                    yield break;
                }

                yield return null;
            }
        }


        // =================================================
        // AI FINISHED
        // =================================================

        if (!responseCancelled)
        {
            duckingManager?.OnCharacterFinishedTalking();

            characterSessionStarted =
                false;
        }

        isPlayingAI = false;


        // =================================================
        // WAIT BEFORE LISTENING
        // =================================================

        if (!responseCancelled)
        {
            yield return new WaitForSeconds(
                1.2f
            );

            if (systemEnabled)
            {
                yield return new WaitForSeconds(
                    0.3f
                );

                if (
                    !isPlayingAI &&
                    !responseCancelled &&
                    voiceSDK != null
                )
                {
                    voiceSDK.ActivateImmediately();
                }
            }
        }
    }


    // =====================================================
    // NORMALIZE TEXT
    // =====================================================

    string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        text =
            text.ToLower();

        text =
            Regex.Replace(
                text,
                @"[^\w\s\u0600-\u06FF\u4e00-\u9fff]",
                " "
            );

        string[] stopWords =
        {
            // Arabic
            "في",
            "من",
            "على",
            "الى",
            "إلى",
            "عن",
            "مع",
            "ان",
            "أن",
            "هذا",
            "هذه",
            "ذلك",
            "تلك",
            "هل",
            "هو",
            "هي",
            "ما",
            "لم",
            "لن",

            // English
            "in",
            "on",
            "at",
            "to",
            "from",
            "with",
            "about",
            "is",
            "are",
            "was",
            "were",
            "the",
            "a",
            "an",
            "of",
            "it",
            "this",
            "that",

            // German
            "in",
            "im",
            "an",
            "auf",
            "mit",
            "von",
            "zu",
            "der",
            "die",
            "das",
            "ist",
            "war",
            "ein",
            "eine",
            "und",
            "oder",

            // Chinese
            "的",
            "了",
            "是",
            "在",
            "有",
            "和",
            "呢",
            "吗",
            "吧",
            "我",
            "你",
            "他",
            "她",
            "它",
            "我们",
            "你们",
            "他们"
        };

        foreach (var w in stopWords)
        {
            text =
                Regex.Replace(
                    text,
                    "\\b" +
                    w +
                    "\\b",
                    " "
                );
        }

        text =
            Regex.Replace(
                text,
                @"\s+",
                " "
            ).Trim();

        return text;
    }


    // =====================================================
    // LANGUAGE DETECTION
    // =====================================================

    string DetectLanguage(string text)
    {
        if (
            Regex.IsMatch(
                text,
                @"[\u0600-\u06FF]"
            )
        )
            return "ar";

        if (
            Regex.IsMatch(
                text,
                @"[\u4e00-\u9fff]"
            )
        )
            return "cn";

        if (
            text.Contains("wie") ||
            text.Contains("hallo") ||
            text.Contains("und")
        )
            return "de";

        return "en";
    }


    // =====================================================
    // FIXED REPLY
    // =====================================================

    string BuildInstantReply(string text)
    {
        string t =
            text.ToLower();

        bool fixedQuestion =
            t.Contains("ازيك") ||
            t.Contains("عامل") ||
            t.Contains("كيف حال") ||
            t.Contains("عامل ايه") ||
            t.Contains("hello") ||
            t.Contains("hi") ||
            t.Contains("how are") ||
            t.Contains("wie geht") ||
            t.Contains("hallo") ||
            t.Contains("你好") ||
            t.Contains("你好吗");

        if (!fixedQuestion)
            return null;

        string lang =
            DetectLanguage(t);

        if (lang == "ar")
        {
            return
                "أنا بخير... كما ينبغي لمن حمل تاج مصر عبر آلاف السنين. ثابتٌ كأعمدة المعابد، وحاضرٌ كأن الزمن لم يجرؤ يومًا على التحرك أمام عظمة الفراعنة.";
        }

        if (lang == "de")
        {
            return
                "Mir geht es gut... so wie es sich für einen König Ägyptens gehört, der seit Jahrtausenden die Krone trägt. Standhaft wie die Säulen der Tempel und gegenwärtig, als hätte die Zeit selbst nie gewagt, sich vor der Größe der Pharaonen zu bewegen.";
        }

        if (lang == "cn")
        {
            return
                "我一切安好……正如一位承载埃及王冠数千年的法老应有的模样。坚定如神庙的石柱，仿佛时间本身也不敢在法老的伟大面前流逝。";
        }

        return
            "I am well... as a king of Egypt should be after bearing the crown through thousands of years. Steadfast like the pillars of the temples, present as though time itself never dared move before the greatness of the Pharaohs.";
    }


    // =====================================================
    // RANDOM
    // =====================================================

    string PickRandom(
        params string[] options
    )
    {
        return options[
            thinkingRandom.Next(
                options.Length
            )
        ];
    }


    // =====================================================
    // THINKING TEXT
    // =====================================================

    string BuildThinkingText(string text)
    {
        string t =
            text.ToLower();

        string lang =
            DetectLanguage(t);

        bool war =
            t.Contains("حرب") ||
            t.Contains("war") ||
            t.Contains("krieg") ||
            t.Contains("战争");

        bool health =
            t.Contains("صحة") ||
            t.Contains("health") ||
            t.Contains("gesund") ||
            t.Contains("健康");

        bool family =
            t.Contains("اسره") ||
            t.Contains("أسرة") ||
            t.Contains("family") ||
            t.Contains("familie") ||
            t.Contains("家庭");

        bool politics =
            t.Contains("حكم") ||
            t.Contains("politic") ||
            t.Contains("politik") ||
            t.Contains("政治");

        bool temples =
            t.Contains("معبد") ||
            t.Contains("temple") ||
            t.Contains("tempel") ||
            t.Contains("神庙");


        // =================================================
        // ARABIC
        // =================================================

        if (lang == "ar")
        {
            if (war)
            {
                return PickRandom(
                    "لقد وقفت يوماً أمام جيوش عظيمة تهتز لها الرمال تحت أقدام المحاربين، حيث تمتزج أصوات الحديد بصدى التاريخ، ولا يبقى في الميدان إلا من كتب اسمه بقوة لا تنكسر عبر الزمن.",

                    "الحروب لا تُحكى بالأرقام ولا تُختصر في انتصارات وهزائم، بل هي صفحات طويلة من دماء وشجاعة صنعت ملامح ممالك لا تزال آثارها شاهدة حتى اليوم.",

                    "رأيت رايات ترتفع فوق الصحارى الممتدة بلا نهاية، كأنها أرواح الملوك والفرسان القدامى تعود لتشهد لحظات لا ينجو منها إلا أصحاب الإرادة الصلبة.",

                    "في ساحات المعارك القديمة يولد المجد الحقيقي، حيث يُختبر الإنسان أمام قدره، ولا يخلد إلا من امتلك قلب ملك وروح محارب لا تعرف الانكسار."
                );
            }

            if (health)
            {
                return PickRandom(
                    "في قصور الملوك تعلمت أن الصحة ليست نعمة عابرة، بل هي تاج خفي يعلو رأس الإنسان دون أن يُرى، لكنه يحدد قدرته على الحكم والحياة والبقاء.",

                    "بين الحكماء والكهنة عرفت أن الجسد ليس مجرد مادة، بل معبد مقدس يرتبط بالروح والكون في توازن دقيق لا يدركه إلا من تعمق في حكمة القدماء.",

                    "علوم القدماء في الطب لم تكن مجرد وصفات، بل كانت فلسفة حياة كاملة تنظر للإنسان كجزء من الطبيعة والنجوم والمصير في آن واحد.",

                    "شفاء الجسد لا يبدأ من الدواء فقط، بل من صفاء الروح واستقامة الداخل، فكل اضطراب في النفس ينعكس على الجسد مهما بلغت قوة الإنسان."
                );
            }

            if (family)
            {
                return PickRandom(
                    "قبل أن تُبنى الممالك وتُرفع العروش، كانت الأسرة هي النواة الأولى لكل حضارة، ومنها خرجت القيم التي صنعت ملوكاً وحضارات لا تُنسى.",

                    "كل حضارة عظيمة بدأت من بيت صغير، حيث تُزرع المبادئ الأولى، وتتشكل أول ملامح القوة والانتماء التي تبقى عبر الأجيال.",

                    "الأسرة ليست مجرد روابط دم، بل هي أصل البناء الإنساني الذي تُبنى عليه الأمم وتستقر به الممالك وتستمر به الحياة.",

                    "داخل البيت يولد القادة، وتتشكل الأرواح التي تتحمل مسؤولية المستقبل، قبل أن ترى العروش أو تسمع أصوات الحكم."
                );
            }

            if (politics)
            {
                return PickRandom(
                    "الحكم مسؤولية عظيمة لا يليق بها الضعفاء، فهي عهد ثقيل يقوم على العدل قبل القوة، وعلى الحكمة قبل السلطة.",

                    "رأيت عروشاً تُبنى بكلمة صادقة، وأخرى تنهار بسبب ظلم واحد، فالكلمة في السياسة قد تصنع تاريخ أمة أو تمحوه.",

                    "السلطة ليست امتيازاً، بل اختبار حقيقي للإنسان أمام نفسه قبل أن يكون أمام شعبه، ومن يظلم فيها يخسر نفسه قبل أن يخسر عرشه.",

                    "من يجلس على العرش لا يحكم وحده، بل يحمل أمة كاملة على كتفيه، ويقف كل يوم أمام ميزان التاريخ بلا حماية سوى عدله."
                );
            }

            if (temples)
            {
                return PickRandom(
                    "بين الأعمدة المقدسة التي تعانق السماء، تقف المعابد كذاكرة حية لحضارة لم تمت، بل ما زالت تتنفس في صمت الحجر.",

                    "كل نقش محفور على جدران المعابد ليس مجرد زخرفة، بل قصة كاملة لملوك وآلهة وأزمنة صنعت مجداً لا يُمحى.",

                    "تحت ظلال الحجر المقدس كان الكهنة يقرأون الكون، وكأن المعابد نفسها كتاب مفتوح يشرح أسرار الخلود.",

                    "المعابد لم تُبنَ لتكون حجارة صامتة، بل لتكون جسراً بين الأرض والسماء، وبين الإنسان وما يظنه مستحيلاً."
                );
            }

            return PickRandom(
                "لقد وصلني سؤالك يا من تخاطب عرش المعرفة، وأنا رمسيس أجيبك الآن بوضوح كما تُقال الأحكام في حضرة الملوك، فالإجابة التي تبحث عنها قد اتضحت أمامي وتُمنح لك كما تُمنح الحقائق لمن يستحقها دون تأخير أو غموض. فها أنا أقدمه لك كما يُقدَّم القرار الملكي الحاسم، مباشرًا، واضحًا، لا يترك مجالًا للالتباس أو التردد.",

                "من قلب المعابد القديمة ومن بين سجلات الزمن أستقبل سؤالك يا من يسعى إلى المعرفة، وأنا رمسيس أكشف لك الجواب الآن كما تُكشف الحقائق في مجالس الملوك، واضحًا لا يشوبه غموض، مكتملًا لا ينقصه بيان، ومباشرًا كما يليق بمن جاء يبحث عن الحقيقة بثقة ويقين.",

                "لقد بلغني نداؤك يا من يقف أمام عرش الحكمة طالبًا للفهم، وأنا رمسيس أُخرج لك الإجابة من بين أسرار الزمن كما تُستخرج الكنوز من أعماق التاريخ، صريحة لا تعرف التردد، واضحة لا يحيط بها لبس، وكاملة كما تُعلن القرارات في حضرة الملوك العظام."
            );
        }


        // =================================================
        // ENGLISH
        // =================================================

        if (lang == "en")
        {
            if (war)
            {
                return PickRandom(
                    "I once stood before mighty armies where the sands trembled beneath the feet of warriors, where the clash of iron merged with the echo of history, and only those whose names were forged with unbreakable strength remained through time.",

                    "Wars are not told through numbers nor reduced to victories and defeats; they are long chapters of blood and courage that shaped kingdoms whose traces still stand witness today.",

                    "I saw banners rise above endless deserts, as though the spirits of ancient kings and knights had returned to witness moments survived only by those with unwavering will.",

                    "In the fields of ancient battles true glory is born, where a human soul is tested before destiny itself, and only those with the heart of a king and the spirit of an unbroken warrior are remembered forever."
                );
            }

            if (health)
            {
                return PickRandom(
                    "Within the palaces of kings I learned that health is not merely a passing blessing, but an invisible crown resting upon a person’s head, defining their power to rule, live, and endure.",

                    "Among sages and priests I discovered that the body is not mere matter, but a sacred temple connected to the soul and the universe in a delicate balance understood only by those who grasp the wisdom of the ancients.",

                    "The sciences of ancient medicine were never just remedies; they were a complete philosophy of life that viewed humanity as part of nature, the stars, and destiny itself.",

                    "The healing of the body does not begin with medicine alone, but with the purity of the soul and the harmony within, for every disturbance in the spirit reflects upon the body no matter how strong one may seem."
                );
            }

            if (family)
            {
                return PickRandom(
                    "Before kingdoms were built and thrones were raised, the family was the first foundation of every civilization, from which emerged the values that shaped unforgettable kings and empires.",

                    "Every great civilization began within a small home, where the first principles were planted and the earliest signs of strength and belonging were formed to endure through generations.",

                    "Family is not merely a bond of blood, but the origin of the human structure upon which nations are built, kingdoms find stability, and life itself continues.",

                    "Within the home leaders are born, and souls capable of carrying the weight of the future are shaped long before they ever see a throne or hear the voices of power."
                );
            }

            if (politics)
            {
                return PickRandom(
                    "Ruling is a great responsibility unworthy of the weak, for it is a heavy covenant built upon justice before power, and wisdom before authority.",

                    "I witnessed thrones built by a single truthful word, and others destroyed by a single act of injustice, for words in politics can create the history of a nation or erase it forever.",

                    "Power is not a privilege, but a true test of a person before themselves before it is before their people, and whoever becomes unjust within it loses themselves before losing their throne.",

                    "Whoever sits upon the throne does not rule alone, but carries an entire nation upon their shoulders, standing each day before the scales of history with no protection but their justice."
                );
            }

            if (temples)
            {
                return PickRandom(
                    "Between the sacred pillars that embrace the heavens, the temples stand as a living memory of a civilization that never died, but still breathes within the silence of stone.",

                    "Every carving etched upon the temple walls is not mere decoration, but a complete story of kings, gods, and ages that forged glory impossible to erase.",

                    "Beneath the shadows of sacred stone, the priests once read the universe itself, as though the temples were an open book revealing the secrets of eternity.",

                    "The temples were not built to be silent stones, but to serve as a bridge between earth and sky, between humanity and what it believes impossible."
                );
            }

            return PickRandom(
                "If you seek to understand the truth, listen as the wise listen to the inscriptions of ancient times, for every carving carries a secret the ages have preserved for those who can understand it.",

                "Not everything is grasped in haste; some meanings can only be seen by those who walk among the remnants of ancient eras with a thoughtful mind and a perceptive eye.",

                "I will guide you to the path, but know that knowledge is not taken by words alone, but through deep understanding, as temple priests comprehend the hidden secrets of their sanctuaries.",

                "Truth is not given; it is uncovered, like the secrets of kings revealed from ancient scrolls to those who can read beyond symbols."
            );
        }


        // =================================================
        // GERMAN
        // =================================================

        if (lang == "de")
        {
            if (war)
            {
                return PickRandom(
                    "Einst stand ich vor mächtigen Armeen, unter deren Schritten selbst der Sand bebte, wo das Klirren von Eisen mit dem Echo der Geschichte verschmolz und nur jene bestehen blieben, deren Namen mit unzerbrechlicher Stärke in die Zeit gemeißelt wurden.",

                    "Kriege werden nicht durch Zahlen erzählt und nicht auf Siege oder Niederlagen reduziert; sie sind lange Kapitel aus Blut und Mut, die Königreiche formten, deren Spuren bis heute Zeugnis ablegen.",

                    "Ich sah Banner über endlosen Wüsten aufsteigen, als wären die Geister alter Könige und Ritter zurückgekehrt, um Augenblicke zu bezeugen, die nur Menschen mit unbeugsamem Willen überleben konnten.",

                    "Auf den Schlachtfeldern der alten Welt wird wahrer Ruhm geboren, wo der Mensch seinem Schicksal gegenübertritt und nur jene verewigt werden, die das Herz eines Königs und den Geist eines ungebrochenen Kriegers besitzen."
                );
            }

            if (health)
            {
                return PickRandom(
                    "In den Palästen der Könige lernte ich, dass Gesundheit kein flüchtiger Segen ist, sondern eine unsichtbare Krone auf dem Haupt des Menschen, die seine Fähigkeit zu herrschen, zu leben und zu bestehen bestimmt.",

                    "Unter Weisen und Priestern erkannte ich, dass der Körper nicht bloße Materie ist, sondern ein heiliger Tempel, verbunden mit Seele und Universum in einem empfindlichen Gleichgewicht, das nur jene verstehen, die die Weisheit der Alten begreifen.",

                    "Die Heilkunst der Alten bestand nicht nur aus Rezepten, sondern war eine vollständige Lebensphilosophie, die den Menschen als Teil von Natur, Sternen und Schicksal zugleich betrachtete.",

                    "Die Heilung des Körpers beginnt nicht allein mit Medizin, sondern mit der Reinheit der Seele und dem inneren Gleichgewicht, denn jede Unruhe im Geist spiegelt sich im Körper wider, ganz gleich wie stark ein Mensch erscheinen mag."
                );
            }

            if (family)
            {
                return PickRandom(
                    "Noch bevor Königreiche errichtet und Throne erhoben wurden, war die Familie der erste Kern jeder Zivilisation, aus dem die Werte hervorgingen, die unvergessliche Könige und Reiche formten.",

                    "Jede große Zivilisation begann in einem kleinen Haus, wo die ersten Prinzipien gepflanzt und die ersten Zeichen von Stärke und Zugehörigkeit geschaffen wurden, die Generationen überdauern.",

                    "Die Familie ist nicht nur ein Band aus Blut, sondern der Ursprung des menschlichen Fundaments, auf dem Nationen aufgebaut werden, Königreiche Stabilität finden und das Leben fortbesteht.",

                    "Im Inneren des Hauses werden Führer geboren und jene Seelen geformt, die die Verantwortung für die Zukunft tragen, lange bevor sie einen Thron sehen oder die Stimmen der Macht hören."
                );
            }

            if (politics)
            {
                return PickRandom(
                    "Zu herrschen ist eine große Verantwortung, die den Schwachen nicht zusteht, denn sie ist ein schweres Bündnis, gegründet auf Gerechtigkeit vor Macht und Weisheit vor Autorität.",

                    "Ich sah Throne durch ein einziges wahres Wort entstehen und andere durch eine einzige Ungerechtigkeit zusammenbrechen, denn Worte in der Politik können die Geschichte einer Nation erschaffen oder auslöschen.",

                    "Macht ist kein Privileg, sondern eine wahre Prüfung des Menschen vor sich selbst, bevor sie eine Prüfung vor seinem Volk ist; und wer darin ungerecht wird, verliert zuerst sich selbst und dann seinen Thron.",

                    "Wer auf dem Thron sitzt, herrscht nicht allein, sondern trägt eine ganze Nation auf seinen Schultern und steht jeden Tag vor der Waage der Geschichte ohne Schutz außer seiner Gerechtigkeit."
                );
            }

            if (temples)
            {
                return PickRandom(
                    "Zwischen den heiligen Säulen, die den Himmel umarmen, stehen die Tempel als lebendige Erinnerung an eine Zivilisation, die niemals starb, sondern noch immer im Schweigen des Steins atmet.",

                    "Jede Gravur an den Tempelwänden ist nicht bloß Schmuck, sondern eine vollständige Geschichte von Königen, Göttern und Zeitaltern, die einen Ruhm erschufen, der niemals ausgelöscht werden kann.",

                    "Unter den Schatten des heiligen Steins lasen die Priester einst das Universum selbst, als wären die Tempel ein offenes Buch voller Geheimnisse der Ewigkeit.",

                    "Die Tempel wurden nicht errichtet, um schweigende Steine zu sein, sondern um eine Brücke zwischen Erde und Himmel zu bilden, zwischen dem Menschen und dem, was er für unmöglich hält."
                );
            }

            return PickRandom(
                "Wenn du die Wahrheit verstehen willst, höre zu wie der Weise auf die Inschriften der alten Zeiten hört, denn jede Gravur trägt ein Geheimnis, das die Zeit nur denen bewahrt hat, die es verstehen können.",

                "Nicht alles wird in Eile erfasst; manche Bedeutungen zeigen sich nur jenen, die mit bedachtem Geist und scharfem Blick durch die Überreste vergangener Epochen wandeln.",

                "Ich werde dich auf den Weg führen, doch wisse: Wissen wird nicht nur durch Worte erlangt, sondern durch tiefes Verständnis, so wie Tempelpriester die verborgenen Geheimnisse ihrer Heiligtümer deuten.",

                "Die Wahrheit wird nicht gegeben; sie wird enthüllt, wie die Geheimnisse der Könige aus alten Schriftrollen für jene offenbart werden, die über die Symbole hinaus lesen können."
            );
        }


        // =================================================
        // CHINESE
        // =================================================

        if (lang == "cn")
        {
            if (war)
            {
                return PickRandom(
                    "我曾站在伟大的军队面前，战士脚下的沙土都为之震颤，铁器碰撞的声音与历史的回响交织在一起，唯有那些以不可摧毁的力量写下名字的人才能被岁月铭记。",

                    "战争从来不是数字能够诉说的，也不是胜负能够概括的，它们是一段段由鲜血与勇气书写的篇章，塑造了至今仍留有痕迹的王国。",

                    "我曾看见旗帜在无尽的沙漠上升起，仿佛古代国王与骑士的灵魂归来，只为见证那些唯有意志坚定之人才能存活的时刻。",

                    "真正的荣耀诞生于古老战场之上，在那里，人类要面对自己的命运，而唯有拥有王者之心与不屈战士之魂的人才能永远被铭记。"
                );
            }

            if (health)
            {
                return PickRandom(
                    "在国王的宫殿中，我明白了健康并非短暂的恩赐，而是一顶无形的王冠，决定着一个人统治、生活与生存的能力。",

                    "在智者与祭司之间，我认识到身体并不仅仅是物质，而是一座与灵魂和宇宙相连的神圣殿堂，只有真正理解古代智慧的人才能领悟其中的平衡。",

                    "古人的医学从不仅仅是药方，而是一种完整的人生哲学，它将人类视为自然、星辰与命运的一部分。",

                    "身体的治愈不仅始于药物，更始于灵魂的纯净与内心的平衡，因为精神中的每一次动荡都会映射到身体之上，无论一个人看起来多么强大。"
                );
            }

            if (family)
            {
                return PickRandom(
                    "在王国建立、王座升起之前，家庭便是所有文明最初的核心，从那里诞生了塑造伟大国王与文明的价值观。",

                    "每一个伟大的文明都始于一个小小的家庭，在那里播下最初的原则，形成延续数代的力量与归属感。",

                    "家庭不仅仅是血缘的联系，更是人类社会的根基，国家由此建立，王国由此稳定，生命也因此得以延续。",

                    "领袖诞生于家中，那些能够承担未来责任的灵魂，在见到王座与听到权力之声之前便已形成。"
                );
            }

            if (politics)
            {
                return PickRandom(
                    "统治是一项伟大的责任，弱者无法承担，因为它首先建立在正义之上，其次才是力量；建立在智慧之上，其次才是权威。",

                    "我见过因一句真诚的话而建立的王座，也见过因一次不公而崩塌的王国，因为政治中的一句话足以创造或抹去一个民族的历史。",

                    "权力并非特权，而是一个人在面对自己时真正的考验；一旦在其中失去公正，人首先失去的是自己，其次才是王座。",

                    "坐在王座上的人并非独自统治，他肩负着整个民族，每一天都站在历史的天平前，唯一能够保护他的只有公正。"
                );
            }

            if (temples)
            {
                return PickRandom(
                    "在拥抱天空的神圣石柱之间，神庙如同一段活着的记忆，诉说着一个从未真正消亡、仍在石头沉默中呼吸的文明。",

                    "神庙墙壁上的每一道雕刻都不仅仅是装饰，而是关于国王、神明与时代的完整故事，铸造了永不磨灭的辉煌。",

                    "在神圣石影之下，祭司们曾解读整个宇宙，仿佛神庙本身就是一本揭示永恒秘密的书籍。",

                    "神庙并非为了成为沉默的石头而建造，而是为了成为连接大地与天空、人类与他们认为不可能之物之间的桥梁。"
                );
            }

            return PickRandom(
                "如果你想理解真相，就像智者聆听古代铭文一样去倾听，因为每一道刻痕都承载着岁月为能理解之人保留的秘密。",

                "并非所有事物都能匆忙理解；有些意义，只有那些以沉思之心和洞察之眼行走于古代遗迹之间的人才能看见。",

                "我会引导你走上道路，但要明白，知识不仅仅通过言语获得，而是通过深刻的理解，就如神庙祭司解读圣所中的隐藏奥秘。",

                "真相不是被给予的，而是被揭示的，如同王者的秘密从古老卷轴中显现，只为那些能够读懂符号之外含义的人。"
            );
        }

        return "";
    }


    // =====================================================
    // SERVER AUDIO REQUEST CLASS
    // =====================================================

    class ServerAudioRequest
    {
        public bool isDone = false;
        public bool hasError = false;
        public string error = "";
        public string filePath = "";
    }
}
